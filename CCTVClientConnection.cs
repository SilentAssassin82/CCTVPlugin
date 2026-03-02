using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CCTVCommon;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Torch.API;
using VRage.Game.ModAPI;
using VRageMath;

namespace CCTVPlugin
{
	/// <summary>
	/// Represents a single CCTVCapture connection with its own TCP listener and camera management.
	/// Multiple instances can run simultaneously for multi-client support.
	/// </summary>
	public class CCTVClientConnection : IDisposable
	{
		private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

		private readonly CCTVClientInstanceConfig _config;
		private readonly CCTVPluginConfig _sharedConfig;
		private readonly ITorchBase _torch;

		private TcpListener _listener;
		private TcpClient _client;
		private NetworkStream _stream;
		private Thread _listenerThread;
		private volatile bool _isRunning;

		// Per-client camera state
		private readonly List<CameraInfo> _cameras = new List<CameraInfo>();
		private int _currentCameraIndex = 0;
		private long _currentCameraEntityId = 0;

		// Tracks the camera list last sent to CCTVCapture so we only push an
		// update when the list actually changes, not on every periodic rescan.
		private string _lastSentCameraListSignature = null;

		// Auto-cycling state
		private int _cameraCycleTicks = 0;
		private readonly int _cameraCycleIntervalTicks; // Configured baseline (ticks)
		private int _dynamicCycleIntervalTicks;          // Auto-adjusted at runtime

		// Settle-time measurement: time from GOTO sent → first frame received
		// Used to auto-tune _dynamicCycleIntervalTicks and pre-TP lead time.
		private DateTime _lastCameraSwitchTime = DateTime.MinValue;
		private bool _awaitingFirstFrameAfterSwitch = false;
		private float _settleTimeEwmaMs = 3000f;  // Conservative start (3s); tightens after observations
		private int _settleTimeObservations = 0;
		private const float SETTLE_EWMA_ALPHA = 0.25f;   // Blend rate: 0.25 = responds in ~4 cycles
		private const float SETTLE_SAFETY_FACTOR = 1.5f; // Cycle must be 1.5× observed settle time
		private const int MIN_CYCLE_TICKS = 120;          // 2s floor
		private const int MAX_CYCLE_TICKS = 3600;         // 60s ceiling
		private const int MIN_OBSERVATIONS = 3;           // Observations before auto-adjust kicks in

		// Pre-emptive teleport: send GOTO to the next camera before display switches,
		// hiding TP latency inside the current camera's display window.
		private bool _preTeleportSent = false;
		private int _nextCameraIndexForPreTP = -1;

		// Manual mode: set by Next/Prev buttons to pause auto-cycling.
		// Cleared by ResetAutoCycle() so the player can re-enable normal cycling.
		private bool _isManualMode = false;

		// Frame queue - decoded frames ready to write to LCDs (on game thread)
		private readonly Queue<(int width, int height, string decodedContent, bool isColor)> _frameQueue = new Queue<(int, int, string, bool)>();
		private readonly object _frameQueueLock = new object();

		// LCD panel cache — avoids per-frame entity scans.
		// Invalidated by InvalidateLcdCache() on every camera rescan.
		private readonly Dictionary<string, IMyTextPanel> _lcdCache =
		new Dictionary<string, IMyTextPanel>(StringComparer.OrdinalIgnoreCase);
		private Dictionary<string, List<IMyTextPanel>> _cachedSlavesByQuad;
		private string _cachedSlavesKey;

		// Proximity gate: skip LCD writes when no players are nearby.
		// CCTVCapture keeps streaming; frames are drained and discarded until a player returns.
		private bool _anyPlayerNearby = true;       // optimistic default so LCDs start active
		private bool _wasPlayerNearby = true;        // tracks last logged state
		private int _proximityCheckTicks = 0;
		private const int PROXIMITY_CHECK_INTERVAL = 300; // ticks between checks (~5 s at 60 TPS)

		// Connection state
		public bool IsConnected => _client != null && _client.Connected;
		public string Name => _config.Name;
		public int Port => _config.TcpPort;
		public ulong SteamId => _config.SpectatorSteamId;
		public int CameraCount => _cameras.Count;
		public string LiveFeedLcdName => _config.LiveFeedLcdName;

		// 🔍 DIAGNOSTIC: Expose queue state
		public int QueuedFrames
		{
			get
			{
				lock (_frameQueueLock)
				{
					return _frameQueue.Count;
				}
			}
		}
		public int UpdateCalls => _updateCallCount;
		public int MessagesSent => _messagesSent;

		public CCTVClientConnection(CCTVClientInstanceConfig config, CCTVPluginConfig sharedConfig, ITorchBase torch)
		{
			_config = config ?? throw new ArgumentNullException(nameof(config));
			_sharedConfig = sharedConfig ?? throw new ArgumentNullException(nameof(sharedConfig));
			_torch = torch ?? throw new ArgumentNullException(nameof(torch));

			// Calculate camera cycle interval (default 10 seconds = 600 ticks)
			int cycleSeconds = sharedConfig?.CameraCycleIntervalSeconds ?? 10;
			_cameraCycleIntervalTicks = cycleSeconds * 60;
			_dynamicCycleIntervalTicks = _cameraCycleIntervalTicks; // starts at configured value
		}

		/// <summary>
		/// Start the TCP listener for this client instance.
		/// </summary>
		public void Start()
		{
			Log.Info($"🔧 [{Name}] Start() called - checking if already running...");

			if (_isRunning)
			{
				Log.Warn($"[{Name}] Already running on port {Port}");
				return;
			}

			try
			{
				Log.Info($"🔧 [{Name}] Setting _isRunning = true");
				_isRunning = true;

				Log.Info($"🔧 [{Name}] Creating TcpListener on port {Port}...");
				_listener = new TcpListener(IPAddress.Any, Port);

				Log.Info($"🔧 [{Name}] Calling listener.Start()...");
				_listener.Start();

				Log.Info($"🔧 [{Name}] Creating listener thread...");
				_listenerThread = new Thread(ListenForClients)
				{
					IsBackground = true,
					Name = $"CCTVCapture-{Name}-Listener"
				};

				Log.Info($"🔧 [{Name}] Starting listener thread...");
				_listenerThread.Start();

				Log.Info($"✅ [{Name}] TCP listener started on port {Port} (Prefix: {_config.CameraPrefix})");
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"❌ [{Name}] Failed to start TCP listener on port {Port}");
				_isRunning = false;
			}
		}

		/// <summary>
		/// Stop the TCP listener and disconnect client.
		/// </summary>
		public void Stop()
		{
			if (!_isRunning)
				return;

			_isRunning = false;

			try
			{
				_stream?.Close();
				_client?.Close();
				_listener?.Stop();
				Log.Info($"🛑 [{Name}] TCP listener stopped");
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error stopping [{Name}] listener");
			}
		}

