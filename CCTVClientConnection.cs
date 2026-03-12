using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
		// _allCameras: every camera assigned to this connection (all loops).
		// _cameras:    only the cameras in the currently active loop — used by all cycling code.
		private readonly List<CameraInfo> _allCameras = new List<CameraInfo>();
		private readonly List<CameraInfo> _cameras    = new List<CameraInfo>();
		private int _currentCameraIndex = 0;
		private long _currentCameraEntityId = 0;

		// Camera loop support — cameras suffixed _L1, _L2 etc. form separate loops.
		// _availableLoops: sorted list of loop numbers found at last rescan (0 = no suffix, 1 = _L1 ...).
		// _currentLoopIndex: which loop number is currently active.
		private static readonly System.Text.RegularExpressions.Regex LoopSuffixRegex =
			new System.Text.RegularExpressions.Regex(
				@"_L(\d+)$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase |
				System.Text.RegularExpressions.RegexOptions.Compiled);
		private readonly List<int> _availableLoops = new List<int>();
		private int _currentLoopIndex = 0;

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
		private const int MAX_CYCLE_TICKS = 900;          // 15s ceiling (was 60s — too long, cameras appeared frozen)
		private const float MAX_SETTLE_EWMA_MS = 10000f;  // 10s cap — discard outliers
		private const int MIN_OBSERVATIONS = 3;           // Observations before auto-adjust kicks in

		// Pre-emptive teleport: send GOTO to the next camera before display switches,
		// hiding TP latency inside the current camera's display window.
		private bool _preTeleportSent = false;
		private int _nextCameraIndexForPreTP = -1;

		// Manual mode: set by Next/Prev buttons to pause auto-cycling.
		// Cleared by ResetAutoCycle() so the player can re-enable normal cycling.
		private bool _isManualMode = false;

		// Frame queue
		private readonly Queue<(int width, int height, string decodedContent, bool isColor)> _frameQueue = new Queue<(int, int, string, bool)>();
		private readonly object _frameQueueLock = new object();

		// Serialises TCP writes so the game thread and listener thread never interleave.
		private readonly object _sendLock = new object();

		// Background send thread: game thread enqueues messages, send thread writes to TCP.
		// This guarantees the game thread NEVER blocks on _stream.Write().
		private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
		private readonly AutoResetEvent _sendEvent = new AutoResetEvent(false);
		private const int MAX_SEND_QUEUE = 100;

		// LCD panel cache — avoids per-frame entity scans.
		// Invalidated by InvalidateLcdCache() on every camera rescan.
		private readonly Dictionary<string, IMyTextPanel> _lcdCache =
		new Dictionary<string, IMyTextPanel>(StringComparer.OrdinalIgnoreCase);
		private Dictionary<string, List<IMyTextPanel>> _cachedSlavesByQuad;
		private string _cachedSlavesKey;

		// Game thread ID — captured on first Update() call so TeleportToCamera can
		// execute inline instead of deadlocking via InvokeBlocking.
		private int _gameThreadId = -1;

		// Deferred GOTO queue: TeleportToCamera enqueues the SendMessageTo action
		// instead of executing it inline.  Update() drains this queue at the start
		// of the NEXT tick so the game thread never blocks mid-call-stack on Steam
		// P2P networking (the root cause of the intermittent server hangs visible
		// in the Windows Wait Chain as "waiting to finish network I/O").
		private readonly ConcurrentQueue<Action> _pendingGotoActions = new ConcurrentQueue<Action>();

		// Proximity gate: skip LCD writes when no players are nearby.
		// CCTVCapture keeps streaming; frames are drained and discarded until a player returns.
		private bool _anyPlayerNearby = true;       // optimistic default so LCDs start active
		private bool _wasPlayerNearby = true;        // tracks last logged state
		private int _proximityCheckTicks = 0;
		private const int PROXIMITY_CHECK_INTERVAL = 300; // ticks between checks (~5 s at 60 TPS)

		// Cockpit-aware LCD throttle: when a player enters a cockpit on the SAME grid
		// as a CCTV LCD (dynamic grids only), SE's renderer rebuilds LCD textures
		// synchronously during the seat transition — stalling the game thread.
		// We pause LCD writes for a short spool-up period after cockpit entry so
		// SE can finish the transition animation without competing with WriteText calls.
		private bool _playerInCockpitOnLcdGrid = false;
		private bool _wasPlayerInCockpitOnLcdGrid = false;
		private int _cockpitSpoolUpTicksRemaining = 0;
		private const int COCKPIT_SPOOL_UP_TICKS = 120; // ~2 seconds at 60 TPS

		// Display FPS throttling: single and grid writes are staggered onto
		// separate ticks so they never coincide, halving per-tick LCD work.
		private int _singleDisplayTicks = 0;
		private int _gridDisplayTicks;
		private readonly int _displayFpsInterval;

		// Persistent latest frame per resolution type.
		// Populated by the cheap per-tick drain, consumed on display ticks.
		private (int width, int height, string content, bool isColor) _pendingSingleFrame;
		private (int width, int height, string content, bool isColor) _pendingGridFrame;
		private bool _hasPendingSingleFrame;
		private bool _hasPendingGridFrame;

		// LCD write diagnostics — counters reset on each heartbeat log so the values
		// show per-interval rates.  Lets us see exactly where the frame pipeline
		// stalls when a user reports "LCDs stopped updating".
		private int _framesReceived;    // frames enqueued by ProcessClientMessage
		private int _lcdWritesSingle;   // successful WriteSingleLCD calls (non-grid)
		private int _lcdWritesGrid;     // successful WriteGridLCDs calls
		private int _lcdMisses;          // FindLCDByName returned null during a write
		private int _lcdEntityFails;     // FindLCDByName failed due to entity snapshot error

		// Performance timing: track worst-case game thread stalls per heartbeat interval.
		// Any single Update() call that exceeds SLOW_TICK_THRESHOLD_MS is logged immediately.
		private const double SLOW_TICK_THRESHOLD_MS = 16.0; // 1 game tick at 60 TPS
		private double _worstTickMs;
		private double _worstWriteMs;
		private double _worstProximityMs;
		private int _slowTickCount;

		// Periodic LCD flush: every 10 minutes, toggle ContentType on all cached LCDs
		// to force SE's renderer to rebuild the display.  Works around SE sometimes
		// ignoring WriteText() visually despite the API call succeeding.
		private int _lcdFlushTicks = 0;
		private const int LCD_FLUSH_INTERVAL = 36000; // 10 minutes at 60 TPS

		// Reusable line-offset buffers for WriteGridLCDs — avoids content.Split('\n')
		// which creates 362+ new strings (~256 KB) on the game thread every grid write.
		// Only accessed from the game thread so no synchronisation needed.
		private int[] _lineStarts;
		private int[] _lineLengths;

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

			// Display FPS: controls how often LCD writes occur (60 ticks / fps).
			// Single and grid writes are staggered by half the interval so they
			// never land on the same tick, keeping per-tick LCD work low.
			int displayFps = Math.Max(1, sharedConfig?.DisplayFps ?? 2);
			_displayFpsInterval = 60 / displayFps;
			_gridDisplayTicks = _displayFpsInterval / 2;
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
			_sendEvent.Set(); // wake send thread so it exits

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
			_allCameras.Clear();
			_allCameras.AddRange(allCameras.Where(ShouldHandleCamera));

			Log.Info($"[{Name}] Updated camera list: {_allCameras.Count} cameras assigned (out of {allCameras.Count} total)");

			foreach (var cam in _allCameras)
				Log.Debug($"[{Name}] - Camera: {cam.DisplayName} (Faction: {cam.FactionTag ?? "None"})");

			Log.Debug($"[{Name}] Config: CameraPrefix='{_config.CameraPrefix}', FactionTag='{_config.FactionTag ?? "None"}'");

			// Discover available loops and rebuild _cameras for the current loop.
			DiscoverLoops();
			RebuildActiveCameras();

			// Invalidate any in-flight pre-TP state: after rebuilding the list the pre-TP
			// index may refer to a different camera (e.g. round-robin reorder on rescan).
			// Better to re-issue the teleport on the next cycle than to skip it silently.
			_preTeleportSent = false;
			_nextCameraIndexForPreTP = -1;

				// Re-pin _currentCameraIndex to the same physical camera after list rebuild.
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
						Log.Info($"[{Name}] Current camera (EntityId: {_currentCameraEntityId}) no longer in list after rescan, resetting index");
						_currentCameraIndex = 0;
						_currentCameraEntityId = 0;
					}
				}

				if (IsConnected)
				{
					string newSig = BuildCameraListSignature();
					if (newSig != _lastSentCameraListSignature)
					{
						Log.Info($"[{Name}] 📤 Camera list changed — sending to client ({_cameras.Count} cameras)");
						SendCameraListToClient();
						_lastSentCameraListSignature = newSig;
					}

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

		/// <summary>
		/// Extracts the loop number from a camera display name.
		/// "Test01_L1" → 1, "Test01_L2" → 2, "Test01" → 0 (no suffix = loop 0).
		/// </summary>
		private int ExtractLoopNumber(string displayName)
		{
			var m = LoopSuffixRegex.Match(displayName);
			return (m.Success && int.TryParse(m.Groups[1].Value, out int n)) ? n : 0;
		}

		/// <summary>
		/// Scans _allCameras to build the sorted list of available loop numbers.
		/// If only loop 0 is found (no _L suffixes), there is just one loop and
		/// NextLoop/PrevLoop will be no-ops.
		/// </summary>
		private void DiscoverLoops()
		{
			_availableLoops.Clear();
			foreach (var cam in _allCameras)
			{
				int n = ExtractLoopNumber(cam.DisplayName);
				if (!_availableLoops.Contains(n))
					_availableLoops.Add(n);
			}
			_availableLoops.Sort();

			// Clamp current loop index to an available loop (handles cameras being renamed/removed)
			if (_availableLoops.Count > 0 && !_availableLoops.Contains(_currentLoopIndex))
			{
				_currentLoopIndex = _availableLoops[0];
				Log.Info($"[{Name}] Current loop no longer exists after rescan — reset to loop {_currentLoopIndex}");
			}

			if (_availableLoops.Count > 1)
				Log.Info($"[{Name}] 🔁 Camera loops discovered: {string.Join(", ", _availableLoops.Select(l => l == 0 ? "(none)" : $"_L{l}"))} — active: {(_currentLoopIndex == 0 ? "(none)" : $"_L{_currentLoopIndex}")}");
		}

		/// <summary>
		/// Rebuilds _cameras to contain only cameras belonging to _currentLoopIndex.
		/// When only one loop exists (no _L suffixes), _cameras == _allCameras.
		/// </summary>
		private void RebuildActiveCameras()
		{
			_cameras.Clear();
			if (_availableLoops.Count <= 1)
			{
				_cameras.AddRange(_allCameras);
			}
			else
			{
				_cameras.AddRange(_allCameras.Where(c => ExtractLoopNumber(c.DisplayName) == _currentLoopIndex));
			}
			Log.Info($"[{Name}] Active loop {(_currentLoopIndex == 0 ? "(none)" : $"_L{_currentLoopIndex}")}: {_cameras.Count}/{_allCameras.Count} cameras");
		}

		/// <summary>
		/// Advance to the next camera loop (_L1 → _L2 → ...).
		/// No-op when only one loop exists or already at the last loop.
		/// </summary>
		public void NextLoop()
		{
			if (_availableLoops.Count <= 1)
			{
				Log.Info($"[{Name}] NextLoop: only one loop available, nothing to switch");
				return;
			}
			int idx = _availableLoops.IndexOf(_currentLoopIndex);
			if (idx >= _availableLoops.Count - 1)
			{
				Log.Info($"[{Name}] NextLoop: already at last loop (_L{_currentLoopIndex}), nothing to switch");
				return;
			}
			_currentLoopIndex = _availableLoops[idx + 1];
			SwitchToLoop();
		}

		/// <summary>
		/// Go back to the previous camera loop (... → _L2 → _L1).
		/// No-op when only one loop exists or already at the first loop.
		/// </summary>
		public void PrevLoop()
		{
			if (_availableLoops.Count <= 1)
			{
				Log.Info($"[{Name}] PrevLoop: only one loop available, nothing to switch");
				return;
			}
			int idx = _availableLoops.IndexOf(_currentLoopIndex);
			if (idx <= 0)
			{
				Log.Info($"[{Name}] PrevLoop: already at first loop (_L{_currentLoopIndex}), nothing to switch");
				return;
			}
			_currentLoopIndex = _availableLoops[idx - 1];
			SwitchToLoop();
		}

		/// <summary>
		/// Common logic after NextLoop/PrevLoop: rebuild camera list, push updated list to
		/// CCTVCapture, and jump to the first camera in the new loop immediately.
		/// </summary>
		private void SwitchToLoop()
		{
			InvalidateLcdCache();
			RebuildActiveCameras();

			_currentCameraIndex = 0;
			_currentCameraEntityId = 0;
			_cameraCycleTicks = 0;
			_preTeleportSent = false;
			_nextCameraIndexForPreTP = -1;
			_isManualMode = false; // keep auto-cycle running in the new loop

			// Reset settle-time EWMA
			// L2 cameras may be in completely different locations with different settle
			// latencies; starting fresh avoids the cycle interval being wrongly
			// extended (or shrunk) based on stale L1 observations.
			_settleTimeEwmaMs = 3000f;
			_settleTimeObservations = 0;

			Log.Info($"[{Name}] 🔄 Switched to loop {(_currentLoopIndex == 0 ? "(none)" : $"_L{_currentLoopIndex}")} ({_cameras.Count} cameras)");

			if (!IsConnected) return;

			// Push the new camera list to CCTVCapture so its CAMERA index is correct
			SendCameraListToClient();
			_lastSentCameraListSignature = BuildCameraListSignature();

			if (_cameras.Count == 0) return;

			// Jump to first camera in the new loop
			_currentCameraEntityId = _cameras[0].EntityId;
			Send("CAMERA 1");
			TeleportToCamera(_cameras[0]);
			_lastCameraSwitchTime = DateTime.UtcNow;
			_awaitingFirstFrameAfterSwitch = true;
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
		/// Thread-safe and non-blocking: enqueues the message for a background send
		/// thread so the game thread NEVER blocks on TCP writes.
		/// </summary>
		public void Send(string message)
		{
			if (!_isRunning) return;

			_sendQueue.Enqueue(Encoding.UTF8.GetBytes(message + "\n"));

			// Cap queue to prevent unbounded growth when client isn't reading
			while (_sendQueue.Count > MAX_SEND_QUEUE)
				_sendQueue.TryDequeue(out _);

			_sendEvent.Set();
		}

		/// <summary>
		/// Background thread that drains the send queue and writes to the TCP stream.
		/// Runs alongside HandleClientMessages so the game thread never touches I/O.
		/// </summary>
		private void SendWorker()
		{
			Log.Info($"[{Name}] Send thread started");

			while (_isRunning)
			{
				_sendEvent.WaitOne(500); // wake on signal or every 500ms

				var stream = _stream;
				if (stream == null) continue;

				while (_sendQueue.TryDequeue(out var data))
				{
					try
					{
						lock (_sendLock)
						{
							stream.Write(data, 0, data.Length);
						}
						_messagesSent++;
					}
					catch (IOException)
					{
						Log.Warn($"[{Name}] Send worker: write failed (client disconnected or timeout)");
						break;
					}
					catch (SocketException)
					{
						Log.Warn($"[{Name}] Send worker: socket error");
						break;
					}
					catch (ObjectDisposedException) { break; }
					catch (Exception ex)
					{
						Log.Error(ex, $"[{Name}] Send worker error");
						break;
					}
				}
			}

			Log.Info($"[{Name}] Send thread exiting");
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
						candidate.SendTimeout = 2000; // 2s — prevents game thread from blocking indefinitely on Write()

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

						// Close previous client before accepting the new one so the old
						// handler thread sees its connection die and exits cleanly.
						var oldClient = _client;
						if (oldClient != null)
						{
							try { oldClient.Close(); } catch { }
						}

						_client = candidate;
						_stream = candidateStream;

						// Clear stale messages from previous client's send queue
						while (_sendQueue.TryDequeue(out _)) { }

						Log.Info($"✅ [{Name}] CCTVCapture connected from {_client.Client.RemoteEndPoint}");

						// Start background send thread BEFORE any Send() calls
						Thread sendThread = new Thread(SendWorker)
						{
							IsBackground = true,
							Name = $"CCTVCapture-{Name}-Sender"
						};
						sendThread.Start();

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
			// Capture local references so the finally block only cleans up THIS
			// connection, not a newer one that the listener may have accepted while
			// this handler was still shutting down.
			var myClient = _client;
			var myStream = _stream;

			try
			{
				// 🔧 CRITICAL FIX: Use larger buffer for StreamReader to handle 500KB FRAME messages
				using (StreamReader reader = new StreamReader(myStream, Encoding.UTF8, false, 1024 * 1024)) // 1 MB buffer
				{
					string line;
					while (_isRunning && myClient != null && myClient.Connected && (line = reader.ReadLine()) != null)
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
				// Only clear shared fields if they still point to OUR connection.
				// A new client may have already connected and overwritten them.
				if (_client == myClient)
				{
					_client = null;
					_stream = null;
					_lastSentCameraListSignature = null; // force full list re-send on next connection
				}
				myClient?.Close();
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
				return;
			}

			// GETCONFIG command
			if (message == "GETCONFIG")
			{
				string config = $"CONFIG CaptureWidth={_sharedConfig.CaptureWidth} CaptureHeight={_sharedConfig.CaptureHeight} " +
							   $"CaptureFps={_sharedConfig.CaptureFps} UseColorMode={_sharedConfig.UseColorMode} " +
							   $"UseDithering={_sharedConfig.UseDithering} DitherMode={_sharedConfig.DitherMode} " +
							   $"PostProcessMode={_sharedConfig.PostProcessMode} " +
							   $"GridPostProcessMode={_sharedConfig.GridPostProcessMode} " +
							   $"LcdGridResolution={_sharedConfig.LcdGridResolution} " +
							   $"DesaturateColorMode={_sharedConfig.DesaturateColorMode} " +
							   $"CropCaptureToSquare={_sharedConfig.CropCaptureToSquare}";
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
								Interlocked.Increment(ref _framesReceived);
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

									// Discard extreme outliers (e.g. frame arrived after a long GC pause
									// or network stall) to prevent the EWMA from ballooning.
									if (settleMs > MAX_SETTLE_EWMA_MS)
									{
										Log.Info($"[{Name}] 📊 Settle outlier discarded: {settleMs:F0}ms (cap {MAX_SETTLE_EWMA_MS:F0}ms)");
										settleMs = MAX_SETTLE_EWMA_MS;
									}

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
			var tickSw = Stopwatch.StartNew();

			// Capture game thread ID on first call (Update is always on game thread)
			if (_gameThreadId == -1)
				_gameThreadId = Thread.CurrentThread.ManagedThreadId;

			// Drain deferred GOTO actions (SendMessageTo).  These were queued by
			// TeleportToCamera so the game thread never blocks on Steam P2P I/O
			// inside a nested call (CycleToNextCamera → TeleportToCamera → SendMessageTo).
			while (_pendingGotoActions.TryDequeue(out var gotoAction))
			{
				try { gotoAction(); }
				catch (Exception ex) { Log.Error(ex, $"[{Name}] Error executing deferred GOTO action"); }
			}

			_updateCallCount++;

			// Log update calls every 300 ticks (5 seconds) when connected
			if (IsConnected && (_updateCallCount % 300) == 0 && _updateCallCount != _lastLoggedUpdateCount)
			{
				_lastLoggedUpdateCount = _updateCallCount;
				int queueCount;
				lock (_frameQueueLock) { queueCount = _frameQueue.Count; }
				int rxSnap = Interlocked.Exchange(ref _framesReceived, 0);
				int wSingle = Interlocked.Exchange(ref _lcdWritesSingle, 0);
				int wGrid   = Interlocked.Exchange(ref _lcdWritesGrid, 0);
				int miss    = Interlocked.Exchange(ref _lcdMisses, 0);
				int eFail   = Interlocked.Exchange(ref _lcdEntityFails, 0);
				Log.Info($"[{Name}] 🔄 Heartbeat: cam {_currentCameraIndex + 1}/{_cameras.Count}, " +
					$"cycle {_cameraCycleTicks}/{_dynamicCycleIntervalTicks}t, " +
					$"manual={_isManualMode}, autoCycle={_sharedConfig.EnableAutoCameraCycling}, " +
					$"queue={queueCount}, preTP={_preTeleportSent}, nearby={_anyPlayerNearby}, " +
					$"rx={rxSnap}, lcdW={wSingle}+{wGrid}, miss={miss}, eFail={eFail}, " +
					$"perf: worst={_worstTickMs:F1}ms write={_worstWriteMs:F1}ms prox={_worstProximityMs:F1}ms slow={_slowTickCount}");
				_worstTickMs = 0; _worstWriteMs = 0; _worstProximityMs = 0; _slowTickCount = 0;
			}

			if (!IsConnected)
				return;

			// Auto-cycle cameras if enabled and not overridden by a manual button press
			bool shouldCycle = _sharedConfig.EnableAutoCameraCycling && _cameras.Count > 0 && !_isManualMode;
			if (!shouldCycle && (_updateCallCount % 600) == 0 && _cameras.Count > 0)
			{
				if (_isManualMode)
					Log.Debug($"[{Name}] Cycling paused: manual mode active (use Reset to resume)");
				else
					Log.Warn($"[{Name}] ⚠️ Cycling paused: autoCycle={_sharedConfig.EnableAutoCameraCycling}, cameras={_cameras.Count}");
			}
			if (shouldCycle)
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

			if (++_proximityCheckTicks >= PROXIMITY_CHECK_INTERVAL)
			{
				_proximityCheckTicks = 0;
				var proxSw = Stopwatch.StartNew();
				CheckPlayerProximity();
				proxSw.Stop();
				if (proxSw.Elapsed.TotalMilliseconds > _worstProximityMs)
					_worstProximityMs = proxSw.Elapsed.TotalMilliseconds;
				if (proxSw.Elapsed.TotalMilliseconds > SLOW_TICK_THRESHOLD_MS)
					Log.Warn($"[{Name}] ⏱️ SLOW proximity check: {proxSw.Elapsed.TotalMilliseconds:F1}ms");
			}

			// Periodic LCD flush: toggle ContentType to NONE on all cached panels,
			// forcing SE to tear down and rebuild their render state.
			// The next WriteLCDContent call restores TEXT_AND_IMAGE automatically.
			if (++_lcdFlushTicks >= LCD_FLUSH_INTERVAL)
			{
				_lcdFlushTicks = 0;
				FlushLCDs();
			}

			// ── 1. Drain queue every tick (cheap) ──────────────────────────────
			// Keep the latest frame per resolution type in persistent fields so
			// stale frames never accumulate and display ticks always have fresh data.
			int gridRes = _sharedConfig.LcdGridResolution;
			lock (_frameQueueLock)
			{
				while (_frameQueue.Count > 0)
				{
					var frame = _frameQueue.Dequeue();
					if (frame.width == gridRes && frame.height == gridRes)
					{
						_pendingGridFrame = frame;
						_hasPendingGridFrame = true;
					}
					else
					{
						_pendingSingleFrame = frame;
						_hasPendingSingleFrame = true;
					}
				}
			}

			// ── 2. Staggered display ticks ────────────────────────────────────
			// Single and grid writes fire on separate ticks (offset by half the
			// interval) so at most ~5 LCD writes happen per tick, not ~10.
			bool singleTick = (++_singleDisplayTicks >= _displayFpsInterval);
			if (singleTick) _singleDisplayTicks = 0;

			bool gridTick = (++_gridDisplayTicks >= _displayFpsInterval);
			if (gridTick) _gridDisplayTicks = 0;

			// Cockpit spool-up gate: when a player just entered a cockpit on the
			// same dynamic grid as CCTV LCDs, pause writes for a short period so
			// SE's renderer can finish the seat transition without competing with
			// WriteText texture rebuilds (the root cause of the cockpit-entry hang).
			if (_cockpitSpoolUpTicksRemaining > 0)
				_cockpitSpoolUpTicksRemaining--;

			bool writesAllowed = _anyPlayerNearby && _cockpitSpoolUpTicksRemaining == 0;

			if (writesAllowed)
			{
				if (singleTick && _hasPendingSingleFrame)
				{
					var sw = Stopwatch.StartNew();
					WriteFrameToLCDs(_pendingSingleFrame.width, _pendingSingleFrame.height, _pendingSingleFrame.content, _pendingSingleFrame.isColor);
					sw.Stop();
					_hasPendingSingleFrame = false;
					if (sw.Elapsed.TotalMilliseconds > _worstWriteMs) _worstWriteMs = sw.Elapsed.TotalMilliseconds;
					if (sw.Elapsed.TotalMilliseconds > SLOW_TICK_THRESHOLD_MS)
						Log.Warn($"[{Name}] ⏱️ SLOW single LCD write: {sw.Elapsed.TotalMilliseconds:F1}ms ({_pendingSingleFrame.width}×{_pendingSingleFrame.height})");
					_pendingSingleFrame = default; // Release content string reference for GC
				}
				if (gridTick && _hasPendingGridFrame)
				{
					var sw = Stopwatch.StartNew();
					WriteFrameToLCDs(_pendingGridFrame.width, _pendingGridFrame.height, _pendingGridFrame.content, _pendingGridFrame.isColor);
					sw.Stop();
					_hasPendingGridFrame = false;
					if (sw.Elapsed.TotalMilliseconds > _worstWriteMs) _worstWriteMs = sw.Elapsed.TotalMilliseconds;
					if (sw.Elapsed.TotalMilliseconds > SLOW_TICK_THRESHOLD_MS)
						Log.Warn($"[{Name}] ⏱️ SLOW grid LCD write: {sw.Elapsed.TotalMilliseconds:F1}ms ({_pendingGridFrame.width}×{_pendingGridFrame.height})");
					_pendingGridFrame = default; // Release LOH content string reference for GC
				}
			}

			tickSw.Stop();
			if (tickSw.Elapsed.TotalMilliseconds > _worstTickMs)
				_worstTickMs = tickSw.Elapsed.TotalMilliseconds;
			if (tickSw.Elapsed.TotalMilliseconds > SLOW_TICK_THRESHOLD_MS)
			{
				_slowTickCount++;
				Log.Warn($"[{Name}] ⏱️ SLOW TICK: {tickSw.Elapsed.TotalMilliseconds:F1}ms (total Update call)");
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
		/// Safe to call from any thread: enqueues a deferred action that Update() drains
		/// on the next game tick so the caller never blocks on Steam P2P networking.
		/// </summary>
		private void TeleportToCamera(CameraInfo camera)
		{
			if (SteamId == 0)
			{
				Log.Warn($"[{Name}] ⚠️ SpectatorSteamId is 0 — set it in CCTVPlugin.cfg or the instance config, TP/spectator cam will not work");
				return;
			}

			// Capture entity ID by value — the CameraInfo reference must not be used
			// after the current call returns because the caller may mutate the list.
			long entityId = camera.EntityId;
			string displayName = camera.DisplayName;
			ulong steamId = SteamId;
			string connName = Name;

			Action sendGoto = () =>
			{
				try
				{
					var cameraEntity = MyAPIGateway.Entities.GetEntityById(entityId) as Sandbox.Game.Entities.MyCameraBlock;
					if (cameraEntity == null)
					{
						Log.Warn($"[{connName}] Camera entity {entityId} not found!");
						return;
					}

					Vector3D position = cameraEntity.WorldMatrix.Translation;
					Vector3D forward = cameraEntity.WorldMatrix.Forward;
					Vector3D up = cameraEntity.WorldMatrix.Up;

					// Format: GOTO|SteamID|CameraName|EntityID|X|Y|Z|FwdX|FwdY|FwdZ|UpX|UpY|UpZ
					var ic = CultureInfo.InvariantCulture;
					string gotoMessage = $"GOTO|{steamId}|{displayName}|{entityId}|" +
									$"{position.X.ToString(ic)}|{position.Y.ToString(ic)}|{position.Z.ToString(ic)}|" +
									$"{forward.X.ToString(ic)}|{forward.Y.ToString(ic)}|{forward.Z.ToString(ic)}|" +
									$"{up.X.ToString(ic)}|{up.Y.ToString(ic)}|{up.Z.ToString(ic)}";

					byte[] data = System.Text.Encoding.UTF8.GetBytes(gotoMessage);
					const ushort MESSAGE_ID = 12346;
					// Target only the spectator client — SendMessageToOthers broadcasts to
					// ALL clients including the player, whose client-side message dispatch
					// can interfere with cockpit seat transitions.
					MyAPIGateway.Multiplayer.SendMessageTo(MESSAGE_ID, data, steamId);

					Log.Debug($"[{connName}] 📡 Sent GOTO message to mod for camera '{displayName}' (SteamID: {steamId})");
				}
				catch (Exception ex)
				{
					Log.Error(ex, $"[{connName}] Failed to send camera position to mod");
				}
			};

			// Never call SendMessageTo inline — always defer to the next game tick.
			// Steam P2P can stall when the target is unreachable, and calling it from
			// inside CycleToNextCamera / UpdateCameras / ManualSwitch blocks the entire
			// game thread mid-call-stack (the root cause of the server hangs visible
			// in the Windows Wait Chain as "waiting to finish network I/O").
			_pendingGotoActions.Enqueue(sendGoto);
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
						string shifted = ApplyContentShift(content, _sharedConfig.SingleContentShift);
						WriteSingleLCD(singleLcdName, shifted, isColor);
						CopyToSlaveLCDs(_config.LcdPrefix, baseName);
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
				_lcdMisses++;
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
			_lcdWritesSingle++;
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
		/// Auto-calculates the correct font size for a single LCD panel using the
		/// dedicated SingleLcdFontSize setting (independent of the 2×2 grid font).
		/// </summary>
		private float CalculateAutoFontSize(bool isColor)
		{
			// Single LCD uses its own font base so it can be tuned without affecting the grid.
			//   colour    → SingleLcdFontSize × 1
			//   grayscale → SingleLcdFontSize × 2  (SE Monospace chars are ~½ as wide as colour)
			float fontSize = isColor ? _sharedConfig.SingleLcdFontSize : _sharedConfig.SingleLcdFontSize * 2f;
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

			// Index-based line scanning — avoids content.Split('\n') which creates
			// 362+ new strings (~256 KB) on the game thread every grid write.
			// Instead we record start-index and length of each line directly
			// into reusable int[] buffers (< 3 KB, SOH).
			int lineCount = 1;
			for (int i = 0; i < content.Length; i++)
				if (content[i] == '\n') lineCount++;

			if (lineCount < 4)
			{
				Log.Warn($"[{Name}] ⚠️ Grid content has {lineCount} lines, need at least 4");
				return;
			}

			// Grow reusable buffers only when needed — no per-frame allocation.
			if (_lineStarts == null || _lineStarts.Length < lineCount)
			{
				_lineStarts  = new int[lineCount];
				_lineLengths = new int[lineCount];
			}

			int li = 0;
			int ls = 0;
			for (int i = 0; i <= content.Length; i++)
			{
				if (i == content.Length || content[i] == '\n')
				{
					_lineStarts[li]  = ls;
					_lineLengths[li] = i - ls;
					li++;
					ls = i + 1;
				}
			}

			// Font size: grayscale chars are ~half the width of colour chars in SE Monospace,
			// so grayscale uses 2× the base GridFontSize to fill each panel horizontally.
			float gridFontSize = isColor
				? _sharedConfig.GridFontSize
				: _sharedConfig.GridFontSize * 2f;

			// Derive per-quadrant row count from the actual frame.
			int effectiveQuadH = lineCount / 2;

			// GridVerticalOffset: positive creates overlap at the seam to close the
			// physical gap between LCD blocks.
			int vOffset = _sharedConfig.GridVerticalOffset;
			int tlStartY = Math.Max(0, Math.Min(vOffset, effectiveQuadH - 1));
			int blStartY = Math.Max(0, effectiveQuadH - vOffset);

			// GridHorizontalOffset: same principle for the vertical seam.
			int hOffset = _sharedConfig.GridHorizontalOffset;

			// GridContentShift: uniform horizontal shift applied to ALL quadrants.
			// Positive = image moves left on LCDs (compensates for SE's built-in
			// left padding). Physically slides the extraction window right so more
			// of the left edge is visible and the right edge is cropped slightly.
			int shift = _sharedConfig.GridContentShift;

			int tlStartX = Math.Max(0, Math.Min(hOffset, quadW - 1) + shift);
			int trStartX = Math.Max(0, quadW - hOffset + shift);

			if (vOffset != 0 || hOffset != 0)
				Log.Debug($"[{Name}] GridOffset: v={vOffset} h={hOffset}, tlStartY={tlStartY}, blStartY={blStartY}, tlStartX={tlStartX}, trStartX={trStartX}");

			string tlContent = ExtractQuadrant(content, lineCount, tlStartX, tlStartY, quadW, effectiveQuadH);
			string trContent = ExtractQuadrant(content, lineCount, trStartX, tlStartY, quadW, effectiveQuadH);
			string blContent = ExtractQuadrant(content, lineCount, tlStartX, blStartY, quadW, effectiveQuadH);
			string brContent = ExtractQuadrant(content, lineCount, trStartX, blStartY, quadW, effectiveQuadH);

			// Build full LCD names: "LCD_TV Test01_TL" format
			// Pattern: <Prefix><space><BaseName><Quadrant>
			string tlLcdName = $"{lcdPrefix} {baseName}_TL";
			string trLcdName = $"{lcdPrefix} {baseName}_TR";
			string blLcdName = $"{lcdPrefix} {baseName}_BL";
			string brLcdName = $"{lcdPrefix} {baseName}_BR";

			Log.Debug($"[{Name}] Writing TL quadrant to '{tlLcdName}' ({tlContent.Length} chars, font {gridFontSize:F3})");
			WriteSingleLCD(tlLcdName, tlContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing TR quadrant to '{trLcdName}' ({trContent.Length} chars)");
			WriteSingleLCD(trLcdName, trContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing BL quadrant to '{blLcdName}' ({blContent.Length} chars)");
			WriteSingleLCD(blLcdName, blContent, isColor, gridFontSize);

			Log.Debug($"[{Name}] Writing BR quadrant to '{brLcdName}' ({brContent.Length} chars)");
			WriteSingleLCD(brLcdName, brContent, isColor, gridFontSize);

			_lcdWritesGrid++;

			// Copy to slave LCDs
			CopyToSlaveLCDs(lcdPrefix, baseName);
		}

		/// <summary>
		/// Extract a quadrant from the full grid content using pre-computed line offsets.
		/// Uses sb.Append(string, startIndex, count) to slice directly from the original
		/// content string — avoids 181 Substring allocations per quadrant (~90 KB each).
		/// </summary>
		private string ExtractQuadrant(string content, int lineCount, int startX, int startY, int width, int height)
		{
			StringBuilder sb = new StringBuilder(width * height + height);

			for (int y = 0; y < height && (startY + y) < lineCount; y++)
			{
				int li = startY + y;
				int ls = _lineStarts[li];
				int ll = _lineLengths[li];

				if (startX < ll)
				{
					int sliceWidth = Math.Min(width, ll - startX);
					sb.Append(content, ls + startX, sliceWidth);

					if (sliceWidth < width)
						sb.Append(' ', width - sliceWidth);
				}
				else
				{
					sb.Append(' ', width);
				}

				if (y < height - 1)
					sb.Append('\n');
			}

			string result = sb.ToString();

			if (startX == 0 && startY == 0)
				Log.Debug($"[{Name}] ExtractQuadrant TL: extracted {result.Length} chars (expected ~{width * height + height - 1})");

			return result;
		}

		/// <summary>
		/// Applies a uniform horizontal content shift to every line in a frame string.
		/// Positive shift trims N chars from the left of each line (image moves left on LCD).
		/// Negative shift prepends N spaces to each line (image moves right on LCD).
		/// Returns the original string unchanged when shift is 0.
		/// </summary>
		private static string ApplyContentShift(string content, int shift)
		{
			if (shift == 0 || string.IsNullOrEmpty(content))
				return content;

			var sb = new StringBuilder(content.Length);
			int i = 0;
			while (i < content.Length)
			{
				// Find end of current line
				int nl = content.IndexOf('\n', i);
				int lineEnd = (nl >= 0) ? nl : content.Length;
				int lineLen = lineEnd - i;

				if (shift > 0)
				{
					// Trim from left: skip 'shift' chars
					int skip = Math.Min(shift, lineLen);
					sb.Append(content, i + skip, lineLen - skip);
				}
				else
				{
					// Pad left: prepend spaces
					sb.Append(' ', -shift);
					sb.Append(content, i, lineLen);
				}

				if (nl >= 0)
					sb.Append('\n');

				i = lineEnd + 1;
			}

			return sb.ToString();
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
			// Snapshot to a list first: merge blocks / PB-driven grid splits can modify
			// the live entity collection mid-iteration → InvalidOperationException.
			List<MyCubeGrid> grids;
			try { grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList(); }
			catch (InvalidOperationException)
			{
				_lcdEntityFails++;
				Log.Info($"[{Name}] FindLCDByName('{name}'): entity list changed mid-snapshot — returning null");
				return null;
			}

			foreach (var grid in grids)
			{
				if (grid == null || grid.MarkedForClose) continue;
				try
				{
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
				}
				catch (InvalidOperationException) { continue; } // grid blocks changed mid-iteration (merge/split)
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
					try
					{
						Log.Debug($"[{Name}] 🔍 Listing LCDs with prefix '{_config.LcdPrefix}':");
						int count = 0;
						foreach (var grid in grids)
						{
							if (grid == null || grid.MarkedForClose) continue;
							foreach (var block in grid.GetFatBlocks())
							{
								var lcd = block as IMyTextPanel;
								if (lcd != null && (lcd.CustomName.Contains(_config.LcdPrefix) || lcd.CustomName.Contains("SLAVE")))
									Log.Debug($"[{Name}]    #{++count}: '{lcd.CustomName}' (Grid: {grid.DisplayName}, Static: {grid.IsStatic})");
							}
						}
					}
					catch (InvalidOperationException) { } // grid changed mid-diagnostic scan — harmless
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

			// Guard: the LCD's grid may have been destroyed or merged between the cache
			// lookup and now (e.g. merge block / PB-triggered grid split). All property
			// accesses below can throw if the block is orphaned.
			try
			{
				if (lcd.CubeGrid == null || ((MyCubeGrid)lcd.CubeGrid).MarkedForClose)
				{
					Log.Debug($"[{Name}] ⏭️ LCD grid gone — skipping write");
					return;
				}
			}
			catch { return; }

			try
			{
				// Auto HUD mode: if the LCD is on a non-static (moving) grid, force a fully
				// transparent background so the feed overlays the pilot's view instead of
				// blocking it. Explicit LcdBackgroundAlpha config only applies to static grids.
				bool isOnDynamicGrid = false;
				try { isOnDynamicGrid = (lcd.CubeGrid as MyCubeGrid)?.IsStatic == false; }
				catch { }

				int effectiveAlpha = isOnDynamicGrid ? 0 : Math.Max(0, Math.Min(255, _config.LcdBackgroundAlpha));

				if (isOnDynamicGrid)
					Log.Debug($"[{Name}] 🚗 HUD mode: LCD '{lcd.CustomName}' is on a dynamic grid — transparent background applied");

				// Auto-heatmap: remap SE color chars to a thermal palette for dynamic-grid HUD LCDs.
				// No config toggle — any moving-grid LCD automatically gets the infrared look.
				string writeContent = (isOnDynamicGrid && isColor) ? RemapToHeatmap(content) : content;
				lcd.WriteText(writeContent);
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
			catch (Exception ex)
			{
				// LCD became invalid mid-write (grid merged/split/destroyed).
				// Evict from cache so the next frame does a fresh scan.
				Log.Debug($"[{Name}] ⚠️ LCD write failed (grid change?): {ex.GetType().Name}");
				try { _lcdCache.Remove(lcd.CustomName); } catch { }
			}
		}

		/// <summary>
		/// Remaps every SE color character in a frame string to a thermal heatmap palette.
		/// Each char's packed 3-bit RGB is decoded, converted to luminance, then mapped
		/// through black→blue→cyan→yellow→red and re-encoded as an SE color char.
		/// Applied automatically to single LCDs on non-static (moving) grids so the HUD
		/// feed gets an infrared look without any player-facing config option.
		/// </summary>
		private static string RemapToHeatmap(string content)
		{
			const int SE_BASE = 0xE100;
			var sb = new System.Text.StringBuilder(content.Length);

			foreach (char c in content)
			{
				// SE color chars occupy 0xE100–0xE2FF (9-bit index: RRR GGG BBB)
				if (c >= 0xE100 && c <= 0xE2FF)
				{
					int index = c - SE_BASE;
					float r7 = (index >> 6) & 7;
					float g7 = (index >> 3) & 7;
					float b7 = index & 7;

					// Luminance (0-1) from the 0-7 channel values
					float lum = (r7 * 0.299f + g7 * 0.587f + b7 * 0.114f) / 7f;

					// Map through black→blue→cyan→yellow→red
					float fr, fg, fb;
					if (lum < 0.25f)      { float s = lum * 4f;           fr = 0; fg = 0; fb = s; }
					else if (lum < 0.5f)  { float s = (lum - 0.25f) * 4f; fr = 0; fg = s; fb = 1; }
					else if (lum < 0.75f) { float s = (lum - 0.5f)  * 4f; fr = s; fg = 1; fb = 1 - s; }
					else                  { float s = (lum - 0.75f) * 4f; fr = 1; fg = 1 - s; fb = 0; }

					// Quantize back to 3-bit per channel and re-encode
					int nr = Math.Max(0, Math.Min(7, (int)(fr * 7f + 0.5f)));
					int ng = Math.Max(0, Math.Min(7, (int)(fg * 7f + 0.5f)));
					int nb = Math.Max(0, Math.Min(7, (int)(fb * 7f + 0.5f)));
					sb.Append((char)(SE_BASE + ((nr << 6) | (ng << 3) | nb)));
				}
				else
				{
					sb.Append(c);
				}
			}

			return sb.ToString();
		}

		/// <summary>
		/// Copy master LCD content to slave LCDs.
		/// A slave is any LCD whose name starts with the master's name (case-insensitive) and
		/// contains "_slave" anywhere after it
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

					try
					{
					MyAPIGateway.Entities.GetEntities(null, entity =>
					{
						var grid = entity as MyCubeGrid;
						if (grid == null || grid.MarkedForClose) return false;

						try
						{
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

							// Single LCD slave: starts with "{lcdPrefix} {baseName}" directly
							// (no quadrant suffix) e.g. "LCD_TV Test01_Slave", "LCD_TV Test01_Slave2"
							string singleMaster = $"{lcdPrefix} {baseName}";
							if (lcdName.Length > singleMaster.Length &&
								lcdName.StartsWith(singleMaster, StringComparison.OrdinalIgnoreCase))
							{
								string remainder = lcdName.Substring(singleMaster.Length);
								bool isQuadrantSlave =
									remainder.StartsWith("_TL", StringComparison.OrdinalIgnoreCase) ||
									remainder.StartsWith("_TR", StringComparison.OrdinalIgnoreCase) ||
									remainder.StartsWith("_BL", StringComparison.OrdinalIgnoreCase) ||
									remainder.StartsWith("_BR", StringComparison.OrdinalIgnoreCase);
								if (!isQuadrantSlave)
								{
									if (!slavesByQuad.ContainsKey("_SINGLE"))
										slavesByQuad["_SINGLE"] = new List<IMyTextPanel>();
									slavesByQuad["_SINGLE"].Add(lcd);
								}
							}
						}
						}
						catch (InvalidOperationException) { } // grid blocks changed mid-iteration
						return false;
					});
					}
					catch (InvalidOperationException) { } // entity list changed mid-scan

				_cachedSlavesByQuad = slavesByQuad;
				_cachedSlavesKey = cacheKey;
			}

			if (slavesByQuad.Count == 0)
			{
				Log.Debug($"[{Name}] 📋 No slave LCDs found for '{baseName}' - name them e.g. 'LCD_TV {baseName}_TL_Slave' or 'LCD_TV {baseName}_Slave'");
				return;
			}

			// Copy master content to each grid quadrant slave group
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
					try
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
					catch (Exception ex)
					{
						Log.Debug($"[{Name}] ⚠️ Slave copy failed (grid change?): {ex.GetType().Name}");
					}
				}
			}

			// Copy master single LCD content to single slaves (e.g. "LCD_TV Test01_Slave")
			if (slavesByQuad.TryGetValue("_SINGLE", out var singleSlaves) && singleSlaves.Count > 0)
			{
				string masterLcdName = $"{lcdPrefix} {baseName}";
				var masterLcd = FindLCDByName(masterLcdName, silent: true);
				if (masterLcd != null)
				{
					string masterText = masterLcd.GetText();
						foreach (var slaveLcd in singleSlaves)
						{
							try
							{
								slaveLcd.WriteText(masterText);
								slaveLcd.ContentType = masterLcd.ContentType;
								slaveLcd.Font = masterLcd.Font;
								slaveLcd.FontSize = masterLcd.FontSize;
								slaveLcd.FontColor = masterLcd.FontColor;
								slaveLcd.BackgroundColor = masterLcd.BackgroundColor;
								slaveLcd.TextPadding = masterLcd.TextPadding;
								slaveLcd.Alignment = masterLcd.Alignment;

								Log.Debug($"[{Name}] ✅ Copied to single slave: '{slaveLcd.CustomName}'");
							}
							catch (Exception ex)
							{
								Log.Debug($"[{Name}] ⚠️ Single slave copy failed (grid change?): {ex.GetType().Name}");
							}
						}
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
		/// Forces SE to rebuild the render state of every cached LCD by briefly
		/// toggling ContentType to NONE. The next normal WriteLCDContent call
		/// restores TEXT_AND_IMAGE and re-writes all properties.
		/// Called every LCD_FLUSH_INTERVAL ticks (~10 minutes).
		/// </summary>
		private void FlushLCDs()
		{
			int flushed = 0;

			// Nuclear option: toggle the block OFF then ON to force SE to completely
			// destroy and recreate the visual render state.  ContentType toggle alone
			// wasn't enough — SE's renderer apparently caches the visual independently.
			foreach (var kvp in _lcdCache)
			{
				try
				{
					var lcd = kvp.Value;
					if (lcd?.CubeGrid == null) continue;
					var func = lcd as Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
					if (func != null && func.Enabled)
					{
						func.Enabled = false;
						func.Enabled = true;
						flushed++;
					}
				}
				catch { }
			}

			// Also flush any slave panels
			if (_cachedSlavesByQuad != null)
			{
				foreach (var slaves in _cachedSlavesByQuad.Values)
				{
					foreach (var lcd in slaves)
					{
						try
						{
							if (lcd?.CubeGrid == null) continue;
							var func = lcd as Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
							if (func != null && func.Enabled)
							{
								func.Enabled = false;
								func.Enabled = true;
								flushed++;
							}
						}
						catch { }
					}
				}
			}

			// Invalidate cache so next frame does a fresh entity scan
			InvalidateLcdCache();

			if (flushed > 0)
				Log.Info($"[{Name}] 🔃 LCD flush: power-cycled {flushed} panels + cache cleared");
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
		/// Also detects whether any nearby player is seated in a cockpit on the same
		/// grid as a CCTV LCD (dynamic grids only) — used to trigger the spool-up pause.
		/// Updates _anyPlayerNearby / _playerInCockpitOnLcdGrid and logs state changes.
		/// Must be called from the game thread.
		/// </summary>
		private void CheckPlayerProximity()
		{
			float radius = _sharedConfig.ProximityCheckRadius;
			if (radius <= 0f)
			{
				_anyPlayerNearby = true;
				_playerInCockpitOnLcdGrid = false;
				return;
			}

			// All LCDs for this connection — master panels AND slaves — share the prefix
			// "{lcdPrefix} {baseName}" so a single StartsWith scan finds them all.
			string baseName = _config.GetLiveFeedLcdBaseName();
			string lcdPrefix = $"{_config.LcdPrefix} {baseName}";

			var lcdPositions = new List<Vector3D>();
			// Collect grid EntityIds that contain CCTV LCDs on dynamic (non-static) grids.
			// Used below to detect cockpit-on-same-grid situations.
			var lcdDynamicGridIds = new HashSet<long>();
			try
			{
				MyAPIGateway.Entities.GetEntities(null, entity =>
				{
					var grid = entity as MyCubeGrid;
					if (grid == null || grid.MarkedForClose) return false;
					try
					{
						foreach (var block in grid.GetFatBlocks())
						{
							var lcd = block as IMyTextPanel;
							if (lcd != null && (lcd.CustomName ?? "").StartsWith(lcdPrefix, StringComparison.OrdinalIgnoreCase))
							{
								lcdPositions.Add(lcd.WorldMatrix.Translation);
								if (!grid.IsStatic)
									lcdDynamicGridIds.Add(grid.EntityId);
							}
						}
					}
					catch (InvalidOperationException) { } // grid blocks changed mid-iteration
					return false;
				});
			}
			catch (InvalidOperationException) { } // entity list changed mid-scan

			if (lcdPositions.Count == 0)
			{
				// No LCD found yet — stay active so writes begin as soon as one appears
				_anyPlayerNearby = true;
				_playerInCockpitOnLcdGrid = false;
				return;
			}

			float radiusSq = radius * radius;
			var players = new List<IMyPlayer>();
			MyAPIGateway.Players.GetPlayers(players);

			bool foundNearby = false;
			bool foundInCockpit = false;

			foreach (var p in players)
			{
				var character = p.Character;
				if (character == null || character.IsDead) continue;
				Vector3D charPos = character.WorldMatrix.Translation;

				bool isNear = lcdPositions.Any(pos => Vector3D.DistanceSquared(charPos, pos) <= radiusSq);
				if (isNear)
					foundNearby = true;

				// Check if this player is seated in a cockpit on a dynamic grid that has CCTV LCDs.
				// ControlledEntity is the cockpit/seat block when the player is seated.
				if (!foundInCockpit && lcdDynamicGridIds.Count > 0)
				{
					try
					{
						var controlled = p.Controller?.ControlledEntity as IMyCubeBlock;
						if (controlled != null && controlled.CubeGrid != null)
						{
							if (lcdDynamicGridIds.Contains(controlled.CubeGrid.EntityId))
								foundInCockpit = true;
						}
					}
					catch { } // entity may be orphaned mid-check
				}
			}

			_anyPlayerNearby = foundNearby;

			// Detect cockpit entry → trigger spool-up pause
			if (foundInCockpit && !_playerInCockpitOnLcdGrid)
			{
				_cockpitSpoolUpTicksRemaining = COCKPIT_SPOOL_UP_TICKS;
				Log.Info($"[{Name}] 🪑 Player entered cockpit on CCTV LCD grid — pausing writes for {COCKPIT_SPOOL_UP_TICKS / 60f:F1}s spool-up");
			}
			else if (!foundInCockpit && _playerInCockpitOnLcdGrid)
			{
				Log.Info($"[{Name}] 🪑 Player left cockpit on CCTV LCD grid — resuming normal writes");
				_cockpitSpoolUpTicksRemaining = 0;
			}
			_playerInCockpitOnLcdGrid = foundInCockpit;

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