		/// <summary>
		/// Check if this client instance should handle a specific camera.
		/// </summary>
		public bool ShouldHandleCamera(CameraInfo camera)
		{
			return _config.ShouldHandleCamera(camera.DisplayName, camera.FactionTag);
		}

		/// <summary>
		/// Update the camera list for this client instance.
		/// </summary>
		public void UpdateCameras(List<CameraInfo> allCameras)
		{
			InvalidateLcdCache();
			_cameras.Clear();
			_cameras.AddRange(allCameras.Where(ShouldHandleCamera));

			Log.Info($"[{Name}] Updated camera list: {_cameras.Count} cameras assigned (out of {allCameras.Count} total)");

			// Log which cameras this connection is handling
			foreach (var cam in _cameras)
			{
				Log.Debug($"[{Name}] - Camera: {cam.DisplayName} (Faction: {cam.FactionTag ?? "None"})");
			}

			// Debug: Log config settings
				Log.Debug($"[{Name}] Config: CameraPrefix='{_config.CameraPrefix}', FactionTag='{_config.FactionTag ?? "None"}'");

				// Re-pin _currentCameraIndex to the same physical camera after list rebuild.
				// Without this, adding or removing a camera mid-game shifts the interleaved
				// sort order and _currentCameraIndex points to the wrong camera on the next cycle.
				if (_currentCameraEntityId != 0)
				{
					int newIndex = _cameras.FindIndex(c => c.EntityId == _currentCameraEntityId);
					if (newIndex >= 0)
					{
						_currentCameraIndex = newIndex;
						Log.Debug($"[{Name}] Re-pinned camera index to {_currentCameraIndex} after rescan (EntityId: {_currentCameraEntityId})");
					}
					else
					{
						// Current camera is no longer in this connection's list — reset so cycling restarts cleanly
						Log.Info($"[{Name}] Current camera (EntityId: {_currentCameraEntityId}) no longer in list after rescan, resetting index");
						_currentCameraIndex = 0;
						_currentCameraEntityId = 0;
					}
				}

				// ⚡ If a client is connected, send the camera list only when it changed.
				// Sending on every rescan floods CCTVCapture with CAMERAS/CAMERA lines
				// even when nothing is different, creating log noise and wasted bandwidth.
			if (IsConnected)
			{
				string newSig = BuildCameraListSignature();
				if (newSig != _lastSentCameraListSignature)
				{
					Log.Info($"[{Name}] 📤 Camera list changed — sending to client ({_cameras.Count} cameras)");
					SendCameraListToClient();
					_lastSentCameraListSignature = newSig;
				}

				// If we have cameras and aren't on one yet, auto-switch to first camera
				if (_cameras.Count > 0 && _currentCameraEntityId == 0)
				{
					Log.Info($"[{Name}] 🎬 Auto-switching to first camera: {_cameras[0].DisplayName}");
					_currentCameraIndex = 0;
					_currentCameraEntityId = _cameras[0].EntityId;
					Send($"CAMERA 1");
					Send("SPECTATOR");
					TeleportToCamera(_cameras[0]);
				}
			}
		}

		/// Returns a string that uniquely represents the current camera list.
		/// Changes when cameras are added, removed, or reordered.
		private string BuildCameraListSignature()
		{
			return string.Join(",", _cameras.Select(c => c.EntityId.ToString()));
		}

		/// <summary>
		/// Send the current camera list to the connected CCTVCapture.
		/// </summary>
		private void SendCameraListToClient()
		{
			if (!IsConnected)
				return;

			Send($"CAMERAS {_cameras.Count}");
			foreach (var camera in _cameras)
			{
				Send($"CAMERA {camera.DisplayName}");
			}
		}

		// Diagnostic: Track sent messages
		private int _messagesSent = 0;

		/// <summary>
		/// Send a command to the connected CCTVCapture.
		/// </summary>
		public void Send(string message)
		{
			if (!IsConnected || _stream == null)
			{
				Log.Warn($"[{Name}] ⚠️ Cannot send '{message}' - not connected (stream null: {_stream == null})");
				return;
			}

			try
			{
				byte[] data = Encoding.UTF8.GetBytes(message + "\n");
				_stream.Write(data, 0, data.Length);
				_messagesSent++;

				// Log important messages
				if (!message.StartsWith("CAMERA") || (_messagesSent % 10) == 0)
				{
					Log.Debug($"[{Name}] >> {message} (total sent: {_messagesSent})");
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"[{Name}] Error sending message: {message}");
			}
		}

		private void ListenForClients()
		{
			Log.Info($"[{Name}] Listening for CCTVCapture connections on port {Port}...");

			while (_isRunning)
			{
				try
				{
					if (_listener.Pending())
					{
						var candidate = _listener.AcceptTcpClient();

						candidate.ReceiveBufferSize = 1024 * 1024;
						candidate.SendBufferSize = 1024 * 1024;
						candidate.NoDelay = true;

						var candidateStream = candidate.GetStream();

						// --- HMAC challenge-response handshake ---
						// Generate a random nonce, send it with HELLO, expect AUTH <hmac>
						string nonce = Guid.NewGuid().ToString("N");
						byte[] nonceBytes = Encoding.UTF8.GetBytes(nonce);
						byte[] keyBytes = Encoding.UTF8.GetBytes(BuildToken.Value);
						string expectedHmac;
						using (var hmac = new HMACSHA256(keyBytes))
							expectedHmac = Convert.ToBase64String(hmac.ComputeHash(nonceBytes));

						byte[] helloBytes = Encoding.UTF8.GetBytes($"HELLO {Name} v1.0 CHALLENGE:{nonce}\n");
						candidateStream.Write(helloBytes, 0, helloBytes.Length);
						candidateStream.Flush();

						// Read AUTH response (with 5-second timeout)
						candidate.ReceiveTimeout = 5000;
						string authLine = null;
						try
						{
							using (var tempReader = new StreamReader(candidateStream, Encoding.UTF8, false, 256, leaveOpen: true))
								authLine = tempReader.ReadLine();
						}
						catch (Exception)
						{
							Log.Warn($"[{Name}] ⛔ Client failed to respond to auth challenge — disconnected");
							candidate.Close();
							continue;
						}
						finally
						{
							candidate.ReceiveTimeout = 0; // reset to infinite
						}

						if (authLine == null || !authLine.StartsWith("AUTH ") || authLine.Substring(5).Trim() != expectedHmac)
						{
							Log.Warn($"[{Name}] ⛔ Auth failed — unexpected or missing AUTH response — disconnected");
							candidate.Close();
							continue;
						}

						Log.Info($"[{Name}] ✅ Auth passed — CCTVCapture verified");
						// --- end handshake ---

						_client = candidate;
						_stream = candidateStream;

						Log.Info($"✅ [{Name}] CCTVCapture connected from {_client.Client.RemoteEndPoint}");

						// Send welcome message
						Send($"HELLO {Name} v1.0");

						// 🔍 DIAGNOSTIC: Send camera list immediately if we have cameras
						if (_cameras.Count > 0)
						{
							Log.Info($"[{Name}] 📤 Sending camera list immediately ({_cameras.Count} cameras)");
							SendCameraListToClient();
							_lastSentCameraListSignature = BuildCameraListSignature();

							// Auto-switch to first camera
							_currentCameraIndex = 0;
							_currentCameraEntityId = _cameras[0].EntityId;
							Send($"CAMERA 1");
							Send("SPECTATOR");
							Log.Info($"[{Name}] 🎬 Auto-switched to camera: {_cameras[0].DisplayName}");
							TeleportToCamera(_cameras[0]);
						}
						else
						{
							Log.Warn($"[{Name}] ⚠️ No cameras available yet - client will receive list on next scan");
						}

						// Trigger immediate camera scan by resetting scan ticks
						// This ensures new clients get camera list quickly instead of waiting 2 seconds
						Log.Debug($"[{Name}] Triggering immediate camera rescan for new client");

						// Start handling client messages on a separate thread
						Thread clientThread = new Thread(HandleClientMessages)
						{
							IsBackground = true,
							Name = $"CCTVCapture-{Name}-Handler"
						};
						clientThread.Start();
					}

					Thread.Sleep(100);
				}
				catch (Exception ex)
				{
					if (_isRunning)
					{
						Log.Error(ex, $"[{Name}] Error in listener thread");
					}
				}
			}

			Log.Info($"[{Name}] Listener thread exiting");
		}

		/// <summary>
		/// Handle incoming messages from the connected CCTVCapture.
		/// Runs on dedicated background thread per client.
		/// </summary>
		private void HandleClientMessages()
		{
			try
			{
				// 🔧 CRITICAL FIX: Use larger buffer for StreamReader to handle 500KB FRAME messages
				using (StreamReader reader = new StreamReader(_stream, Encoding.UTF8, false, 1024 * 1024)) // 1 MB buffer
				{
					string line;
					while (_isRunning && _client != null && _client.Connected && (line = reader.ReadLine()) != null)
					{
						try
						{
							ProcessClientMessage(line.Trim());
						}
						catch (Exception ex)
						{
							Log.Error(ex, $"[{Name}] Error processing message: {line?.Substring(0, Math.Min(100, line?.Length ?? 0))}...");
						}
					}
				}

				Log.Info($"[{Name}] Client disconnected");
			}
			catch (IOException ex) when (ex.InnerException is SocketException)
			{
				Log.Info($"[{Name}] Client disconnected (connection forcibly closed)");
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"[{Name}] Error in client handler thread");
			}
			finally
			{
				_client?.Close();
				_client = null;
				_stream = null;
				_lastSentCameraListSignature = null; // force full list re-send on next connection
			}
		}

		/// <summary>
		/// Process a single message from the CCTVCapture.
		/// </summary>
		private void ProcessClientMessage(string message)
		{
			if (string.IsNullOrEmpty(message))
				return;

			// Log all incoming messages for debugging (except FRAME which is huge)
			if (!message.StartsWith("FRAME "))
			{
				Log.Debug($"[{Name}] << {message}");
			}
			else
			{
				Log.Debug($"[{Name}] << FRAME command received ({message.Length} bytes)");
			}

			// PING command
			if (message == "PING")
			{
				Send("PONG");
				Send("READY");
				return;
			}

			// GETCONFIG command
			if (message == "GETCONFIG")
			{
				string config = $"CONFIG CaptureWidth={_sharedConfig.CaptureWidth} CaptureHeight={_sharedConfig.CaptureHeight} " +
							   $"CaptureFps={_sharedConfig.CaptureFps} UseColorMode={_sharedConfig.UseColorMode} " +
							   $"UseDithering={_sharedConfig.UseDithering} PostProcessMode={_sharedConfig.PostProcessMode} " +
							   $"LcdGridResolution={_sharedConfig.LcdGridResolution}";
				Send(config);
				return;
			}

			// LISTCAMERAS command
			if (message == "LISTCAMERAS")
			{
				// If camera list is empty, trigger a rescan
				if (_cameras.Count == 0)
				{
					Log.Debug($"[{Name}] Camera list empty, will be populated on next rescan");
				}

				Send($"CAMERAS {_cameras.Count}");
				foreach (var camera in _cameras)
				{
					Send($"CAMERA {camera.DisplayName}");
				}
				return;
			}

			// CAMERA command - switch to specific camera by index or name
			if (message.StartsWith("CAMERA "))
			{
				string arg = message.Substring(7).Trim();

				// Try parse as index
				if (int.TryParse(arg, out int camIndex))
				{
					camIndex--; // Convert 1-based to 0-based
					if (camIndex >= 0 && camIndex < _cameras.Count)
					{
						_currentCameraIndex = camIndex;
						_currentCameraEntityId = _cameras[camIndex].EntityId;

						var cam = _cameras[camIndex];
						Send($"OK Switched to {cam.DisplayName}");

						Log.Info($"[{Name}] Switched to camera {camIndex + 1}/{_cameras.Count}: {cam.DisplayName}");

						// Tell CCTVCapture.exe to re-enter spectator mode
						Send("SPECTATOR");

						// Teleport the character to the camera
						TeleportToCamera(cam);
					}
					else
					{
						Send($"ERROR Camera index {camIndex + 1} out of range (1-{_cameras.Count})");
					}
				}
				else
				{
					Send($"ERROR Invalid CAMERA argument");
				}

				return;
			}

			// FRAME command - decode and queue for LCD writing on game thread
			if (message.StartsWith("FRAME "))
			{
				// Parse: FRAME <width> <height> <mode> <base64data>
				string[] parts = message.Split(new[] { ' ' }, 5); // Changed to 5 to capture mode properly
				if (parts.Length >= 4)
				{
					if (int.TryParse(parts[1], out int width) && int.TryParse(parts[2], out int height))
					{
						string mode = parts[3];

						// Extract base64 data after "FRAME W H MODE "
						int dataStartIndex = message.IndexOf(mode) + mode.Length + 1;
						if (dataStartIndex < message.Length)
						{
							string base64Data = message.Substring(dataStartIndex);

							Log.Debug($"[{Name}] 🔍 FRAME parsing: {width}×{height} {mode}, base64 length: {base64Data.Length}");

							// Decode on background thread
							try
							{
								byte[] bytes = Convert.FromBase64String(base64Data);
								string decodedContent;

								if (mode.EndsWith("GZ"))
								{
									using (var ms = new MemoryStream(bytes))
									using (var gz = new GZipStream(ms, CompressionMode.Decompress))
									// Pre-allocate ~4× the compressed size to avoid repeated doublings.
									// GetBuffer() is used below to skip the extra ToArray() copy.
									using (var outMs = new MemoryStream(bytes.Length * 4))
									{
										gz.CopyTo(outMs);
										decodedContent = Encoding.UTF8.GetString(outMs.GetBuffer(), 0, (int)outMs.Length);
									}
								}
								else
								{
									decodedContent = Encoding.UTF8.GetString(bytes);
								}

								bool isColor = mode.Contains("COLOR");

								// Queue for game thread processing
								lock (_frameQueueLock)
								{
									_frameQueue.Enqueue((width, height, decodedContent, isColor));

									// Limit queue size to prevent memory issues
									while (_frameQueue.Count > 5)
									{
										_frameQueue.Dequeue();
										Log.Warn($"[{Name}] ⚠️ Frame queue overflow - dropped oldest frame");
									}
								}

								// Settle-time measurement: first frame back after a camera switch
								// tells us how long GOTO→capture takes. Used to auto-tune cycle interval.
								if (_awaitingFirstFrameAfterSwitch && _lastCameraSwitchTime != DateTime.MinValue)
								{
									_awaitingFirstFrameAfterSwitch = false;
									float settleMs = (float)(DateTime.UtcNow - _lastCameraSwitchTime).TotalMilliseconds;

									_settleTimeObservations++;
									if (_settleTimeObservations == 1)
										_settleTimeEwmaMs = settleMs; // seed with first sample
									else
										_settleTimeEwmaMs = _settleTimeEwmaMs * (1f - SETTLE_EWMA_ALPHA) + settleMs * SETTLE_EWMA_ALPHA;

									// Auto-adjust only after enough observations to trust the EWMA.
									// Floor is the configured interval — auto-tune can only extend the cycle
									// when settle is slow, never shrink it below the user's setting.
									if (_settleTimeObservations >= MIN_OBSERVATIONS)
									{
										int newTicks = (int)(_settleTimeEwmaMs / 1000f * 60f * SETTLE_SAFETY_FACTOR);
										int oldTicks = _dynamicCycleIntervalTicks;
										_dynamicCycleIntervalTicks = Math.Max(_cameraCycleIntervalTicks, Math.Min(MAX_CYCLE_TICKS, newTicks));

										if (Math.Abs(oldTicks - _dynamicCycleIntervalTicks) > 30) // >0.5s change
											Log.Info($"[{Name}] 📊 Settle: {settleMs:F0}ms (EWMA {_settleTimeEwmaMs:F0}ms) → cycle {_dynamicCycleIntervalTicks / 60f:F1}s");
									}
								}

								Log.Debug($"[{Name}] ✅ Queued FRAME {width}×{height} {mode} ({decodedContent.Length} chars, queue: {_frameQueue.Count})");
							}
							catch (FormatException ex)
							{
								Log.Error(ex, $"[{Name}] ❌ Failed to decode base64 frame (invalid format)");
							}
							catch (Exception ex)
							{
								Log.Error(ex, $"[{Name}] ❌ Failed to decode frame");
							}
						}
						else
						{
							Log.Warn($"[{Name}] ⚠️ FRAME command missing data");
						}
					}
					else
					{
						Log.Warn($"[{Name}] ⚠️ FRAME command invalid dimensions: '{parts[1]}' × '{parts[2]}'");
					}
				}
				else
				{
					Log.Warn($"[{Name}] ⚠️ FRAME command malformed (parts: {parts.Length})");
				}
				return;
			}

			Log.Debug($"[{Name}] Unknown command: {message}");
		}

		// Diagnostic: Track update calls
		private int _updateCallCount = 0;
		private int _lastLoggedUpdateCount = 0;

		/// <summary>
		/// Update method - called each game tick to process queued frames and write to LCDs.
		/// MUST be called from game thread for SE API compatibility.
		/// </summary>
		public void Update()
		{
			_updateCallCount++;

			// Log update calls every 300 ticks (5 seconds) when connected
			if (IsConnected && (_updateCallCount % 300) == 0 && _updateCallCount != _lastLoggedUpdateCount)
			{
				_lastLoggedUpdateCount = _updateCallCount;
				lock (_frameQueueLock)
				{
					Log.Info($"[{Name}] 🔄 Update() heartbeat: {_updateCallCount} calls, Queue: {_frameQueue.Count} frames, Camera: {_currentCameraIndex + 1}/{_cameras.Count}");
				}
			}

			if (!IsConnected)
				return;

			// Auto-cycle cameras if enabled and not overridden by a manual button press
			if (_sharedConfig.EnableAutoCameraCycling && _cameras.Count > 0 && !_isManualMode)
			{
				_cameraCycleTicks++;

				// Pre-emptive teleport: send GOTO to the next camera before the display switches.
				// This pipelines the TP latency into the current camera's display window so that
				// by the time we switch, the spectator is already positioned and a frame comes back fast.
				if (!_preTeleportSent && _cameras.Count > 1)
				{
					// Lead time = observed settle EWMA (at least 30 ticks / 0.5s, at most half the cycle)
					int leadTicks = (int)(_settleTimeEwmaMs / 1000f * 60f);
					leadTicks = Math.Max(30, Math.Min(leadTicks, _dynamicCycleIntervalTicks / 2));

					if (_cameraCycleTicks >= _dynamicCycleIntervalTicks - leadTicks)
					{
						int nextIndex = (_currentCameraIndex + 1) % _cameras.Count;
						_nextCameraIndexForPreTP = nextIndex;
						_preTeleportSent = true;
						TeleportToCamera(_cameras[nextIndex]);
						Log.Debug($"[{Name}] ⏩ Pre-TP camera {nextIndex + 1} ({leadTicks} ticks early, EWMA {_settleTimeEwmaMs:F0}ms)");
					}
				}

				if (_cameraCycleTicks >= _dynamicCycleIntervalTicks)
				{
					_cameraCycleTicks = 0;

					// Skip redundant teleport if pre-TP already sent to the correct next camera
					int expectedNext = (_currentCameraIndex + 1) % _cameras.Count;
					bool tpAlreadySent = _preTeleportSent && _nextCameraIndexForPreTP == expectedNext;

					_preTeleportSent = false;
					_nextCameraIndexForPreTP = -1;

					CycleToNextCamera(teleportAlreadySent: tpAlreadySent);
				}
			}

			// Process all queued frames
			if (++_proximityCheckTicks >= PROXIMITY_CHECK_INTERVAL)
			{
				_proximityCheckTicks = 0;
				CheckPlayerProximity();
			}

			int processedCount = 0;
			while (true)
			{
				(int width, int height, string content, bool isColor) frame;

				lock (_frameQueueLock)
				{
					if (_frameQueue.Count == 0)
						break;

					frame = _frameQueue.Dequeue();
				}

				processedCount++;
				if (_anyPlayerNearby)
					WriteFrameToLCDs(frame.width, frame.height, frame.content, frame.isColor);
			}

			if (processedCount > 0)
			{
				Log.Debug($"[{Name}] ⚙️ Processed {processedCount} frames from queue");
			}
		}

		/// <summary>
		/// Auto-cycle to the next camera in the list.
		/// teleportAlreadySent: pass true when a pre-emptive GOTO was already sent for this camera
		/// so we skip the duplicate TeleportToCamera call.
		/// </summary>
		private void CycleToNextCamera(bool teleportAlreadySent = false)
		{
			if (_cameras.Count == 0)
			{
				Log.Debug($"[{Name}] No cameras available for cycling");
				return;
			}

			// Move to next camera
			_currentCameraIndex = (_currentCameraIndex + 1) % _cameras.Count;
			var nextCamera = _cameras[_currentCameraIndex];

			_currentCameraEntityId = nextCamera.EntityId;

			Log.Info($"[{Name}] 🎬 Auto-cycling to camera {_currentCameraIndex + 1}/{_cameras.Count}: {nextCamera.DisplayName}" +
					 (teleportAlreadySent ? " (TP already sent)" : ""));

			// Send camera switch command to CCTVCapture.exe
			Send($"CAMERA {_currentCameraIndex + 1}");

			// Tell CCTVCapture.exe to re-enter spectator mode
			Send("SPECTATOR");

			// Only teleport if we haven't already pre-teleported to this camera
			if (!teleportAlreadySent)
				TeleportToCamera(nextCamera);

			// Mark switch time so the first returning frame can measure settle latency
			_lastCameraSwitchTime = DateTime.UtcNow;
			_awaitingFirstFrameAfterSwitch = true;
		}

		/// <summary>
		/// Teleport the fake client character to a camera's position.
		/// Actually sends a multiplayer message to the client-side mod to move the spectator camera.
		/// The character body never moves - only the spectator camera view changes.
		/// </summary>
		private void TeleportToCamera(CameraInfo camera)
		{
			if (SteamId == 0)
			{
				Log.Warn($"[{Name}] ⚠️ SpectatorSteamId is 0 — set it in CCTVPlugin.cfg or the instance config, TP/spectator cam will not work");
				return;
			}

			_torch.InvokeBlocking(() =>
			{
				try
				{
					var cameraEntity = MyAPIGateway.Entities.GetEntityById(camera.EntityId) as Sandbox.Game.Entities.MyCameraBlock;
					if (cameraEntity == null)
					{
						Log.Warn($"[{Name}] Camera entity {camera.EntityId} not found!");
						return;
					}

					Vector3D position = cameraEntity.WorldMatrix.Translation;
					Vector3D forward = cameraEntity.WorldMatrix.Forward;
					Vector3D up = cameraEntity.WorldMatrix.Up;

					// Send multiplayer message to client-side mod to move spectator camera
					// Format: GOTO|SteamID|CameraName|EntityID|X|Y|Z|FwdX|FwdY|FwdZ|UpX|UpY|UpZ
					var ic = CultureInfo.InvariantCulture;
					string gotoMessage = $"GOTO|{SteamId}|{camera.DisplayName}|{camera.EntityId}|" +
									$"{position.X.ToString(ic)}|{position.Y.ToString(ic)}|{position.Z.ToString(ic)}|" +
									$"{forward.X.ToString(ic)}|{forward.Y.ToString(ic)}|{forward.Z.ToString(ic)}|" +
									$"{up.X.ToString(ic)}|{up.Y.ToString(ic)}|{up.Z.ToString(ic)}";

					byte[] data = System.Text.Encoding.UTF8.GetBytes(gotoMessage);
					const ushort MESSAGE_ID = 12346;
					MyAPIGateway.Multiplayer.SendMessageToOthers(MESSAGE_ID, data);

					Log.Info($"[{Name}] 📡 Sent GOTO message to mod for camera '{camera.DisplayName}' (SteamID: {SteamId})");
				}
				catch (Exception ex)
				{
					Log.Error(ex, $"[{Name}] Failed to send camera position to mod");
				}
			});
		}

		/// <summary>
		/// Write decoded frame to LCDs based on configured LiveFeedLcdName.
		/// Supports both single LCD and 2×2 grid patterns.
		/// MUST be called from game thread for SE API compatibility.
		/// </summary>
		private void WriteFrameToLCDs(int width, int height, string content, bool isColor)
		{
			// Already on game thread via Update() — InvokeBlocking not needed here
			try
			{
				// Use LiveFeedLcdName if configured; otherwise fall back to the active camera's
				// DisplayName so LCDs named "LCD_TV Test01" work without extra config.
				string baseName;
				if (!string.IsNullOrEmpty(_config.LiveFeedLcdName))
				{
					baseName = _config.LiveFeedLcdName;
				}
				else if (_cameras.Count > 0 && _currentCameraIndex >= 0 && _currentCameraIndex < _cameras.Count)
				{
					baseName = _cameras[_currentCameraIndex].DisplayName;
				}
				else
				{
					Log.Warn($"[{Name}] No LiveFeedLcdName configured and no active camera — frame dropped");
					return;
				}

				Log.Debug($"[{Name}] 🖥️ Writing {width}×{height} frame (BaseName: '{baseName}', Prefix: '{_config.LcdPrefix}')");

				if (width == _sharedConfig.LcdSingleResolution && height == _sharedConfig.LcdSingleResolution)
				{
					string singleLcdName = $"{_config.LcdPrefix} {baseName}";
					WriteSingleLCD(singleLcdName, content, isColor);
				}
				else if (width == _sharedConfig.LcdGridResolution && height == _sharedConfig.LcdGridResolution)
				{
					WriteGridLCDs(_config.LcdPrefix, baseName, content, isColor, width, height);
				}
				else
				{
					Log.Warn($"[{Name}] Unsupported resolution: {width}×{height} (expected {_sharedConfig.LcdSingleResolution} or {_sharedConfig.LcdGridResolution})");
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"[{Name}] Error writing frame to LCDs");
			}
		}

		/// <summary>
		/// Write to a single LCD panel.
		/// fontSizeOverride: when set, uses that exact size (e.g. GridFontSize for quadrant panels);
		/// when null, auto-calculates based on color mode and FontScale (for 181×181 single LCDs).
		/// LCDs that don't exist are silently skipped — players may have any combination of
		/// single panel, 2×2 grid, or both, so missing LCDs are never an error.
		/// </summary>
		private void WriteSingleLCD(string lcdName, string content, bool isColor, float? fontSizeOverride = null)
		{
			var lcd = FindLCDByName(lcdName, silent: true);
			if (lcd == null)
			{
				Log.Debug($"[{Name}] ⏭️ Skipping LCD '{lcdName}' - not present in world");
				return;
			}

			float fontSize;
			if (fontSizeOverride.HasValue)
			{
				// Grid quadrant panel: transparent LCDs need a larger font (1.12×) to
				// overlap the panel borders and hide the seams between the 4 panels.
				fontSize = fontSizeOverride.Value;
				if (IsTransparentLcd(lcd))
				{
					fontSize *= 1.12f;
					Log.Debug($"[{Name}] 🔍 Transparent LCD detected for '{lcdName}', adjusted fontSize: {fontSize:F3}");
				}
			}
			else
			{
				fontSize = CalculateAutoFontSize(isColor);
			}

			Log.Debug($"[{Name}] ✅ Writing to single LCD '{lcdName}' ({content.Length} chars, color: {isColor}, fontSize: {fontSize:F3})");
			WriteLCDContent(lcd, content, fontSize, isColor);
		}

		/// <summary>
		/// Detects if an LCD panel is a transparent type.
		/// Transparent LCDs have wider panel borders — they need a slightly larger font
		/// so characters overlap the borders and hide the seams in a 2×2 grid.
		/// </summary>
		private bool IsTransparentLcd(IMyTextPanel lcd)
		{
			try
			{
				var textPanel = lcd as Sandbox.Game.Entities.Blocks.MyTextPanel;
				if (textPanel == null) return false;

				var blockDef = textPanel.BlockDefinition;
				if (blockDef == null) return false;

				string subtype = blockDef.Id.SubtypeName ?? "";
				return subtype.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			catch (Exception ex)
			{
				Log.Debug($"[{Name}] Error detecting transparent LCD: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Auto-calculates the correct font size for a 181×181 single LCD panel,
		/// matching the legacy single-client logic (base 0.1f color / 0.2f gray, scaled by FontScale).
		/// </summary>
		private float CalculateAutoFontSize(bool isColor)
		{
			const float REFERENCE_SIZE = 178f; // Legacy reference resolution
			float lcdSize = _sharedConfig.LcdSingleResolution; // Actual frame resolution
			float baseFont = isColor ? 0.1f : 0.2f;
			float scale = REFERENCE_SIZE / lcdSize;
			float fontSize = baseFont * scale;
			if (!isColor) fontSize *= 1.05f;
			fontSize *= _sharedConfig.FontScale;
			return Math.Max(0.03f, Math.Min(0.35f, fontSize));
		}

		/// <summary>
		/// Write to 2×2 grid of LCDs (TL, TR, BL, BR).
		/// LCD naming: "LCD_TV Test01_TL", "LCD_TV Test01_TR", etc.
		/// </summary>
		private void WriteGridLCDs(string lcdPrefix, string baseName, string content, bool isColor, int width, int height)
		{
			Log.Debug($"[{Name}] 📐 Writing 2×2 grid (Prefix: '{lcdPrefix}', Base: '{baseName}')");

			int quadW = width / 2;
			int quadH = height / 2;

			// Split full grid content into 4 quadrants
			string[] lines = content.Split('\n');
			if (lines.Length < height)
			{
				Log.Warn($"[{Name}] ⚠️ Grid content has {lines.Length} lines, expected {height}");
				return;
			}

			string tlContent = ExtractQuadrant(lines, 0, 0, quadW, quadH);
			string trContent = ExtractQuadrant(lines, quadW, 0, quadW, quadH);
			string blContent = ExtractQuadrant(lines, 0, quadH, quadW, quadH);
			string brContent = ExtractQuadrant(lines, quadW, quadH, quadW, quadH);

			// Build full LCD names: "LCD_TV Test01_TL" format
			// Pattern: <Prefix><space><BaseName><Quadrant>
			string tlLcdName = $"{lcdPrefix} {baseName}_TL";
			string trLcdName = $"{lcdPrefix} {baseName}_TR";
			string blLcdName = $"{lcdPrefix} {baseName}_BL";
			string brLcdName = $"{lcdPrefix} {baseName}_BR";

			// Write to each LCD quadrant using GridFontSize (default 0.1f)
			float gridFontSize = _sharedConfig.GridFontSize;
			Log.Debug($"[{Name}] Writing TL quadrant to '{tlLcdName}' ({tlContent.Length} chars)");
			WriteSingleLCD(tlLcdName, tlContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing TR quadrant to '{trLcdName}' ({trContent.Length} chars)");
			WriteSingleLCD(trLcdName, trContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing BL quadrant to '{blLcdName}' ({blContent.Length} chars)");
			WriteSingleLCD(blLcdName, blContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing BR quadrant to '{brLcdName}' ({brContent.Length} chars)");
			WriteSingleLCD(brLcdName, brContent, isColor, gridFontSize);

			// Copy to slave LCDs
			CopyToSlaveLCDs(lcdPrefix, baseName);
		}

		/// <summary>
		/// Extract a quadrant from the full 362×362 content.
		/// startX, startY: top-left corner of quadrant in the full image
		/// width, height: dimensions of the quadrant to extract
		/// </summary>
		private string ExtractQuadrant(string[] lines, int startX, int startY, int width, int height)
		{
			// Pre-allocate buffer: width chars per line + newlines
			StringBuilder sb = new StringBuilder(width * height + height);

			for (int y = 0; y < height && (startY + y) < lines.Length; y++)
			{
				string line = lines[startY + y];

				// Extract horizontal slice from this line
				if (startX < line.Length)
				{
					int endX = Math.Min(startX + width, line.Length);
					int sliceWidth = endX - startX;
					sb.Append(line.Substring(startX, sliceWidth));

					// Pad with spaces if line was too short
					if (sliceWidth < width)
					{
						sb.Append(' ', width - sliceWidth);
					}
				}
				else
				{
					// Line was too short, pad entire line with spaces
					sb.Append(' ', width);
				}

				// Add newline between lines (but not after last line)
				if (y < height - 1)
					sb.Append('\n');
			}

			string result = sb.ToString();

			// Debug: Log quadrant stats
			if (startX == 0 && startY == 0)
			{
				Log.Debug($"[{Name}] ExtractQuadrant TL: extracted {result.Length} chars (expected ~{width * height + height - 1})");
			}

			return result;
		}

		/// <summary>
		/// Find LCD by custom name.
		/// MUST be called from game thread.
		/// Pass silent=true for speculative lookups (e.g. slave searches) to suppress WARN on miss.
		/// </summary>
		private IMyTextPanel FindLCDByName(string name, bool silent = false)
		{
			// Cache hit: return immediately if the panel is still in the world
			if (_lcdCache.TryGetValue(name, out var cached))
			{
				try { if (cached.CubeGrid != null) return cached; } catch { }
				_lcdCache.Remove(name); // Stale entry — fall through to full scan
			}

			IMyTextPanel foundLcd = null;
			int totalLcds = 0;

			// Use MyEntities (Torch internal) rather than MyAPIGateway.Entities so that
			// non-static (dynamic) grids — e.g. vehicles — are included in the scan.
			// MyAPIGateway.Entities can miss dynamic grids in the Torch plugin context.
			foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
			{
				if (grid == null) continue;
				foreach (var block in grid.GetFatBlocks())
				{
					var lcd = block as IMyTextPanel;
					if (lcd == null) continue;
					totalLcds++;
					if (lcd.CustomName == name)
					{
						foundLcd = lcd;
						Log.Debug($"[{Name}] ✅ FOUND LCD: '{name}' (Grid: {grid.DisplayName}, Static: {grid.IsStatic})");
						break;
					}
				}
				if (foundLcd != null) break;
			}

			if (foundLcd != null)
				_lcdCache[name] = foundLcd;

			if (foundLcd == null && !silent)
			{
				Log.Warn($"[{Name}] ❌ LCD NOT FOUND: '{name}' (scanned {totalLcds} total LCDs)");

				// Debug: List all available LCDs with a matching prefix to help diagnose naming issues
				if (totalLcds > 0)
				{
					Log.Debug($"[{Name}] 🔍 Listing LCDs with prefix '{_config.LcdPrefix}':");
					int count = 0;
					foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
					{
						if (grid == null) continue;
						foreach (var block in grid.GetFatBlocks())
						{
							var lcd = block as IMyTextPanel;
							if (lcd != null && (lcd.CustomName.Contains(_config.LcdPrefix) || lcd.CustomName.Contains("SLAVE")))
								Log.Debug($"[{Name}]    #{++count}: '{lcd.CustomName}' (Grid: {grid.DisplayName}, Static: {grid.IsStatic})");
						}
					}
				}
			}

			return foundLcd;
		}

		/// <summary>
		/// Write content to a specific LCD.
		/// </summary>
		private void WriteLCDContent(IMyTextPanel lcd, string content, float fontSize, bool isColor)
		{
			if (lcd == null)
				return;

			// Auto HUD mode: if the LCD is on a non-static (moving) grid, force a fully
			// transparent background so the feed overlays the pilot's view instead of
			// blocking it. Explicit LcdBackgroundAlpha config only applies to static grids.
			bool isOnDynamicGrid = false;
			try { isOnDynamicGrid = (lcd.CubeGrid as MyCubeGrid)?.IsStatic == false; }
			catch { }

			int effectiveAlpha = isOnDynamicGrid ? 0 : Math.Max(0, Math.Min(255, _config.LcdBackgroundAlpha));

			if (isOnDynamicGrid)
				Log.Debug($"[{Name}] 🚗 HUD mode: LCD '{lcd.CustomName}' is on a dynamic grid — transparent background applied");

			lcd.WriteText(content);
			lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
			lcd.Font = "Monospace";
			lcd.FontSize = fontSize;
			lcd.TextPadding = 0f;
			lcd.Alignment = VRage.Game.GUI.TextPanel.TextAlignment.LEFT;
			lcd.BackgroundColor = new Color(0, 0, 0, (byte)effectiveAlpha);

			// SE color chars encode their own color — white font lets them render correctly.
			// Grayscale uses the configured font tint instead.
			if (isColor)
				lcd.FontColor = new Color(255, 255, 255);
			else
				lcd.FontColor = ParseColor(_sharedConfig.LcdFontTint);
		}

		/// <summary>
		/// Copy master LCD content to slave LCDs.
		/// A slave is any LCD whose name starts with the master's name (case-insensitive) and
		/// contains "_slave" anywhere after it — e.g. "LCD_TV Test01_TL_Slave",
		/// "LCD_TV Test01_TL_SLAVE1", "LCD_TV Test01_TL_Slave_GridB", etc.
		/// Uses a single entity scan for all quadrants to avoid per-frame spam.
		/// </summary>
		private void CopyToSlaveLCDs(string lcdPrefix, string baseName)
		{
			string[] quadrants = { "_TL", "_TR", "_BL", "_BR" };
			string cacheKey = $"{lcdPrefix}|{baseName}";

			Dictionary<string, List<IMyTextPanel>> slavesByQuad;
			if (_cachedSlavesKey == cacheKey && _cachedSlavesByQuad != null)
			{
				slavesByQuad = _cachedSlavesByQuad;
			}
			else
			{
				// Build master name → quadrant tag lookup (case-insensitive keys)
				var masterPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (string quad in quadrants)
					masterPrefixes[$"{lcdPrefix} {baseName}{quad}"] = quad;

				slavesByQuad = new Dictionary<string, List<IMyTextPanel>>(StringComparer.OrdinalIgnoreCase);

				MyAPIGateway.Entities.GetEntities(null, entity =>
				{
					var grid = entity as MyCubeGrid;
					if (grid == null) return false;

					foreach (var block in grid.GetFatBlocks())
					{
						var lcd = block as IMyTextPanel;
						if (lcd == null) continue;

						string lcdName = lcd.CustomName ?? "";

						// Quick filter: must contain "slave" somewhere
						if (lcdName.IndexOf("slave", StringComparison.OrdinalIgnoreCase) < 0)
							continue;

						foreach (var kvp in masterPrefixes)
						{
							// Name must start with the master LCD name (e.g. "LCD_TV Test01_TL")
							// then have at least one more character (the _Slave suffix)
							if (lcdName.Length > kvp.Key.Length &&
								lcdName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
							{
								string quad = kvp.Value;
								if (!slavesByQuad.ContainsKey(quad))
									slavesByQuad[quad] = new List<IMyTextPanel>();
								slavesByQuad[quad].Add(lcd);
								break;
							}
						}
					}
					return false;
				});

				_cachedSlavesByQuad = slavesByQuad;
				_cachedSlavesKey = cacheKey;
			}

			if (slavesByQuad.Count == 0)
			{
				Log.Debug($"[{Name}] 📋 No slave LCDs found for '{baseName}' - name them e.g. 'LCD_TV {baseName}_TL_Slave'");
				return;
			}

			// Copy master content to each slave group (master looked up silently — it was just written)
			foreach (string quad in quadrants)
			{
				if (!slavesByQuad.TryGetValue(quad, out var slaves) || slaves.Count == 0)
					continue;

				string masterLcdName = $"{lcdPrefix} {baseName}{quad}";
				var masterLcd = FindLCDByName(masterLcdName, silent: true);
				if (masterLcd == null)
					continue;

				string masterText = masterLcd.GetText();
				foreach (var slaveLcd in slaves)
				{
					slaveLcd.WriteText(masterText);
					slaveLcd.ContentType = masterLcd.ContentType;
					slaveLcd.Font = masterLcd.Font;
					slaveLcd.FontSize = masterLcd.FontSize;
					slaveLcd.FontColor = masterLcd.FontColor;
					slaveLcd.BackgroundColor = masterLcd.BackgroundColor;
					slaveLcd.TextPadding = masterLcd.TextPadding;
					slaveLcd.Alignment = masterLcd.Alignment;

					Log.Debug($"[{Name}] ✅ Copied {quad} to slave: '{slaveLcd.CustomName}'");
				}
			}
		}

		/// <summary>
		/// Parse RGB color from string "R,G,B".
		/// </summary>
		private Color ParseColor(string colorString)
		{
			try
			{
				string[] parts = colorString.Split(',');
				if (parts.Length == 3)
				{
					byte r = byte.Parse(parts[0].Trim());
					byte g = byte.Parse(parts[1].Trim());
					byte b = byte.Parse(parts[2].Trim());
					return new Color(r, g, b);
				}
			}
			catch { }

			return Color.White;
		}

		/// <summary>
		/// Clears all cached LCD references so the next frame write performs a fresh entity scan.
		/// Called automatically on every camera rescan to pick up newly placed or renamed panels.
		/// </summary>
		public void InvalidateLcdCache()
		{
			_lcdCache.Clear();
			_cachedSlavesByQuad = null;
			_cachedSlavesKey = null;
			Log.Debug($"[{Name}] LCD cache invalidated");
		}

		/// <summary>
		/// Logs at INFO when EnableVerboseFrameLogging is on, DEBUG otherwise.
		/// Use for repetitive operational messages (cycling, GOTO, heartbeat) that
		/// are useful for diagnostics but noisy in normal production runs.
		/// </summary>
		private void LogActivity(string message)
		{
			if (_sharedConfig.EnableVerboseFrameLogging)
				Log.Info(message);
			else
				Log.Debug(message);
		}

		/// <summary>
		/// Checks whether any live player is within ProximityCheckRadius metres of any LCD
		/// belonging to this connection (masters and slaves included).
		/// Updates _anyPlayerNearby and logs a single INFO line when the state changes.
		/// Must be called from the game thread.
		/// </summary>
		private void CheckPlayerProximity()
		{
			float radius = _sharedConfig.ProximityCheckRadius;
			if (radius <= 0f)
			{
				_anyPlayerNearby = true;
				return;
			}

			// All LCDs for this connection — master panels AND slaves — share the prefix
			// "{lcdPrefix} {baseName}" so a single StartsWith scan finds them all.
			string baseName = _config.GetLiveFeedLcdBaseName();
			string lcdPrefix = $"{_config.LcdPrefix} {baseName}";

			var lcdPositions = new List<Vector3D>();
			MyAPIGateway.Entities.GetEntities(null, entity =>
			{
				var grid = entity as MyCubeGrid;
				if (grid == null) return false;
				foreach (var block in grid.GetFatBlocks())
				{
					var lcd = block as IMyTextPanel;
					if (lcd != null && (lcd.CustomName ?? "").StartsWith(lcdPrefix, StringComparison.OrdinalIgnoreCase))
						lcdPositions.Add(lcd.WorldMatrix.Translation);
				}
				return false;
			});

			if (lcdPositions.Count == 0)
			{
				// No LCD found yet — stay active so writes begin as soon as one appears
				_anyPlayerNearby = true;
				return;
			}

			float radiusSq = radius * radius;
			var players = new List<IMyPlayer>();
			MyAPIGateway.Players.GetPlayers(players);

			_anyPlayerNearby = players.Any(p =>
			{
				var character = p.Character;
				if (character == null || character.IsDead) return false;
				Vector3D charPos = character.WorldMatrix.Translation;
				return lcdPositions.Any(pos => Vector3D.DistanceSquared(charPos, pos) <= radiusSq);
			});

			if (_anyPlayerNearby != _wasPlayerNearby)
			{
				Log.Info(_anyPlayerNearby
					? $"[{Name}] 👁️ Player entered CCTV area ({radius:F0}m) - resuming LCD stream"
					: $"[{Name}] 💤 No players within {radius:F0}m of any '{lcdPrefix}*' LCD - pausing LCD stream");
				_wasPlayerNearby = _anyPlayerNearby;
			}
		}

		/// <summary>
		/// Manually advance to the next camera. Resets the cycle timer so the new camera
		/// displays for a full interval before auto-cycling resumes.
		/// </summary>
		public void ManualNextCamera()
		{
			if (_cameras.Count == 0) return;
			_currentCameraIndex = (_currentCameraIndex + 1) % _cameras.Count;
			ExecuteManualSwitch();
		}

		/// <summary>
		/// Manually go back to the previous camera.
		/// </summary>
		public void ManualPreviousCamera()
		{
			if (_cameras.Count == 0) return;
			_currentCameraIndex = (_currentCameraIndex - 1 + _cameras.Count) % _cameras.Count;
			ExecuteManualSwitch();
		}

		/// <summary>
		/// Reset the auto-cycle timer without switching camera.
		/// Useful after a manual switch to restart the countdown cleanly.
		/// </summary>
		public void ResetAutoCycle()
		{
			_cameraCycleTicks = 0;
			_preTeleportSent = false;
			_nextCameraIndexForPreTP = -1;
			_isManualMode = false;
			Log.Info($"[{Name}] 🔄 Auto-cycle resumed");
		}

		/// <summary>
		/// Common logic for manual camera switches — sends commands, teleports,
		/// and resets the cycle timer so the chosen camera gets a full display window.
		/// </summary>
		private void ExecuteManualSwitch()
		{
			var cam = _cameras[_currentCameraIndex];
			_currentCameraEntityId = cam.EntityId;
			_cameraCycleTicks = 0;
			_preTeleportSent = false;
			_nextCameraIndexForPreTP = -1;
			_isManualMode = true;

			Send($"CAMERA {_currentCameraIndex + 1}");
			Send("SPECTATOR");
			TeleportToCamera(cam);

			_lastCameraSwitchTime = DateTime.UtcNow;
			_awaitingFirstFrameAfterSwitch = true;

			Log.Info($"[{Name}] 🎮 Manual → camera {_currentCameraIndex + 1}/{_cameras.Count}: {cam.DisplayName}");
		}

		public void Dispose()
		{
			Stop();
		}
	}
}
