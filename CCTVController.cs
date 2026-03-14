using Sandbox.Engine.Utils;
using Sandbox.ModAPI;
using Sandbox.Game.Entities;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using VRage.ObjectBuilders;
using CCTVCommon;
using NLog;
using Torch.API;

namespace CCTVPlugin
{
    public class CCTVPlugin
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        // Matches a trailing "_L{n}" loop suffix on a camera display name.
        // Used to resolve the base LCD name when cameras are named e.g. "Test02_L1".
        private static readonly Regex _loopSuffixRegex =
            new Regex(@"_L(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string StripLoopSuffix(string name) =>
            _loopSuffixRegex.IsMatch(name)
                ? name.Substring(0, _loopSuffixRegex.Match(name).Index)
                : name;

        // Legacy single-client fields (for backward compatibility)
        private readonly string _cameraPrefix;
        private string _lcdPrefix = "LCD_TV";

        private readonly int _port;
        private readonly int _cameraRescanTicks;
        private readonly bool _enableHeartbeat;
        private readonly int _heartbeatIntervalTicks = 300;
        private readonly bool _enableAutoCameraCycling;
        private readonly int _cameraCycleIntervalTicks;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamReader _streamReader;

        // Second listener for CCTVMod (in-game spectator control)
        private TcpListener _modListener;
        private TcpClient _modClient;
        private NetworkStream _modStream;

        // Multi-client support
        private readonly bool _useMultiClientMode;
        private readonly List<CCTVClientConnection> _clientConnections = new List<CCTVClientConnection>();

        private bool _initialized = false;
        private int _scanTicks = 0;
        private int _heartbeatTicks = 0;
        private int _cameraCycleTicks = 0;
        private int _currentCameraIndex = 0;

        // In-game button control (MESSAGE_ID 12347: client → server)
        private const ushort CTRL_MESSAGE_ID = 12347;
        private const long   CTRL_MOD_CHANNEL = 123461234L; // server-side mod → plugin (same process)
        private bool _ctrlHandlerRegistered = false;
        private readonly ConcurrentQueue<Action> _pendingCameraActions = new ConcurrentQueue<Action>();
        private int _pbScanTicks = 0;
        private const int PB_SCAN_INTERVAL = 300; // scan every ~5 seconds (was 60 — caused heavy GC pressure)

        private readonly List<CameraInfo> _indexedCameras = new List<CameraInfo>();
        private readonly Dictionary<long, CameraInfo> _cameraByEntity = new Dictionary<long, CameraInfo>();

        private readonly Dictionary<long, int> _gridIndexByEntityId = new Dictionary<long, int>();
        private readonly Dictionary<long, int> _ownerIndexByIdentityId = new Dictionary<long, int>();
        private int _nextGridIndex = 1;
        private int _nextOwnerIndex = 1;

        // LCD panel tracking (keyed by EntityId, not DisplayName, so each camera gets its own setup)
        private readonly Dictionary<long, LcdDisplayInfo> _lcdsByEntityId = new Dictionary<long, LcdDisplayInfo>();
        private long _currentCameraEntityId = 0; // Track which camera is currently active (by EntityId)
        private bool _cameraIndexSent = false; // Track if we've sent the index to current client

        // Per-camera frame buffer — each camera keeps its last captured frame
        // so LCDs always show something even when the client moves to another camera
        private readonly Dictionary<long, BufferedFrame> _frameBuffer = new Dictionary<long, BufferedFrame>();

        // Async frame processing - thread-safe queue for decoded frames ready to write
        private readonly ConcurrentQueue<ProcessedFrame> _processedFrameQueue = new ConcurrentQueue<ProcessedFrame>();

        // Reusable line-offset buffers for SplitFrameIntoQuadrants — avoids
        // asciiFrame.Split('\n') which creates 362+ new strings on the game thread.
        private int[] _lineStarts;
        private int[] _lineLengths;

        // Frame queue system: Store incoming frames at capture FPS, display at lower display FPS
        // This allows client to send frames at 4 FPS while server displays at 2 FPS
        private readonly Dictionary<long, Queue<BufferedFrame>> _frameQueues = new Dictionary<long, Queue<BufferedFrame>>();
        private int _displayFpsTicks = 0; // Counter for display FPS timing
        private readonly int _displayFpsInterval; // Ticks between frame displays (e.g., 30 ticks for 2 FPS)

        private readonly ulong _configuredSpectatorSteamId;
        private readonly string _lcdFontTint;
        private readonly int _captureWidth;
        private readonly int _captureHeight;
        private readonly int _captureFps;
        private readonly bool _useColorMode;
        private readonly bool _useDithering;
        private readonly float _fontScale;
        private readonly bool _autoAdjustFontSize;
        private readonly CCTVPluginConfig _config;
        private readonly ITorchBase _torch;
        private readonly bool _enableVerboseFrameLogging;

        public CCTVPlugin(int port, int cameraRescanTicks, bool enableHeartbeat, bool enableAutoCameraCycling, int cameraCycleIntervalSeconds, ulong fakeClientSteamId, string cameraPrefix, string lcdPrefix, string lcdFontTint, int captureWidth, int captureHeight, int captureFps, bool useColorMode, bool useDithering, float fontScale, bool autoAdjustFontSize, CCTVPluginConfig config, ITorchBase torch)
        {
            _port = port;
            _cameraRescanTicks = cameraRescanTicks;
            _enableHeartbeat = enableHeartbeat;
            _enableAutoCameraCycling = enableAutoCameraCycling;
            _cameraCycleIntervalTicks = cameraCycleIntervalSeconds * 60;
            _configuredSpectatorSteamId = fakeClientSteamId;
            _cameraPrefix = string.IsNullOrWhiteSpace(cameraPrefix) ? "LCD_TVCamera" : cameraPrefix;
            _lcdPrefix = string.IsNullOrWhiteSpace(lcdPrefix) ? "LCD_TV" : lcdPrefix;
            _lcdFontTint = string.IsNullOrWhiteSpace(lcdFontTint) ? "255,255,255" : lcdFontTint;
            _captureWidth = captureWidth;
            _captureHeight = captureHeight;
            _captureFps = captureFps;
            _useColorMode = useColorMode;
            _useDithering = useDithering;
            _fontScale = fontScale;
            _autoAdjustFontSize = autoAdjustFontSize;
            _config = config;
            _torch = torch;
            _enableVerboseFrameLogging = config?.EnableVerboseFrameLogging ?? false;

            // Check if multi-client mode is enabled
            _useMultiClientMode = config?.UseMultiClientMode ?? false;

            // Calculate display FPS interval (ticks between frame displays)
            // 60 ticks = 1 second, so for 2 FPS: 60/2 = 30 ticks
            int displayFps = Math.Max(1, config?.DisplayFps ?? 2);
            _displayFpsInterval = 60 / displayFps;
            Log.Info($"CCTVPlugin: Display FPS set to {displayFps} ({_displayFpsInterval} ticks per frame)");

            if (_useMultiClientMode)
            {
                // Initialize multiple client connections
                var instances = config.GetClientInstances();
                Log.Info($"🔀 Multi-Client Mode ENABLED: {instances.Count} instance(s) configured");

                foreach (var instanceConfig in instances)
                {
                    if (!instanceConfig.Enabled)
                    {
                        Log.Info($"⏭️ Skipping disabled instance: {instanceConfig.Name}");
                        continue;
                    }

                    var connection = new CCTVClientConnection(instanceConfig, config, torch);
                    _clientConnections.Add(connection);
                    Log.Info($"✅ Registered CCTVCapture instance: {instanceConfig.Name} (Port: {instanceConfig.TcpPort}, Prefix: {instanceConfig.CameraPrefix})");
                }
            }
            else
            {
                // Legacy single-client mode
                Log.Info($"🔗 Legacy Single-Client Mode (Port: {_port}, Prefix: {_cameraPrefix})");

                if (_configuredSpectatorSteamId == 0)
                {
                    Log.Warn("⚠️ No SpectatorSteamId configured! Auto-teleport will be DISABLED!");
                }
            }

            Log.Info($"CCTVPlugin: LCD font tint set to '{_lcdFontTint}' (R,G,B)");
            Log.Info($"CCTVPlugin: Verbose frame logging = {(_enableVerboseFrameLogging ? "ENABLED" : "DISABLED")}");
        }

        // ⭐ Torch plugin entrypoint calls this
        public void Start()
        {
            Log.Info("CCTVPlugin: Start called");

            if (_useMultiClientMode)
            {
                // Start all client connections
                Log.Info($"🔧 Starting {_clientConnections.Count} client connections...");
                foreach (var connection in _clientConnections)
                {
                    try
                    {
                        Log.Info($"🔧 Calling Start() on {connection.Name}...");
                        connection.Start();
                        Log.Info($"✅ Start() completed for {connection.Name}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"❌ Failed to start connection: {connection.Name}");
                    }
                }

                // Prime the scan tick so Update() triggers a camera scan on the very first tick.
                // RescanCameras() would always fail here because MyAPIGateway.Entities is not
                // available during Plugin.Init() — the game session hasn't loaded yet.
                _scanTicks = _cameraRescanTicks - 1;

                // ⚡ Mark as initialized so Update() doesn't try to call Initialize()
                _initialized = true;
                Log.Info("✅ Multi-client mode: Marked as initialized (skipping legacy Initialize)");
            }
            else
            {
                // Single-client mode: use legacy Initialize()
                Log.Info("🔵 Single-client mode: Calling Initialize()");
                Initialize();
            }
        }

        /// <summary>
        /// Send current capture/config settings to the connected CCTVCapture (if any).
        /// This allows runtime config changes in the Torch UI to be pushed to the client
        /// without requiring the client process to be restarted.
        /// </summary>
        public void SendConfigToClient()
        {
            if (_client == null || _stream == null || !_client.Connected)
            {
                Log.Debug("SendConfigToClient: No connected fake client");
                return;
            }

            try
            {
                string message = $"CONFIG LcdFontTint={_lcdFontTint} CaptureWidth={_config.CaptureWidth} CaptureHeight={_config.CaptureHeight} CaptureFps={_captureFps} UseColorMode={_useColorMode} UseDithering={_useDithering} DitherMode={_config.DitherMode} PostProcessMode={_config.PostProcessMode} GridPostProcessMode={_config.GridPostProcessMode} LcdGridResolution={_config.LcdGridResolution} DesaturateColorMode={_config.DesaturateColorMode} NightVisionMode={_config.NightVisionMode} CropCaptureToSquare={_config.CropCaptureToSquare}";
                Send(message);
                Log.Info($"Sent CONFIG to fake client: {_config.CaptureWidth}x{_config.CaptureHeight} @{_captureFps}fps color={_useColorMode} dither={_config.DitherMode} postproc={_config.PostProcessMode} gridPostProc={_config.GridPostProcessMode}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to send config to fake client: {ex.Message}");
            }
        }

        // ⭐ Torch plugin calls this every tick via Plugin.Update()
        public void Update()
        {
            if (!_initialized)
            {
                // In multi-client mode, Start() should have set _initialized=true
                // If we're here in multi-client mode, something went wrong
                if (_useMultiClientMode)
                {
                    Log.Error("❌ Update() called before initialization in MULTI-CLIENT mode! This should not happen.");
                    Log.Error("❌ _initialized should have been set to true in Start()");
                    _initialized = true; // Prevent spam
                    return;
                }

                // Single-client mode: Initialize on first Update() call
                Initialize();
                _initialized = true;
            }

            // Handle multi-client mode
            if (_useMultiClientMode)
            {
                // Register camera control message handler on first tick (MyAPIGateway ready by now)
                if (!_ctrlHandlerRegistered)
                {
                    // MyAPIGateway members can be null during session load/transition
                    if (MyAPIGateway.Utilities == null || MyAPIGateway.Multiplayer == null)
                        return; // retry next tick

                    // Server-side (dedicated): button actions fire server-side and use SendModMessage
                    MyAPIGateway.Utilities.RegisterMessageHandler(CTRL_MOD_CHANNEL, OnCameraControlModMessage);
                    // Client-side (listen server / single-player): button actions send over network
                    MyAPIGateway.Multiplayer.RegisterMessageHandler(CTRL_MESSAGE_ID, OnCameraControlMessage);
                    _ctrlHandlerRegistered = true;
                    Log.Info($"✅ Camera control message handler registered (ID: {CTRL_MESSAGE_ID}, ModChannel: {CTRL_MOD_CHANNEL})");
                }

                // Drain actions queued by the message handler (game-thread safe)
                while (_pendingCameraActions.TryDequeue(out var act))
                {
                    try { act(); }
                    catch (Exception ex) { Log.Error(ex, "Error executing queued camera action"); }
                }

                // Poll Programmable Blocks for CCTV commands (mod-free button control)
                if (++_pbScanTicks >= PB_SCAN_INTERVAL)
                {
                    _pbScanTicks = 0;
                    ScanProgrammableBlocksForCommands();
                }

                // Update each client connection (process queued frames)
                foreach (var connection in _clientConnections)
                {
                    try
                    {
                        connection.Update();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error updating connection: {connection.Name}");
                    }
                }

                // Rescan cameras periodically
                if (++_scanTicks >= _cameraRescanTicks)
                {
                    _scanTicks = 0;
                    try
                    {
                        RescanCameras();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error during RescanCameras (entity state may have changed during auto-save)");
                    }
                }

                return; // Skip legacy single-client logic
            }

            // Legacy single-client mode below
            // Read complete lines from stream (handles large FRAME commands)
            if (_client != null && _client.Connected && _streamReader != null)
            {
                try
                {
                    // Check if data is available before trying to read
                    while (_stream.DataAvailable)
                    {
                        string line = _streamReader.ReadLine();
                        if (line != null)
                        {
                            HandleCommand(line.Trim());
                        }
                    }
                }
                catch (IOException)
                {
                    // Client disconnected
                    CleanupClient();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading from client");
                }
            }

            // Process any frames that were decoded on background threads
            // (LCD writes MUST happen on game thread for SE API compatibility)
            ProcessQueuedFrames();

            // REMOVED: DisplayQueuedFrames() - now writing frames directly in ProcessQueuedFrames()
            // No more buffering or delayed display!

            if (++_scanTicks >= _cameraRescanTicks)
            {
                _scanTicks = 0;
                try
                {
                    RescanCameras();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during RescanCameras");
                }
            }

            if (_enableHeartbeat && ++_heartbeatTicks >= _heartbeatIntervalTicks)
            {
                _heartbeatTicks = 0;
                Log.Info($"CCTVPlugin: Heartbeat {DateTime.UtcNow:O}");
            }

            // Auto camera cycling (allow with 1 camera for testing)
            if (_enableAutoCameraCycling && _client != null && _client.Connected && _indexedCameras.Count >= 1)
            {
                if (++_cameraCycleTicks >= _cameraCycleIntervalTicks)
                {
                    _cameraCycleTicks = 0;
                    CycleToNextCamera();
                }
            }
        }

        /// <summary>
        /// Routes CAMCTRL messages from in-game button panels to the correct CCTVClientConnection.
        /// Format: "CAMCTRL|NEXT|Test01", "CAMCTRL|PREV|Test01", "CAMCTRL|RESET|Test01"
        /// Called on network thread — queues work for the game thread.
        /// </summary>
        /// <summary>
        /// Scans all Programmable Blocks for CustomData commands written by a simple PB script.
        /// Format: "NEXT|Test01", "PREV|Test01", or "RESET|Test01"
        /// Clears the CustomData after reading so each press fires exactly once.
        /// No mod required — button panel triggers the PB, PB writes CustomData, plugin reads it.
        /// </summary>
        private void ScanProgrammableBlocksForCommands()
        {
            // Skip scan entirely when no connections have LiveFeedLcdName configured
            // (button controls are impossible without it, and this scan is expensive)
            bool anyLiveFeed = false;
            foreach (var c in _clientConnections)
            {
                if (!string.IsNullOrEmpty(c.LiveFeedLcdName))
                {
                    anyLiveFeed = true;
                    break;
                }
            }
            if (!anyLiveFeed) return;

            try
            {
                var entities = MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    var grid = entity as MyCubeGrid;
                    if (grid == null) continue;
                    foreach (var block in grid.GetFatBlocks())
                    {
                        var pb = block as IMyProgrammableBlock;
                        if (pb == null) continue;

                        string data = (pb.CustomData ?? "").Trim();
                        if (string.IsNullOrEmpty(data)) continue;

                        string[] parts = data.Split('|');
                        if (parts.Length < 2) continue;

                        string action  = parts[0].Trim().ToUpperInvariant();
                        string lcdName = parts[1].Trim();

                        if (action != "NEXT" && action != "PREV" && action != "RESET") continue;
                        if (!_clientConnections.Any(c => string.Equals(c.LiveFeedLcdName, lcdName, StringComparison.OrdinalIgnoreCase))) continue;

                        // Clear immediately so the same command doesn't fire twice
                        pb.CustomData = "";

                        Log.Info($"🎮 PB command: {action}|{lcdName} (from '{pb.CustomName}')");
                        ProcessCameraControl($"CAMCTRL|{action}|{lcdName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error scanning PBs for CCTV commands");
            }
        }

        private void OnCameraControlModMessage(object data)
        {
            try
            {
                string msg = data as string;
                if (msg != null) ProcessCameraControl(msg);
            }
            catch (Exception ex) { Log.Error(ex, "Error in OnCameraControlModMessage"); }
        }

        private void OnCameraControlMessage(byte[] data)
        {
            try { ProcessCameraControl(Encoding.UTF8.GetString(data)); }
            catch (Exception ex) { Log.Error(ex, "Error in OnCameraControlMessage"); }
        }

        private void ProcessCameraControl(string msg)
        {
            try
            {
                Log.Info($"🎮 CAMCTRL received: '{msg}' (connections: {_clientConnections.Count})");

                string[] parts = msg.Split('|');
                if (parts.Length < 3 || parts[0] != "CAMCTRL")
                {
                    Log.Warn($"CAMCTRL: malformed message '{msg}'");
                    return;
                }

                string action = parts[1];
                string lcdName = parts[2];

                Log.Info($"🎮 CAMCTRL routing: action='{action}' lcdName='{lcdName}'");
                foreach (var c in _clientConnections)
                    Log.Info($"  connection '{c.Name}' LiveFeedLcdName='{c.LiveFeedLcdName}'");

                var connection = _clientConnections.FirstOrDefault(c =>
                    string.Equals(c.LiveFeedLcdName, lcdName, StringComparison.OrdinalIgnoreCase));

                if (connection == null)
                {
                    Log.Warn($"CAMCTRL: no connection found for LiveFeedLcdName='{lcdName}'");
                    return;
                }

                Log.Info($"🎮 CAMCTRL queuing '{action}' on [{connection.Name}]");
                _pendingCameraActions.Enqueue(() =>
                {
                    switch (action)
                    {
                        case "NEXT":     connection.ManualNextCamera();    break;
                        case "PREV":     connection.ManualPreviousCamera(); break;
                        case "RESET":    connection.ResetAutoCycle();       break;
                        case "NEXTLOOP": connection.NextLoop();             break;
                        case "PREVLOOP": connection.PrevLoop();             break;
                        default: Log.Warn($"CAMCTRL: unknown action '{action}'"); break;
                    }
                });
            }
            catch (Exception ex) { Log.Error(ex, "Error in ProcessCameraControl"); }
        }

        private void Initialize()
        {
            // ⚠️ Skip initialization in multi-client mode (per-client listeners are already started)
            if (_useMultiClientMode)
            {
                Log.Info("⚠️ Initialize() called but multi-client mode is active - skipping legacy listener");
                return;
            }

            if (_listener != null)
            {
                Log.Info("CCTVPlugin: Already initialized, skipping");
                return;
            }

            try
            {
                // Main listener for CCTVCapture.exe
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _listener.BeginAcceptTcpClient(OnClientConnected, null);
                Log.Info($"CCTVPlugin: Listening on port {_port} for CCTVCapture.exe");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Log.Error($"CCTVPlugin: Port is already in use. Please change the port in CCTVPlugin.cfg or close the other application.");
                _listener = null;
            }
            catch (Exception e)
            {
                Log.Error(e, "CCTVPlugin Init error");
                _listener = null;
            }
        }

        private void OnClientConnected(IAsyncResult ar)
        {
            TcpClient newClient = null;

            try
            {
                newClient = _listener.EndAcceptTcpClient(ar);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CCTVPlugin OnClientConnected error");
            }

            // Always replace old connection — single CCTVCapture.exe handles all cameras
            // Old connection may appear "Connected" even after client exited (stale TCP state)
            if (_client != null)
            {
                Log.Info("Cleaning up previous client connection");
                CleanupClient();
            }

            _client = newClient;
            if (_client != null)
            {
                _client.SendTimeout = 500; // 500ms — legacy mode fallback, shorter than multi-client
                _client.NoDelay = true;
                _stream = _client.GetStream();
                _streamReader = new StreamReader(_stream, Encoding.UTF8);
                Log.Info("CCTVPlugin: CCTVCapture.exe connected");

                // Reset so new client gets camera index on next rescan
                _cameraIndexSent = false;
                _cameraCycleTicks = 0;
                _currentCameraIndex = 0;
            }

            // Accept next connection attempt
            try
            {
                _listener?.BeginAcceptTcpClient(OnClientConnected, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to begin accepting next client");
            }
        }

        private void HandleCommand(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            string[] parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string cmd = parts[0].ToUpperInvariant();

            switch (cmd)
            {
                case "PING":
                    Send("PONG");
                    break;

                case "LISTCAMERAS":
                    ListCameras();
                    break;

                case "GETCONFIG":
                    Log.Info($"Client requested config");
                    Send($"CONFIG LcdFontTint={_lcdFontTint} CaptureWidth={_config.CaptureWidth} CaptureHeight={_config.CaptureHeight} CaptureFps={_captureFps} UseColorMode={_useColorMode} UseDithering={_useDithering} DitherMode={_config.DitherMode} PostProcessMode={_config.PostProcessMode} GridPostProcessMode={_config.GridPostProcessMode} LcdGridResolution={_config.LcdGridResolution}");
                    return;

                case "CAMERA":
                    if (parts.Length == 2)
                        HandleCameraCommand(parts[1]);
                    else
                        Send("ERROR CAMERA requires an index or entityId");
                    break;

                case "FRAME":
                    // FRAME <width> <height> <mode> <data>
                    if (parts.Length >= 4)
                    {
                        HandleFrameCommand(parts);
                    }
                    else
                    {
                        Send("ERROR FRAME requires: width height mode data");
                    }
                    return; // Don't send READY for FRAME commands (high frequency)

                default:
                    Send($"ERROR Unknown command: {cmd}");
                    break;
            }

            Send("READY");
        }

        private void HandleCameraCommand(string arg)
        {
            if (int.TryParse(arg, out int idx))
            {
                if (idx <= 0 || idx > _indexedCameras.Count)
                {
                    Send($"ERROR Invalid camera index {idx}");
                    return;
                }

                var camInfo = _indexedCameras[idx - 1];
                TeleportToCameraPosition(camInfo);
            }
            else if (long.TryParse(arg, out long entityId))
            {
                if (!_cameraByEntity.TryGetValue(entityId, out var camInfo))
                {
                    Send($"ERROR Camera {entityId} not found");
                    return;
                }

                TeleportToCameraPosition(camInfo);
            }
            else
            {
                Send($"ERROR Invalid CAMERA argument {arg}");
            }
        }

        private void TeleportToCameraPosition(CameraInfo camInfo)
        {
            var entity = MyAPIGateway.Entities.GetEntityById(camInfo.EntityId);
            if (entity == null)
            {
                Send($"ERROR Camera entity {camInfo.EntityId} no longer exists");
                return;
            }

            // Get the camera position AND orientation
            MatrixD m = entity.WorldMatrix;
            Vector3D pos = m.Translation;
            Vector3D forward = m.Forward;
            Vector3D up = m.Up;

            // Send to mod for camera view control (includes entity ID for direct camera view)
            SendCameraPositionToClient(camInfo.DisplayName, camInfo.EntityId, pos, forward, up);

            // Track current camera by EntityId (not DisplayName, so multiple cameras with same name work)
            _currentCameraEntityId = camInfo.EntityId;

            // Partially clear this camera's frame queue when it becomes active
            // Keep the most recent 3-5 frames to provide continuity during camera switch
            // This prevents blank/frozen display while waiting for new frames
            if (_frameQueues.TryGetValue(camInfo.EntityId, out var queue))
            {
                int oldFrameCount = queue.Count;
                const int KEEP_FRAMES = 4; // Keep 4 most recent frames (2 seconds @ 2 FPS display)

                while (queue.Count > KEEP_FRAMES)
                {
                    queue.Dequeue(); // Remove oldest frames
                }

                Log.Debug($"Cleared old frames for newly active camera {camInfo.EntityId} (removed {oldFrameCount - queue.Count}, kept {queue.Count} recent frames)");
            }

            Send($"OK Switched to {camInfo.DisplayName} ({camInfo.EntityId})");
        }

        private void CycleToNextCamera()
        {
            if (_indexedCameras.Count == 0)
            {
                Log.Debug("No cameras available for cycling");
                return;
            }

            // Find the next camera that has players in sync distance
            // This skips cameras in remote/empty areas to optimize cycling
            int attempts = 0;
            int maxAttempts = _indexedCameras.Count; // Don't loop forever

            while (attempts < maxAttempts)
            {
                // Move to next camera
                _currentCameraIndex = (_currentCameraIndex + 1) % _indexedCameras.Count;
                var nextCamera = _indexedCameras[_currentCameraIndex];
                attempts++;

                // Check if any players are within sync distance of this camera's LCDs
                if (!IsCameraRelevantToPlayers(nextCamera))
                {
                    Log.Debug($"Skipping camera {nextCamera.DisplayName} (EntityId: {nextCamera.EntityId}) - no players in sync distance");
                    continue; // Skip to next camera
                }

                // Found a camera with nearby players!
                Log.Info($"🎬 Auto-cycling to camera {_currentCameraIndex + 1}/{_indexedCameras.Count}: {nextCamera.DisplayName} (EntityId: {nextCamera.EntityId})");

                // Get camera position AND orientation
                var entity = MyAPIGateway.Entities.GetEntityById(nextCamera.EntityId);
                if (entity != null)
                {
                    MatrixD m = entity.WorldMatrix;
                    Vector3D pos = m.Translation;
                    Vector3D forward = m.Forward;
                    Vector3D up = m.Up;

                    // Send multiplayer message to client-side mod (camera view control)
                    SendCameraPositionToClient(nextCamera.DisplayName, nextCamera.EntityId, pos, forward, up);
                }

                // Track current camera by EntityId (not DisplayName)
                _currentCameraEntityId = nextCamera.EntityId;

                // Partially clear this camera's frame queue when it becomes active
                // Keep the most recent 3-5 frames to provide continuity during camera switch
                // This prevents blank/frozen display while waiting for new frames
                if (_frameQueues.TryGetValue(nextCamera.EntityId, out var queue))
                {
                    int oldFrameCount = queue.Count;
                    const int KEEP_FRAMES = 4; // Keep 4 most recent frames (2 seconds @ 2 FPS display)

                    while (queue.Count > KEEP_FRAMES)
                    {
                        queue.Dequeue(); // Remove oldest frames
                    }

                    Log.Debug($"Cleared old frames for newly active camera {nextCamera.EntityId} (removed {oldFrameCount - queue.Count}, kept {queue.Count} recent frames)");
                }

                // Send CAMERA command with 1-based index to CCTVCapture.exe
                // This allows cycling between cameras with the same display name
                Send($"CAMERA {_currentCameraIndex + 1}");

                // Refresh all other LCDs from their last buffered frame
                // so they keep showing their camera's view while we capture this one
                RefreshAllLcdsFromBuffer();

                Log.Info($"✅ Camera switched to: {nextCamera.DisplayName} (Entity: {nextCamera.EntityId}, Index: {_currentCameraIndex + 1})");
                return; // Successfully switched camera
            }

            // If we get here, NO cameras have players nearby
            Log.Info("⏸️ No cameras have players in sync distance - pausing cycling");
        }

        /// <summary>
        /// Checks if a camera is relevant to any online players (within sync distance of its LCDs).
        /// Returns false if no players can see this camera's LCDs, allowing us to skip it during cycling.
        /// </summary>
        private bool IsCameraRelevantToPlayers(CameraInfo camera)
        {
            try
            {
                // Get all online players
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players, p => p != null && !p.IsBot);

                if (players.Count == 0)
                {
                    // No players online - skip all cameras
                    return false;
                }

                // Get the camera's grid to find its LCDs
                var cameraEntity = MyAPIGateway.Entities.GetEntityById(camera.EntityId);
                if (cameraEntity == null)
                    return false;

                var cameraGrid = cameraEntity.Parent as MyCubeGrid;
                if (cameraGrid == null)
                    return false;

                // Check if any LCD on the camera's grid is within sync distance of any player
                // Sync distance is typically 3000m (configurable in server settings)
                const double SYNC_DISTANCE_SQ = 3000 * 3000; // Squared for performance (avoid sqrt)

                Vector3D gridPosition = cameraGrid.PositionComp.GetPosition();

                foreach (var player in players)
                {
                    if (player.Character == null)
                        continue;

                    Vector3D playerPos = player.Character.PositionComp.GetPosition();
                    double distanceSq = Vector3D.DistanceSquared(playerPos, gridPosition);

                    if (distanceSq <= SYNC_DISTANCE_SQ)
                    {
                        // At least one player is within sync distance
                        return true;
                    }
                }

                // No players within sync distance
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error checking camera relevance for {camera.DisplayName}");
                return true; // On error, assume camera is relevant (fail-safe)
            }
        }

        /// <summary>
        /// Sends camera position to the fake client's mod via multiplayer messaging.
        /// Uses SendMessageTo so ONLY the fake client receives it — not other players.
        /// </summary>
        private void SendCameraPositionToClient(string cameraName, long entityId, Vector3D position, Vector3D forward, Vector3D up)
        {
            if (_configuredSpectatorSteamId == 0)
            {
                Log.Warn("Cannot send camera position: SpectatorSteamId not configured");
                return;
            }

            try
            {
                // Format: GOTO|SteamID|CameraName|EntityID|X|Y|Z|FwdX|FwdY|FwdZ|UpX|UpY|UpZ
                string message = $"GOTO|{_configuredSpectatorSteamId}|{cameraName}|{entityId}|{position.X}|{position.Y}|{position.Z}|{forward.X}|{forward.Y}|{forward.Z}|{up.X}|{up.Y}|{up.Z}";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(message);

                const ushort MESSAGE_ID = 12346;
                MyAPIGateway.Multiplayer.SendMessageTo(MESSAGE_ID, data, _configuredSpectatorSteamId);

                Log.Info($"📡 Sent camera position to fake client ({_configuredSpectatorSteamId}): {cameraName} entity={entityId}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to send multiplayer message: {ex.Message}");
            }
        }

        private void TeleportCCTVCaptureToPosition(Vector3D position)
        {
            // Safety check: If no SteamID is configured, refuse to teleport anyone
            if (_configuredSpectatorSteamId == 0)
            {
                Log.Warn("⚠️ Cannot teleport: SpectatorSteamId not configured! Set it in CCTVPlugin.cfg");
                return;
            }

            try
            {
                // Find the fake client player
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                if (players == null || players.Count == 0)
                {
                    Log.Debug("No players online to teleport");
                    return;
                }

                IMyPlayer fakeClientPlayer = null;

                // ONLY find the player with the EXACT configured SteamID
                foreach (var player in players)
                {
                    if (player.SteamUserId == _configuredSpectatorSteamId)
                    {
                        fakeClientPlayer = player;
                        Log.Debug($"Found configured fake client: {player.DisplayName} (SteamID: {player.SteamUserId})");
                        break;
                    }
                }

                if (fakeClientPlayer == null)
                {
                    Log.Debug($"Configured fake client (SteamID: {_configuredSpectatorSteamId}) is not online");
                    return;
                }

                // Get the player's character
                var character = fakeClientPlayer.Character;
                if (character == null)
                {
                    Log.Warn($"Fake client player '{fakeClientPlayer.DisplayName}' has no character");
                    return;
                }

                // Teleport the character
                character.SetPosition(position);

                Log.Info($"✅ Teleported '{fakeClientPlayer.DisplayName}' to {position.X:F1}, {position.Y:F1}, {position.Z:F1}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error teleporting fake client player");
            }
        }

        private void TeleportCCTVCaptureToCameraWithOrientation(Vector3D position, Vector3D forward, Vector3D up)
        {
            // Safety check: If no SteamID is configured, refuse to teleport anyone
            if (_configuredSpectatorSteamId == 0)
            {
                Log.Warn("⚠️ Cannot teleport: SpectatorSteamId not configured! Set it in CCTVPlugin.cfg");
                return;
            }

            try
            {
                // Find the fake client player
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);

                if (players == null || players.Count == 0)
                {
                    Log.Debug("No players online to teleport");
                    return;
                }

                IMyPlayer fakeClientPlayer = null;

                // ONLY find the player with the EXACT configured SteamID
                foreach (var player in players)
                {
                    if (player.SteamUserId == _configuredSpectatorSteamId)
                    {
                        fakeClientPlayer = player;
                        Log.Debug($"Found configured fake client: {player.DisplayName} (SteamID: {player.SteamUserId})");
                        break;
                    }
                }

                if (fakeClientPlayer == null)
                {
                    Log.Debug($"Configured fake client (SteamID: {_configuredSpectatorSteamId}) is not online");
                    return;
                }

                // Get the player's character
                var character = fakeClientPlayer.Character;
                if (character == null)
                {
                    Log.Warn($"Fake client player '{fakeClientPlayer.DisplayName}' has no character");
                    return;
                }

                // 🎨 TEST: Try to make character less visible by switching model
                // This tests if we can control character appearance before building custom mod
                TrySwitchCharacterModel(character);

                // First teleport to position
                character.SetPosition(position);

                // Then set orientation using physics
                var physicsBody = character.Physics;
                if (physicsBody != null)
                {
                    // Create orientation matrix
                    MatrixD newOrientation = MatrixD.CreateWorld(position, forward, up);

                    // Set both the character matrix and head orientation
                    character.SetWorldMatrix(newOrientation);

                    // Force physics update
                    physicsBody.ClearSpeed();

                    Log.Info($"✅ Teleported '{fakeClientPlayer.DisplayName}' to {position.X:F1}, {position.Y:F1}, {position.Z:F1} facing camera direction");
                }
                else
                {
                    // Fallback: just set position
                    Log.Warn("Character has no physics body, orientation may not be set correctly");
                    Log.Info($"✅ Teleported '{fakeClientPlayer.DisplayName}' to {position.X:F1}, {position.Y:F1}, {position.Z:F1} (position only)");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error teleporting fake client player with orientation");
            }
        }

        /// <summary>
        /// TEST METHOD: Switch character model (proof of concept for invisible character)
        /// This actually respawns the character with a different model
        /// </summary>
        private void TrySwitchCharacterModel(IMyCharacter character)
        {
            try
            {
                // Get the internal character object
                var internalChar = character as Sandbox.Game.Entities.Character.MyCharacter;
                if (internalChar == null)
                {
                    Log.Warn("Could not access internal character for model switch");
                    return;
                }

                // Get current character definition
                var currentDef = internalChar.Definition;
                Log.Info($"Current character definition: {currentDef?.Id.SubtypeName ?? "Unknown"}");

                // Try to get the camera character definition
                string targetSubtype = "CameraCharacter";
                Log.Info($"🎥 Checking for camera character model '{targetSubtype}'...");

                // Get the target character definition
                var defId = new MyDefinitionId(typeof(MyObjectBuilder_Character), targetSubtype);
                var newDef = MyDefinitionManager.Static.GetDefinition(defId) as Sandbox.Definitions.MyCharacterDefinition;

                if (newDef != null)
                {
                    Log.Info($"✅ Found camera character definition: {newDef.Id.SubtypeName}");
                    Log.Info($"   Model: {newDef.Model}");
                    Log.Info($"   Mass: {newDef.Mass}");
                    Log.Info($"   📷 Character will appear as tiny camera block!");
                    Log.Info($"   📝 Player must manually respawn with this character in spawn menu");
                }
                else
                {
                    Log.Warn($"Could not find character definition '{targetSubtype}'");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Character model switch failed: {ex.Message}");
                // Not critical - character will still work normally
            }
        }

        private void SendCameraIndex()
        {
            if (_indexedCameras.Count == 0)
            {
                Log.Warn("SendCameraIndex: No cameras to index yet");
                Send("INDEX_COMPLETE");
                return;
            }

            Log.Info($"SendCameraIndex: Sending index with {_indexedCameras.Count} cameras...");

            foreach (var camera in _indexedCameras)
            {
                var entity = MyAPIGateway.Entities.GetEntityById(camera.EntityId);
                if (entity != null)
                {
                    MatrixD m = entity.WorldMatrix;
                    Vector3D pos = m.Translation;

                    // Send to CCTVCapture.exe via TCP
                    string indexMsg = $"INDEX {camera.DisplayName} {pos.X:F2} {pos.Y:F2} {pos.Z:F2}";
                    Send(indexMsg);

                    // Also send to client-side mod via multiplayer
                    SendCameraIndexItemToClient(camera.DisplayName, pos);
                }
            }

            Send("INDEX_COMPLETE");
            SendCameraIndexCompleteToClient();
            Log.Info("SendCameraIndex: Complete");
        }

        /// <summary>
        /// Sends camera index to client-side mod via multiplayer
        /// </summary>
        private void SendCameraIndexItemToClient(string cameraName, Vector3D position)
        {
            if (_configuredSpectatorSteamId == 0) return;

            try
            {
                string message = $"INDEX|{_configuredSpectatorSteamId}|{cameraName}|{position.X}|{position.Y}|{position.Z}";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
                const ushort MESSAGE_ID = 12346;
                MyAPIGateway.Multiplayer.SendMessageTo(MESSAGE_ID, data, _configuredSpectatorSteamId);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to send camera index to client: {ex.Message}");
            }
        }

        private void SendCameraIndexCompleteToClient()
        {
            if (_configuredSpectatorSteamId == 0) return;

            try
            {
                string message = $"INDEX_COMPLETE|{_configuredSpectatorSteamId}";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
                const ushort MESSAGE_ID = 12346;
                MyAPIGateway.Multiplayer.SendMessageTo(MESSAGE_ID, data, _configuredSpectatorSteamId);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to send index complete to client: {ex.Message}");
            }
        }

        private void SendCameraIndexToMod()
        {
            if (_indexedCameras.Count == 0)
            {
                Log.Warn("SendCameraIndexToMod: No cameras to index yet");
                SendToMod("INDEX_COMPLETE");
                return;
            }

            Log.Info($"SendCameraIndexToMod: Sending index with {_indexedCameras.Count} cameras...");

            foreach (var camera in _indexedCameras)
            {
                var entity = MyAPIGateway.Entities.GetEntityById(camera.EntityId);
                if (entity != null)
                {
                    MatrixD m = entity.WorldMatrix;
                    Vector3D pos = m.Translation;

                    // Send: INDEX <name> <x> <y> <z>
                    string indexMsg = $"INDEX {camera.DisplayName} {pos.X:F2} {pos.Y:F2} {pos.Z:F2}";
                    SendToMod(indexMsg);
                }
            }

            SendToMod("INDEX_COMPLETE");
            Log.Info("SendCameraIndexToMod: Complete");
        }

        private void HandleFrameCommand(string[] parts)
        {
            try
            {
                // Parse: FRAME <width> <height> <mode> <base64_data>
                if (!int.TryParse(parts[1], out int width) || !int.TryParse(parts[2], out int height))
                {
                    Log.Warn("Invalid FRAME dimensions");
                    return;
                }

                string mode = parts[3]; // GRAY, COLOR16, COLORRGB
                string base64Data = parts[4];
                long cameraEntityId = _currentCameraEntityId;

                if (cameraEntityId == 0)
                {
                    Log.Warn("FRAME received but _currentCameraEntityId is 0!");
                    return;
                }

                // Determine frame type based on dimensions
                int singleLcdSize = _config.LcdSingleResolution;
                int gridSize = _config.LcdGridResolution;

                bool isSingleLcdFrame = (width == singleLcdSize && height == singleLcdSize);
                bool isGridFrame = (width == gridSize && height == gridSize);

                // Log receipt (cheap operation, keep on game thread)
                if (_enableVerboseFrameLogging)
                {
                    string frameType = isSingleLcdFrame ? "Single LCD" : (isGridFrame ? "Grid" : "Generic");
                    Log.Info($"[FRAME] EntityId={cameraEntityId} {frameType}: {width}×{height} Mode={mode}");
                }

                // ⚡ ASYNC PROCESSING: Move CPU-heavy work (Base64 decode + string processing) to background thread
                // This prevents blocking the game thread for 50+ cameras @ 2 FPS = 100 frames/sec
                Task.Run(() =>
                {
                    try
                    {
                        // CPU-heavy: Base64 decode + optional GZip decompress (runs on thread pool)
                        byte[] bytes = Convert.FromBase64String(base64Data);
                        string ascii;
                        if (mode.EndsWith("GZ", StringComparison.OrdinalIgnoreCase))
                        {
                            using (var ms = new MemoryStream(bytes))
                            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                            using (var outMs = new MemoryStream(bytes.Length * 4))
                            {
                                gz.CopyTo(outMs);
                                ascii = Encoding.UTF8.GetString(outMs.GetBuffer(), 0, (int)outMs.Length);
                            }
                        }
                        else
                        {
                            ascii = Encoding.UTF8.GetString(bytes);
                        }

                        // Queue the decoded frame for game thread to process
                        _processedFrameQueue.Enqueue(new ProcessedFrame
                        {
                            CameraEntityId = cameraEntityId,
                            DecodedAscii = ascii,
                            Mode = mode,
                            Width = width,
                            Height = height,
                            IsSingleLcdFrame = isSingleLcdFrame,
                            IsGridFrame = isGridFrame
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Async frame decode error for camera {cameraEntityId}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing FRAME command");
            }
        }

        /// <summary>
        /// Processes frames that were decoded on background threads.
        /// MUST run on game thread for SE API compatibility (LCD writes).
        /// Called every tick from Update().
        /// DIRECT WRITE: No buffering, frames written immediately to LCDs.
        /// </summary>
        private void ProcessQueuedFrames()
        {
            // Process all queued frames (up to 100 per tick to avoid lag spikes)
            int processed = 0;
            const int MAX_PER_TICK = 100;

            while (processed < MAX_PER_TICK && _processedFrameQueue.TryDequeue(out var processedFrame))
            {
                processed++;

                try
                {
                    // DIRECT WRITE: Write frame immediately to LCDs without buffering
                    // This ensures NO old frames are shown - always live!

                    // Find LCD info for this camera
                    if (!_lcdsByEntityId.TryGetValue(processedFrame.CameraEntityId, out var lcdInfo))
                    {
                        lcdInfo = FindLcdsForCamera(processedFrame.CameraEntityId);
                        _lcdsByEntityId[processedFrame.CameraEntityId] = lcdInfo;

                        if (!lcdInfo.IsValid())
                        {
                            continue; // No LCD for this camera, skip
                        }
                    }

                    if (!lcdInfo.IsValid())
                        continue;

                    // Write directly to LCDs based on frame type
                    if (processedFrame.IsGridFrame && lcdInfo.HasGridMode)
                    {
                        // 2×2 grid frame - split and write to 4 panels
                        int gridW = processedFrame.Width;
                        int gridH = processedFrame.Height;
                        bool isColorFrame = processedFrame.Mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0;
                        var (tl, tr, bl, br) = SplitFrameIntoQuadrants(processedFrame.DecodedAscii, gridW, gridH, isColorFrame);

                        int quadrantWidth  = gridW / 2;
                        // Grayscale uses half the rows (aspect-ratio corrected), colour uses half the full height
                        int quadrantHeight = isColorFrame ? gridH / 2 : gridH / 4;

                        WriteToPanelGrid(lcdInfo.TopLeft,     tl, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "TL");
                        WriteToPanelGrid(lcdInfo.TopRight,    tr, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "TR");
                        WriteToPanelGrid(lcdInfo.BottomLeft,  bl, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "BL");
                        WriteToPanelGrid(lcdInfo.BottomRight, br, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "BR");
                    }
                    else if (processedFrame.IsSingleLcdFrame && lcdInfo.HasSingleLcd)
                    {
                        // Single LCD frame - write directly
                        WriteToPanelGrid(lcdInfo.SingleLcd, processedFrame.DecodedAscii, processedFrame.Mode,
                                               processedFrame.Width, processedFrame.Height, processedFrame.CameraEntityId, "SINGLE");
                    }
                    else
                    {
                        // Generic frame - try single LCD first, then grid
                        if (lcdInfo.HasSingleLcd)
                        {
                            WriteToPanelGrid(lcdInfo.SingleLcd, processedFrame.DecodedAscii, processedFrame.Mode,
                                                   processedFrame.Width, processedFrame.Height, processedFrame.CameraEntityId, "SINGLE");
                        }
                        else if (lcdInfo.HasGridMode)
                        {
                            // Legacy: stretch to grid
                            const int GRID_SIZE = 362;
                            bool isColorFrame2 = processedFrame.Mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0;
                            var (tl, tr, bl, br) = SplitFrameIntoQuadrants(processedFrame.DecodedAscii, processedFrame.Width, processedFrame.Height, isColorFrame2);

                            int quadrantWidth  = processedFrame.Width / 2;
                            int quadrantHeight = isColorFrame2 ? processedFrame.Height / 2 : processedFrame.Height / 4;

                            WriteToPanelGrid(lcdInfo.TopLeft,     tl, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "TL");
                            WriteToPanelGrid(lcdInfo.TopRight,    tr, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "TR");
                            WriteToPanelGrid(lcdInfo.BottomLeft,  bl, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "BL");
                            WriteToPanelGrid(lcdInfo.BottomRight, br, processedFrame.Mode, quadrantWidth, quadrantHeight, processedFrame.CameraEntityId, "BR");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error writing frame for camera {processedFrame.CameraEntityId}");
                }
            }

            // Log if we're falling behind (queue building up)
            if (_processedFrameQueue.Count > 50)
            {
                Log.Warn($"⚠️ Frame processing queue backlog: {_processedFrameQueue.Count} frames pending (consider increasing MAX_PER_TICK)");
            }

            // Actively drain stale frames so LCDs never show content that is many seconds old.
            // This can happen after GC pauses or a rescan burst that temporarily blocks the game thread.
            // Keep only the 5 most-recently-decoded frames (matching the per-connection limit in
            // CCTVClientConnection._frameQueue).
            const int STALE_FRAME_LIMIT = 5;
            if (_processedFrameQueue.Count > STALE_FRAME_LIMIT)
            {
                int dropped = 0;
                while (_processedFrameQueue.Count > STALE_FRAME_LIMIT)
                {
                    _processedFrameQueue.TryDequeue(out _);
                    dropped++;
                }
                Log.Warn($"Dropped {dropped} stale frames from queue to prevent showing old content");
            }
        }

        /// <summary>
        /// Displays queued frames at the configured display FPS.
        /// SIMPLIFIED APPROACH: Only the active camera uses queued display.
        /// All other cameras show their last buffered frame (static).
        /// This prevents LCD conflicts and flickering between cameras with same DisplayName.
        /// </summary>
        private void DisplayQueuedFrames()
        {
            // Only display the active camera from its queue
            // All other cameras remain static (showing last buffered frame)
            if (_currentCameraEntityId == 0)
                return;

            if (!_frameQueues.TryGetValue(_currentCameraEntityId, out var activeQueue))
                return;

            if (activeQueue.Count == 0)
                return;

            // Dequeue and display ONE frame for the active camera only
            var frame = activeQueue.Dequeue();

            // Update the frame buffer
            _frameBuffer[_currentCameraEntityId] = frame;

            // Write to LCDs
            WriteFrameToLcds(_currentCameraEntityId);

            if (_enableVerboseFrameLogging)
            {
                Log.Debug($"[DISPLAY] Active camera {_currentCameraEntityId}: Displayed queued frame (queue: {activeQueue.Count} remaining)");
            }
        }

        /// <summary>
        /// Writes all buffered frames to their respective LCDs.
        /// Called after camera cycling so LCDs that aren't currently being captured
        /// still show their last known frame.
        /// </summary>
        private void RefreshAllLcdsFromBuffer()
        {
            // Get the current camera's display name to avoid conflicts
            string currentCameraDisplayName = null;
            if (_currentCameraEntityId != 0)
            {
                var currentCamera = _indexedCameras.FirstOrDefault(c => c.EntityId == _currentCameraEntityId);
                if (currentCamera != null)
                {
                    currentCameraDisplayName = currentCamera.DisplayName;
                }
            }

            foreach (var kvp in _frameBuffer)
            {
                long cameraEntityId = kvp.Key;
                var frame = kvp.Value;

                // Skip the current camera — it gets live updates via HandleFrameCommand
                if (cameraEntityId == _currentCameraEntityId)
                    continue;

                // Skip cameras that share the same display name (and thus share LCDs)
                // This prevents multiple cameras from fighting over the same LCD panels
                if (currentCameraDisplayName != null)
                {
                    var otherCamera = _indexedCameras.FirstOrDefault(c => c.EntityId == cameraEntityId);
                    if (otherCamera != null && otherCamera.DisplayName == currentCameraDisplayName)
                    {
                        // This camera shares LCDs with the current camera - skip it
                        continue;
                    }
                }

                WriteFrameToLcds(cameraEntityId);
            }
        }

        private void WriteFrameToLcds(long cameraEntityId)
        {
            // Find or scan for LCD panels matching this camera
            if (!_lcdsByEntityId.TryGetValue(cameraEntityId, out var lcdInfo))
            {
                lcdInfo = FindLcdsForCamera(cameraEntityId);
                _lcdsByEntityId[cameraEntityId] = lcdInfo;

                if (!lcdInfo.IsValid())
                {
                    Log.Debug($"No valid LCD setup found for camera entity {cameraEntityId}");
                    return;
                }
            }

            if (!lcdInfo.IsValid())
                return;

            // Get the buffered frame for this camera
            if (!_frameBuffer.TryGetValue(cameraEntityId, out var frame))
            {
                Log.Debug($"No buffered frame for camera entity {cameraEntityId}");
                return;
            }

            try
            {
                // Write to 2×2 grid if it exists
                if (lcdInfo.HasGridMode && !string.IsNullOrEmpty(frame.GridFrame))
                {
                    int gridSize = _config.LcdGridResolution;
                    Log.Debug($"[GRID RENDER] EntityId={cameraEntityId} Using {gridSize}×{gridSize} grid frame");

                    bool isColor = frame.Mode != null && !frame.Mode.Contains("GRAY");
                    var (tl, tr, bl, br) = SplitFrameIntoQuadrants(frame.GridFrame, gridSize, gridSize, isColor);

                    int quadrantWidth = gridSize / 2;
                    int quadrantHeight = gridSize / 2;

                    WriteToPanelGrid(lcdInfo.TopLeft, tl, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "TL");
                    WriteToPanelGrid(lcdInfo.TopRight, tr, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "TR");
                    WriteToPanelGrid(lcdInfo.BottomLeft, bl, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "BL");
                    WriteToPanelGrid(lcdInfo.BottomRight, br, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "BR");
                }
                else if (lcdInfo.HasGridMode && !string.IsNullOrEmpty(frame.AsciiData))
                {
                    // Fallback to legacy single-frame mode
                    Log.Debug($"[GRID RENDER] EntityId={cameraEntityId} Using legacy frame {frame.Width}×{frame.Height}");

                    bool isColorLegacy = frame.Mode != null && !frame.Mode.Contains("GRAY");
                    var (tl, tr, bl, br) = SplitFrameIntoQuadrants(frame.AsciiData, frame.Width, frame.Height, isColorLegacy);

                    int quadrantWidth = frame.Width / 2;
                    int quadrantHeight = frame.Height / 2;

                    WriteToPanelGrid(lcdInfo.TopLeft, tl, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "TL");
                    WriteToPanelGrid(lcdInfo.TopRight, tr, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "TR");
                    WriteToPanelGrid(lcdInfo.BottomLeft, bl, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "BL");
                    WriteToPanelGrid(lcdInfo.BottomRight, br, frame.Mode, quadrantWidth, quadrantHeight, cameraEntityId, "BR");
                }

                // Write to single LCD if it exists
                if (lcdInfo.HasSingleLcd)
                {
                    int singleSize = _config.LcdSingleResolution;

                    // Use dedicated single LCD frame if available
                    if (!string.IsNullOrEmpty(frame.SingleLcdFrame))
                    {
                        Log.Debug($"[SINGLE LCD] EntityId={cameraEntityId} Using dedicated {singleSize}×{singleSize} frame");
                        WriteToPanelGrid(lcdInfo.SingleLcd, frame.SingleLcdFrame, frame.Mode, singleSize, singleSize, cameraEntityId, "SINGLE");
                    }
                    // Fallback to legacy downsampling/cropping if only one frame is available
                    else if (!string.IsNullOrEmpty(frame.AsciiData))
                    {
                        // In color mode, ANSI escape codes can't be safely downsampled
                        if (frame.Mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0 && (frame.Width > singleSize || frame.Height > singleSize))
                        {
                            Log.Debug($"[SINGLE LCD] Skipping color frame {frame.Width}×{frame.Height} - no dedicated single LCD frame available");
                        }
                        else
                        {
                            string displayFrame = frame.AsciiData;
                            int displayWidth = frame.Width;
                            int displayHeight = frame.Height;

                            if (frame.Width > singleSize || frame.Height > singleSize)
                            {
                                // Grayscale can downsample safely
                                displayFrame = DownsampleFrame(frame.AsciiData, frame.Width, frame.Height, singleSize, singleSize);
                                displayWidth = singleSize;
                                displayHeight = singleSize;
                                Log.Debug($"[SINGLE LCD] Down-sampled frame from {frame.Width}×{frame.Height} to {displayWidth}×{displayHeight}");
                            }
                            else
                            {
                                Log.Debug($"[SINGLE LCD] Using original frame size {displayWidth}×{displayHeight}");
                            }

                            WriteToPanelGrid(lcdInfo.SingleLcd, displayFrame, frame.Mode, displayWidth, displayHeight, cameraEntityId, "SINGLE");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error writing frame to LCDs for camera entity {cameraEntityId}");
            }
        }

        /// <summary>
        /// Writes ASCII content to a single LCD panel
        /// </summary>
        private void WriteToPanelGrid(MyCubeBlock block, string content, string mode, int width, int height, long cameraEntityId, string position = null)
        {
            if (block == null)
                return;

            try
            {
                var textPanel = block as Sandbox.Game.Entities.Blocks.MyTextPanel;
                if (textPanel == null)
                    return;

                if (textPanel.Closed)
                {
                    Log.Info($"Detected closed LCD, clearing cache for camera entity {cameraEntityId}");
                    _lcdsByEntityId.Remove(cameraEntityId);
                    return;
                }

                var modApiPanel = textPanel as IMyTextPanel;
                if (modApiPanel == null)
                    return;

                // Write content
                modApiPanel.WriteText(content);
                modApiPanel.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                modApiPanel.Font = "Monospace";

                float fontSize;

                // Determine which display type this block is
                bool isThisPanelSingleLcd = false;
                bool isThisPanelGridPanel = false;

                // Lists to hold slave LCDs that need to copy this content
                List<MyCubeBlock> slavesToCopy = new List<MyCubeBlock>();

                if (_lcdsByEntityId.TryGetValue(cameraEntityId, out var lcdInfo))
                {
                    // Check if this specific block is the single LCD (master)
                    if (lcdInfo.HasSingleLcd && block == lcdInfo.SingleLcd)
                    {
                        isThisPanelSingleLcd = true;
                        // Add all single LCD slaves to copy list
                        slavesToCopy.AddRange(lcdInfo.SlaveSingle);
                    }
                    // Check if this block is one of the grid panels (master)
                    else if (lcdInfo.HasGridMode)
                    {
                        if (block == lcdInfo.TopLeft)
                        {
                            isThisPanelGridPanel = true;
                            slavesToCopy.AddRange(lcdInfo.SlaveTopLeft);
                        }
                        else if (block == lcdInfo.TopRight)
                        {
                            isThisPanelGridPanel = true;
                            slavesToCopy.AddRange(lcdInfo.SlaveTopRight);
                        }
                        else if (block == lcdInfo.BottomLeft)
                        {
                            isThisPanelGridPanel = true;
                            slavesToCopy.AddRange(lcdInfo.SlaveBottomLeft);
                        }
                        else if (block == lcdInfo.BottomRight)
                        {
                            isThisPanelGridPanel = true;
                            slavesToCopy.AddRange(lcdInfo.SlaveBottomRight);
                        }
                    }
                }

                // Apply appropriate settings for THIS panel's type
                if (isThisPanelSingleLcd)
                {
                    // SINGLE LCD: uses its own font base (SingleLcdFontSize) independent of
                    // the grid so both can be tuned separately.
                    // Grayscale: SingleLcdFontSize×2; colour: SingleLcdFontSize×1.
                    bool isColorMode = mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (_autoAdjustFontSize)
                    {
                        float baseFont = (_config?.SingleLcdFontSize ?? 0.080f) * (isColorMode ? 1f : 2f);
                        fontSize = baseFont * _fontScale;
                        fontSize = Math.Max(0.03f, Math.Min(0.35f, fontSize));
                    }
                    else
                    {
                        fontSize = _fontScale;
                    }
                }
                else if (isThisPanelGridPanel)
                {
                    // GRID PANEL: Auto-detect transparent vs regular LCD
                    // Transparent LCDs need slightly LARGER font to overlap and hide seams.
                    // Grayscale chars are ~half the width of colour chars at the same font size
                    // (SE Monospace 2:1 aspect ratio), so grayscale uses 2× the base grid font.
                    bool isTransparent = IsTransparentLcd(textPanel);
                    bool isColorMode   = mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0;
                    float baseGridFont = (_config?.GridFontSize ?? 0.1f) * (isColorMode ? 1f : 2f);

                    if (isTransparent)
                    {
                        fontSize = baseGridFont * 1.12f;
                        Log.Debug($"Grid panel ({position ?? "?"}): Transparent LCD, font {fontSize:F3}");
                    }
                    else
                    {
                        fontSize = baseGridFont;
                        Log.Debug($"Grid panel ({position ?? "?"}): Regular LCD, font {fontSize:F3}");
                    }
                }
                else
                {
                    // Fallback (shouldn't happen)
                    fontSize = _fontScale;
                }

                modApiPanel.FontSize = fontSize;
                modApiPanel.FontColor = mode.IndexOf("COLOR", StringComparison.OrdinalIgnoreCase) >= 0
                    ? new VRageMath.Color(255, 255, 255)
                    : ParseColor(_lcdFontTint);
                modApiPanel.BackgroundColor = new VRageMath.Color(0, 0, 0);
                modApiPanel.TextPadding = 0f;
                modApiPanel.Alignment = VRage.Game.GUI.TextPanel.TextAlignment.LEFT;

                // COPY TO SLAVE LCDs (fast text copy, no re-rendering!)
                foreach (var slaveLcd in slavesToCopy)
                {
                    try
                    {
                        var slaveTextPanel = slaveLcd as Sandbox.Game.Entities.Blocks.MyTextPanel;
                        if (slaveTextPanel == null) continue;

                        var slaveModApiPanel = slaveTextPanel as IMyTextPanel;
                        if (slaveModApiPanel == null) continue;

                        // Fast copy: Just copy text and settings from master
                        slaveModApiPanel.WriteText(content);
                        slaveModApiPanel.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                        slaveModApiPanel.Font = "Monospace";
                        slaveModApiPanel.FontSize = fontSize; // Same size as master
                        slaveModApiPanel.FontColor = modApiPanel.FontColor; // Same color as master
                        slaveModApiPanel.BackgroundColor = modApiPanel.BackgroundColor; // Same background as master
                        slaveModApiPanel.TextPadding = 0f;
                        slaveModApiPanel.Alignment = VRage.Game.GUI.TextPanel.TextAlignment.LEFT;
                    }
                    catch (Exception slaveEx)
                    {
                        Log.Warn($"Failed to copy to slave LCD: {slaveEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error writing to individual LCD panel for camera entity {cameraEntityId}");
            }
        }

        /// <summary>
        /// Returns true if the grid has at least one active, broadcasting radio antenna.
        /// Used to gate slave LCD updates — a grid must have antenna infrastructure to receive a feed.
        /// </summary>
        private bool GridHasActiveAntenna(MyCubeGrid grid)
        {
            try
            {
                foreach (var block in grid.GetFatBlocks())
                {
                    var antenna = block as IMyRadioAntenna;
                    if (antenna != null && antenna.IsWorking && antenna.EnableBroadcasting)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Error checking antenna on grid '{grid?.DisplayName}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Detects if an LCD panel is a transparent type.
        /// Transparent LCDs have different character spacing and need adjusted font sizes.
        /// </summary>
        private bool IsTransparentLcd(Sandbox.Game.Entities.Blocks.MyTextPanel textPanel)
        {
            try
            {
                // Check the block's definition for transparent LCD subtypes
                var blockDef = textPanel.BlockDefinition;
                if (blockDef == null)
                    return false;

                string subtype = blockDef.Id.SubtypeName ?? "";

                // Space Engineers transparent LCD subtypes contain "Transparent" in the name
                // Examples: "TransparentLCDSmall", "LargeLCDPanelTransparent", etc.
                bool isTransparent = subtype.Contains("Transparent", StringComparison.OrdinalIgnoreCase);

                return isTransparent;
            }
            catch (Exception ex)
            {
                Log.Debug($"Error detecting transparent LCD: {ex.Message}");
                return false; // Assume regular LCD on error
            }
        }

        /// <summary>
        /// Find ALL LCDs matching the camera's display name across ALL grids.
        /// This allows multiple grids to show the same camera feed.
        /// </summary>
        private LcdDisplayInfo FindLcdsForCamera(long cameraEntityId)
        {
            var result = new LcdDisplayInfo();

            try
            {
                // Find the camera info to get its grid
                var cameraInfo = _indexedCameras.FirstOrDefault(c => c.EntityId == cameraEntityId);
                if (cameraInfo == null)
                {
                    Log.Warn($"Camera with entity ID {cameraEntityId} not found in indexed cameras");
                    return result;
                }

                string cameraName = cameraInfo.DisplayName;
                long cameraGridId = cameraInfo.GridEntityId;

                // Strip _L{n} loop suffix so "Test02_L1" matches LCD "LCD_TV Test02".
                string lcdBaseName = StripLoopSuffix(cameraName);

                var grids = MyEntities.GetEntities().OfType<MyCubeGrid>();

                // Track MASTER panels (first found) and SLAVE panels (all others with _SLAVE suffix)
                MyCubeBlock singleLcd = null;
                MyCubeBlock topLeft = null, topRight = null, bottomLeft = null, bottomRight = null;

                // Search ALL grids for matching LCDs (faction-wide broadcast, no grid filtering)
                foreach (var grid in grids)
                {
                    var fatBlocks = grid.GetFatBlocks();
                    foreach (var block in fatBlocks)
                    {
                        var textPanel = block as Sandbox.Game.Entities.Blocks.MyTextPanel;
                        if (textPanel == null)
                            continue;

                        string lcdName = textPanel.CustomName?.ToString() ?? "";
                        if (!lcdName.StartsWith(_lcdPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string lcdSuffix = lcdName.Substring(_lcdPrefix.Length).Trim();

                        // Detect SLAVE suffix (e.g., "Test01_TL_SLAVE" or "Test01_SLAVE")
                        bool isSlave = lcdSuffix.EndsWith("_SLAVE", StringComparison.OrdinalIgnoreCase) ||
                                      lcdSuffix.Contains("_SLAVE", StringComparison.OrdinalIgnoreCase);

                        // Remove _SLAVE suffix for matching
                        string matchName = lcdSuffix;
                        if (isSlave)
                        {
                            // Remove everything from _SLAVE onward (handles _SLAVE, _SLAVE2, _SLAVE_GridB, etc.)
                            int slaveIndex = matchName.IndexOf("_SLAVE", StringComparison.OrdinalIgnoreCase);
                            if (slaveIndex >= 0)
                                matchName = matchName.Substring(0, slaveIndex);
                        }

                        // Check for single LCD match
                        if (string.Equals(matchName, lcdBaseName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSlave)
                            {
                                if (GridHasActiveAntenna(grid))
                                {
                                    result.SlaveSingle.Add(block);
                                    Log.Debug($"[LCD Scan] Found SLAVE single LCD: '{lcdName}' on grid '{grid.DisplayName}'");
                                }
                                else
                                {
                                    Log.Debug($"[LCD Scan] Skipping SLAVE single LCD '{lcdName}' on grid '{grid.DisplayName}' - no active antenna");
                                }
                            }
                            else if (singleLcd == null)
                            {
                                singleLcd = block;
                                Log.Debug($"[LCD Scan] Found MASTER single LCD: '{lcdName}' on grid '{grid.DisplayName}'");
                            }
                        }

                        // Check for grid mode suffixes
                        if (matchName.Equals($"{lcdBaseName}_TL", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSlave)
                            {
                                if (GridHasActiveAntenna(grid))
                                {
                                    result.SlaveTopLeft.Add(block);
                                    Log.Debug($"[LCD Scan] Found SLAVE grid panel (TL): '{lcdName}' on grid '{grid.DisplayName}'");
                                }
                                else
                                {
                                    Log.Debug($"[LCD Scan] Skipping SLAVE TL '{lcdName}' on grid '{grid.DisplayName}' - no active antenna");
                                }
                            }
                            else if (topLeft == null)
                            {
                                topLeft = block;
                                Log.Debug($"[LCD Scan] Found MASTER grid panel (TL): '{lcdName}' on grid '{grid.DisplayName}'");
                            }
                        }
                        else if (matchName.Equals($"{lcdBaseName}_TR", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSlave)
                            {
                                if (GridHasActiveAntenna(grid))
                                {
                                    result.SlaveTopRight.Add(block);
                                    Log.Debug($"[LCD Scan] Found SLAVE grid panel (TR): '{lcdName}' on grid '{grid.DisplayName}'");
                                }
                                else
                                {
                                    Log.Debug($"[LCD Scan] Skipping SLAVE TR '{lcdName}' on grid '{grid.DisplayName}' - no active antenna");
                                }
                            }
                            else if (topRight == null)
                            {
                                topRight = block;
                                Log.Debug($"[LCD Scan] Found MASTER grid panel (TR): '{lcdName}' on grid '{grid.DisplayName}'");
                            }
                        }
                        else if (matchName.Equals($"{lcdBaseName}_BL", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSlave)
                            {
                                if (GridHasActiveAntenna(grid))
                                {
                                    result.SlaveBottomLeft.Add(block);
                                    Log.Debug($"[LCD Scan] Found SLAVE grid panel (BL): '{lcdName}' on grid '{grid.DisplayName}'");
                                }
                                else
                                {
                                    Log.Debug($"[LCD Scan] Skipping SLAVE BL '{lcdName}' on grid '{grid.DisplayName}' - no active antenna");
                                }
                            }
                            else if (bottomLeft == null)
                            {
                                bottomLeft = block;
                                Log.Debug($"[LCD Scan] Found MASTER grid panel (BL): '{lcdName}' on grid '{grid.DisplayName}'");
                            }
                        }
                        else if (matchName.Equals($"{lcdBaseName}_BR", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isSlave)
                            {
                                if (GridHasActiveAntenna(grid))
                                {
                                    result.SlaveBottomRight.Add(block);
                                    Log.Debug($"[LCD Scan] Found SLAVE grid panel (BR): '{lcdName}' on grid '{grid.DisplayName}'");
                                }
                                else
                                {
                                    Log.Debug($"[LCD Scan] Skipping SLAVE BR '{lcdName}' on grid '{grid.DisplayName}' - no active antenna");
                                }
                            }
                            else if (bottomRight == null)
                            {
                                bottomRight = block;
                                Log.Debug($"[LCD Scan] Found MASTER grid panel (BR): '{lcdName}' on grid '{grid.DisplayName}'");
                            }
                        }
                    }
                }

                // Assign found MASTER LCDs (can have BOTH single and grid)
                if (singleLcd != null)
                {
                    result.SingleLcd = singleLcd;
                    Log.Debug($"✅ Matched camera '{cameraName}' to MASTER single LCD (181×181 resolution)");
                }

                if (topLeft != null && topRight != null && bottomLeft != null && bottomRight != null)
                {
                    result.TopLeft = topLeft;
                    result.TopRight = topRight;
                    result.BottomLeft = bottomLeft;
                    result.BottomRight = bottomRight;
                    Log.Debug($"✅ Matched camera '{cameraName}' to MASTER 2×2 LCD grid (362×362 effective resolution)");
                    Log.Debug($"   📺 Grid-optimized settings applied: Font size 0.22, edge-to-edge display");
                }

                // Report what was found
                if (result.IsValid())
                {
                    if (result.HasSingleLcd && result.HasGridMode)
                    {
                        Log.Debug($"🎉 Camera '{cameraName}' has BOTH MASTER single LCD and 2×2 grid! Both will display the same content.");
                    }

                    // Report slave count
                    if (result.HasSlaves)
                    {
                        int totalSlaves = result.SlaveSingle.Count + result.SlaveTopLeft.Count +
                                         result.SlaveTopRight.Count + result.SlaveBottomLeft.Count +
                                         result.SlaveBottomRight.Count;
                        Log.Info($"📡 Found {totalSlaves} SLAVE LCDs - will copy from master (efficient!)");
                    }
                }
                else
                {
                    Log.Warn($"❌ No LCD setup found for camera '{cameraName}'");
                    if (topLeft != null || topRight != null || bottomLeft != null || bottomRight != null)
                    {
                        Log.Warn($"⚠️ Partial grid detected - missing panels:");
                        Log.Warn($"   TL: {(topLeft != null ? "✅" : "❌")} TR: {(topRight != null ? "✅" : "❌")} BL: {(bottomLeft != null ? "✅" : "❌")} BR: {(bottomRight != null ? "✅" : "❌")}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error scanning for LCD panels");
            }

            return result;
        }

        /// <summary>
        /// Splits an ASCII frame into 4 quadrants for 2×2 LCD grid display.
        /// Uses index-based line scanning with reusable buffers to avoid
        /// asciiFrame.Split('\n') + Substring allocations (~512 KB per call).
        /// </summary>
        private (string topLeft, string topRight, string bottomLeft, string bottomRight) SplitFrameIntoQuadrants(string asciiFrame, int width, int height, bool isColor = true)
        {
            // Count lines without allocating a string[]
            int lineCount = 1;
            for (int i = 0; i < asciiFrame.Length; i++)
                if (asciiFrame[i] == '\n') lineCount++;

            int midWidth = width / 2;
            int midHeight = lineCount / 2;

            // Grow reusable buffers only when needed — no per-frame allocation.
            if (_lineStarts == null || _lineStarts.Length < lineCount)
            {
                _lineStarts  = new int[lineCount];
                _lineLengths = new int[lineCount];
            }

            int li = 0;
            int ls = 0;
            for (int i = 0; i <= asciiFrame.Length; i++)
            {
                if (i == asciiFrame.Length || asciiFrame[i] == '\n')
                {
                    _lineStarts[li]  = ls;
                    _lineLengths[li] = i - ls;
                    li++;
                    ls = i + 1;
                }
            }

            // GridVerticalOffset: positive creates overlap at the seam to close the
            // physical gap between LCD blocks.  Top panels skip vOffset rows at the
            // top, bottom panels start vOffset rows earlier.
            int vOffset = _config.GridVerticalOffset;
            int tlStartY = Math.Max(0, Math.Min(vOffset, midHeight - 1));
            int blStartY = Math.Max(0, midHeight - vOffset);
            int blEndY = blStartY + midHeight;

            // GridHorizontalOffset: same principle for the vertical seam.
            int hOffset = _config.GridHorizontalOffset;
            int tlStartX = Math.Max(0, Math.Min(hOffset, midWidth - 1));
            int trStartX = Math.Max(0, midWidth - hOffset);

            var tlBuilder = new StringBuilder(midWidth * midHeight + midHeight);
            var trBuilder = new StringBuilder(midWidth * midHeight + midHeight);
            var blBuilder = new StringBuilder(midWidth * midHeight + midHeight);
            var brBuilder = new StringBuilder(midWidth * midHeight + midHeight);

            for (int y = 0; y < lineCount; y++)
            {
                int lineStart = _lineStarts[y];
                int lineLen   = _lineLengths[y];

                // Top half
                if (y >= tlStartY && y < tlStartY + midHeight)
                {
                    // Top-left — slice directly from asciiFrame, no Substring
                    {
                        int endX = Math.Min(tlStartX + midWidth, lineLen);
                        if (endX > tlStartX)
                            tlBuilder.Append(asciiFrame, lineStart + tlStartX, endX - tlStartX);
                        else if (lineLen < width)
                            tlBuilder.Append(' ', midWidth); // line too short, pad
                        if (y < tlStartY + midHeight - 1) tlBuilder.Append('\n');
                    }
                    // Top-right
                    {
                        int endX = Math.Min(trStartX + midWidth, lineLen);
                        if (endX > trStartX)
                            trBuilder.Append(asciiFrame, lineStart + trStartX, endX - trStartX);
                        else if (lineLen < width)
                            trBuilder.Append(' ', midWidth);
                        if (y < tlStartY + midHeight - 1) trBuilder.Append('\n');
                    }
                }
                // Bottom half
                if (y >= blStartY && y < blEndY)
                {
                    // Bottom-left
                    {
                        int endX = Math.Min(tlStartX + midWidth, lineLen);
                        if (endX > tlStartX)
                            blBuilder.Append(asciiFrame, lineStart + tlStartX, endX - tlStartX);
                        else if (lineLen < width)
                            blBuilder.Append(' ', midWidth);
                        if (y < blEndY - 1) blBuilder.Append('\n');
                    }
                    // Bottom-right
                    {
                        int endX = Math.Min(trStartX + midWidth, lineLen);
                        if (endX > trStartX)
                            brBuilder.Append(asciiFrame, lineStart + trStartX, endX - trStartX);
                        else if (lineLen < width)
                            brBuilder.Append(' ', midWidth);
                        if (y < blEndY - 1) brBuilder.Append('\n');
                    }
                }
            }

            return (tlBuilder.ToString(), trBuilder.ToString(), blBuilder.ToString(), brBuilder.ToString());
        }

        /// <summary>
        /// Down-samples a frame to a smaller size using nearest-neighbor sampling
        /// Used when single LCD receives over-sized frame (e.g., 362×362 → 181×181)
        /// Uses line-offset buffers to avoid Split/Substring allocations.
        /// </summary>
        private string DownsampleFrame(string asciiFrame, int srcWidth, int srcHeight, int destWidth, int destHeight)
        {
            // Build line offsets using the reusable buffers
            int lineCount = 1;
            for (int i = 0; i < asciiFrame.Length; i++)
                if (asciiFrame[i] == '\n') lineCount++;

            if (_lineStarts == null || _lineStarts.Length < lineCount)
            {
                _lineStarts  = new int[lineCount];
                _lineLengths = new int[lineCount];
            }

            int li = 0;
            int ls = 0;
            for (int i = 0; i <= asciiFrame.Length; i++)
            {
                if (i == asciiFrame.Length || asciiFrame[i] == '\n')
                {
                    _lineStarts[li]  = ls;
                    _lineLengths[li] = i - ls;
                    li++;
                    ls = i + 1;
                }
            }

            var resultBuilder = new StringBuilder(destWidth * destHeight + destHeight);

            // Calculate sampling ratios
            float xRatio = (float)srcWidth / destWidth;
            float yRatio = (float)srcHeight / destHeight;

            // Sample from center of each destination pixel for better quality
            for (int destY = 0; destY < destHeight; destY++)
            {
                float srcYf = (destY + 0.5f) * yRatio;
                int srcY = (int)srcYf;
                if (srcY >= lineCount) srcY = lineCount - 1;
                if (srcY < 0) srcY = 0;

                int srcLineStart = _lineStarts[srcY];
                int srcLineLen   = _lineLengths[srcY];

                for (int destX = 0; destX < destWidth; destX++)
                {
                    float srcXf = (destX + 0.5f) * xRatio;
                    int srcX = (int)srcXf;

                    if (srcX >= srcLineLen)
                        resultBuilder.Append(' '); // line too short, pad
                    else
                        resultBuilder.Append(asciiFrame[srcLineStart + srcX]);
                }

                resultBuilder.Append('\n');
            }

            return resultBuilder.ToString();
        }

        private void RescanCameras()
        {
            _indexedCameras.Clear();
            _cameraByEntity.Clear();

            // IMPORTANT: Clear LCD cache so deleted LCDs are removed!
            _lcdsByEntityId.Clear();
            Log.Debug("Cleared LCD cache - will rescan on next frame");

            if (MyAPIGateway.Entities == null)
            {
                Log.Warn("MyAPIGateway.Entities is null, cannot scan cameras");
                return;
            }

            int cameraBlockCount = 0;
            int prefixMatchCount = 0;

            // Use Torch's full game API to access grids and blocks
            int gridsChecked = 0;
            int totalBlocks = 0;

            // Get all cube grids using Torch's internal API
            // Snapshot to a list first: merge blocks / PB-driven grid splits can modify
            // the live entity collection mid-iteration → InvalidOperationException.
            List<MyCubeGrid> grids;
            try
            {
                var entities = MyEntities.GetEntities();
                grids = new List<MyCubeGrid>();
                foreach (var entity in entities)
                {
                    var grid = entity as MyCubeGrid;
                    if (grid != null)
                        grids.Add(grid);
                }
            }
            catch (InvalidOperationException)
            {
                Log.Info("RescanCameras: entity list changed mid-snapshot (merge block?) — will retry next interval");
                return;
            }

            foreach (var grid in grids)
            {
                if (grid.MarkedForClose) continue;

                gridsChecked++;
                long gridEntityId = grid.EntityId;
                string gridName = grid.DisplayName ?? "UnknownGrid";

                // Iterate through fat blocks (functional blocks)
                // Wrapped in try/catch: merge blocks can modify the block list mid-iteration.
                try
                {
                var fatBlocks = grid.GetFatBlocks();
                if (fatBlocks.Count == 0)
                {
                    Log.Debug($"Grid '{gridName}' has no fat blocks");
                    continue;
                }

                int blockCount = fatBlocks.Count;
                totalBlocks += blockCount;

                foreach (var block in fatBlocks)
                {
                    if (block == null)
                        continue;

                    // Check if this is a camera block
                    var camera = block as Sandbox.Game.Entities.MyCameraBlock;
                    if (camera == null)
                        continue;

                    string customName = camera.CustomName?.ToString() ?? "";
                    if (string.IsNullOrEmpty(customName))
                        continue;

                    // DEBUG: Log ALL cameras found (before prefix filter)
                    Log.Debug($"[SCAN] Found camera: '{customName}' on grid '{gridName}'");

                    // Check if it starts with our camera prefix
                    if (!customName.StartsWith(_cameraPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Debug($"[SCAN] REJECTED: '{customName}' (doesn't match prefix '{_cameraPrefix}')");
                        continue;
                    }

                    cameraBlockCount++;
                    long blockEntityId = camera.EntityId;

                    Log.Debug($"[SCAN] ACCEPTED: '{customName}' EntityId={blockEntityId}");

                    prefixMatchCount++;

                    // Extract the suffix after the prefix (e.g., "LCD_TVCamera Test01" → "Test01")
                    string suffix = customName.Substring(_cameraPrefix.Length).Trim();
                    if (string.IsNullOrEmpty(suffix))
                        suffix = "Default";

                    // Get faction tag of grid owner for faction-based routing
                    string factionTag = null;
                    try
                    {
                        if (camera.OwnerId != 0)
                        {
                            var faction = MyAPIGateway.Session?.Factions?.TryGetPlayerFaction(camera.OwnerId);
                            if (faction != null)
                            {
                                factionTag = faction.Tag;
                                Log.Debug($"Camera '{suffix}' owner faction: {faction.Name} [{factionTag}]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Failed to get faction for camera '{suffix}': {ex.Message}");
                    }

                    var info = new CameraInfo
                    {
                        EntityId = blockEntityId,
                        DisplayName = suffix,
                        GridIndex = 0, // Not used in new simple matching
                        OwnerIndex = 0, // Not used in new simple matching
                        GridName = gridName,
                        OwnerId = camera.OwnerId,
                        GridEntityId = gridEntityId, // Store grid ID for same-grid matching
                        FactionTag = factionTag // Store faction tag for faction-based routing
                    };

                    _indexedCameras.Add(info);
                    _cameraByEntity[blockEntityId] = info;
                }

                if (blockCount > 0)
                {
                    Log.Debug($"Grid '{gridName}' ({gridEntityId}): {blockCount} blocks");
                }
                }
                catch (InvalidOperationException)
                {
                    Log.Debug($"RescanCameras: grid '{gridName}' blocks changed mid-iteration (merge block?) — skipping grid");
                    continue;
                }
            }

            // Sort cameras using round-robin grid interleaving for balanced cycling
            // This ensures all grids get regular updates instead of waiting for one grid to finish
            // Example: Grid A Cam1 → Grid B Cam1 → Grid A Cam2 → Grid B Cam2 (instead of all Grid A then all Grid B)
            if (_indexedCameras.Count > 0)
            {
                // Group cameras by grid
                var camerasByGrid = _indexedCameras
                    .GroupBy(c => c.GridEntityId)
                    .OrderBy(g => g.Key) // Stable grid ordering
                    .Select(g => g.OrderBy(c => c.EntityId).ToList()) // Stable camera ordering within each grid
                    .ToList();

                // Interleave cameras round-robin across grids
                var interleavedCameras = new List<CameraInfo>();
                int maxCamerasPerGrid = camerasByGrid.Max(g => g.Count);

                for (int i = 0; i < maxCamerasPerGrid; i++)
                {
                    foreach (var gridCameras in camerasByGrid)
                    {
                        if (i < gridCameras.Count)
                        {
                            interleavedCameras.Add(gridCameras[i]);
                        }
                    }
                }

                _indexedCameras.Clear();
                _indexedCameras.AddRange(interleavedCameras);

                Log.Info($"Cameras sorted with round-robin grid interleaving: {camerasByGrid.Count} grids, max {maxCamerasPerGrid} cameras per grid");
            }

            // Update _currentCameraIndex to track the same physical camera after rescan
            if (_currentCameraEntityId != 0 && _indexedCameras.Count > 0)
            {
                int newIndex = _indexedCameras.FindIndex(c => c.EntityId == _currentCameraEntityId);
                if (newIndex >= 0)
                {
                    _currentCameraIndex = newIndex;
                    Log.Debug($"Updated camera index after rescan: {_currentCameraIndex} (EntityId: {_currentCameraEntityId})");
                }
                else
                {
                    // Current camera no longer exists, reset to first camera
                    Log.Info($"Current camera (EntityId: {_currentCameraEntityId}) no longer exists, resetting to first camera");
                    _currentCameraIndex = 0;
                    _currentCameraEntityId = 0;
                }
            }

            if (_indexedCameras.Count > 0)
            {
                Log.Info($"Indexed {_indexedCameras.Count} cameras across {gridsChecked} grids ({totalBlocks} total blocks)");

                // Check if capture resolution is sufficient for 2×2 grids
                CheckCaptureResolutionForGrids();

                // Multi-client mode: Distribute cameras to connections
                if (_useMultiClientMode)
                {
                    foreach (var connection in _clientConnections)
                    {
                        connection.UpdateCameras(_indexedCameras);
                    }
                    Log.Info($"Updated camera lists for {_clientConnections.Count} client connections");
                }
                // Single-client mode: Send camera index
                else if (!_cameraIndexSent && _client != null && _client.Connected)
                {
                    SendCameraIndex();
                    _cameraIndexSent = true;
                    Log.Info("Camera index sent to client (first time)");
                }

                // Also send to mod if connected
                if (_modClient != null && _modClient.Connected)
                {
                    SendCameraIndexToMod();
                    Log.Info("Camera index sent to mod");
                }
            }
            else
            {
                Log.Warn($"RescanCameras: 0 cameras found matching prefix '{_cameraPrefix}' (scanned {gridsChecked} grids, {totalBlocks} blocks)");
                if (_client != null && _client.Connected)
                    Send($"INFO Cameras indexed: {_indexedCameras.Count}");
            }
        }

        /// <summary>
        /// Checks if any camera has a 2×2 grid setup and warns if capture resolution is insufficient
        /// </summary>
        private void CheckCaptureResolutionForGrids()
        {
            bool hasGridSetup = false;

            foreach (var camera in _indexedCameras)
            {
                if (!_lcdsByEntityId.TryGetValue(camera.EntityId, out var lcdInfo))
                {
                    // Scan and cache — without this the result was thrown away, causing a second
                    // redundant full-world scan on the very next tick inside ProcessQueuedFrames.
                    lcdInfo = FindLcdsForCamera(camera.EntityId);
                    _lcdsByEntityId[camera.EntityId] = lcdInfo;
                }

                if (lcdInfo.HasGridMode)
                {
                    hasGridSetup = true;
                    break;
                }
            }

            if (hasGridSetup && (_config.CaptureWidth < _config.LcdGridResolution || _config.CaptureHeight < _config.LcdGridResolution))
            {
                Log.Warn("⚠️ ═══════════════════════════════════════════════════════════════");
                Log.Warn("⚠️  2×2 LCD GRID DETECTED - CAPTURE RESOLUTION TOO LOW!");
                Log.Warn($"⚠️  Current: {_config.CaptureWidth}×{_config.CaptureHeight}");
                Log.Warn($"⚠️  Required: {_config.LcdGridResolution}×{_config.LcdGridResolution} (or higher)");
                Log.Warn("⚠️");
                Log.Warn("⚠️  Your 2×2 LCD grids will show pixelated/stretched images!");
                Log.Warn("⚠️");
                Log.Warn($"⚠️  FIX: Set LCD Render Resolution to {_config.LcdGridResolution} in your config");
                Log.Warn($"⚠️       Single LCDs will automatically downscale from {_config.LcdGridResolution}→{_config.LcdSingleResolution}");
                Log.Warn("⚠️ ═══════════════════════════════════════════════════════════════");
            }
            else if (hasGridSetup)
            {
                Log.Debug($"✅ 2×2 LCD grids detected - capture resolution OK ({_config.CaptureWidth}×{_config.CaptureHeight})");
            }
        }

        private int GetOrAssignGridIndex(long gridEntityId)
        {
            if (_gridIndexByEntityId.TryGetValue(gridEntityId, out int idx))
                return idx;

            idx = _nextGridIndex++;
            if (_nextGridIndex > 999)
                _nextGridIndex = 1;

            _gridIndexByEntityId[gridEntityId] = idx;
            return idx;
        }

        private VRageMath.Color ParseColor(string rgb)
        {
            try
            {
                var parts = rgb.Split(',');
                if (parts.Length == 3)
                {
                    int r = int.Parse(parts[0].Trim());
                    int g = int.Parse(parts[1].Trim());
                    int b = int.Parse(parts[2].Trim());
                    return new VRageMath.Color(r, g, b);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to parse LCD font tint '{rgb}': {ex.Message}, using white");
            }

            // Default to white
            return new VRageMath.Color(255, 255, 255);
        }

        private int GetOrAssignOwnerIndex(long ownerIdentityId)
        {
            if (_ownerIndexByIdentityId.TryGetValue(ownerIdentityId, out int idx))
                return idx;

            idx = _nextOwnerIndex++;
            if (_nextOwnerIndex > 999)
                _nextOwnerIndex = 1;

            _ownerIndexByIdentityId[ownerIdentityId] = idx;
            return idx;
        }

        private void ParseCameraName(string customName, out string userName, out int gridIndex, out int ownerIndex)
        {
            userName = "Camera";
            gridIndex = 0;
            ownerIndex = 0;

            string trimmed = customName.Trim();
            if (!trimmed.StartsWith(_cameraPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            string rest = trimmed.Substring(_cameraPrefix.Length).Trim();
            if (string.IsNullOrEmpty(rest))
                return;

            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                userName = parts[0];
                return;
            }

            string last = parts[parts.Length - 1];

            // Try new format first: "Name 001" (grid index only, 3 digits)
            if (last.Length == 3 && int.TryParse(last, out int gIdx))
            {
                gridIndex = gIdx;
                ownerIndex = 0; // Not used anymore
                userName = string.Join(" ", parts, 0, parts.Length - 1);
            }
            // Try old format for backward compatibility: "Name 001002" (6 digits)
            else if (last.Length == 6 &&
                int.TryParse(last.Substring(0, 3), out int gIdxOld) &&
                int.TryParse(last.Substring(3, 3), out int oIdx))
            {
                gridIndex = gIdxOld;
                ownerIndex = oIdx;
                userName = string.Join(" ", parts, 0, parts.Length - 1);
            }
            else
            {
                userName = string.Join(" ", parts);
            }
        }

        private void ListCameras()
        {
            if (_indexedCameras.Count == 0)
            {
                Send("INFO No CCTV cameras found");
                return;
            }

            int index = 1;
            foreach (var cam in _indexedCameras)
            {
                Send($"IDX {index} CAM {cam.EntityId} {cam.DisplayName} GridIdx:{cam.GridIndex:000} OwnerIdx:{cam.OwnerIndex:000} Grid:{cam.GridName}");
                index++;
            }
        }

        private void Send(string text)
        {
            var stream = _stream; // local snapshot
            if (_client == null || stream == null)
            {
                return;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text + "\n");
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (IOException)
            {
                // Write timed out or client disconnected
                CleanupClient();
            }
            catch (SocketException)
            {
                CleanupClient();
            }
            catch (ObjectDisposedException) { /* stream closed between check and write */ }
            catch (Exception ex)
            {
                Log.Error(ex, "CCTVPlugin Send error");
                CleanupClient();
            }
        }

        private void SendToMod(string text)
        {
            if (_modClient == null || _modStream == null)
            {
                return;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text + "\n");
                _modStream.Write(data, 0, data.Length);
                _modStream.Flush();
            }
            catch (IOException)
            {
                // Mod disconnected - clean up silently (normal operation)
                CleanupModClient();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CCTVPlugin SendToMod error");
                CleanupModClient();
            }
        }

        private void CleanupClient()
        {
            try
            {
                _streamReader?.Close();
                _streamReader = null;
            }
            catch { }

            try
            {
                _stream?.Close();
                _stream = null;
            }
            catch { }

            try
            {
                _client?.Close();
                _client = null;
            }
            catch { }
        }

        private void CleanupModClient()
        {
            try
            {
                _modStream?.Close();
                _modStream = null;
            }
            catch { }

            try
            {
                _modClient?.Close();
                _modClient = null;
            }
            catch { }
        }

        public void Dispose()
        {
            Log.Info("CCTVPlugin: Disposing");

            if (_ctrlHandlerRegistered)
            {
                try { MyAPIGateway.Utilities.UnregisterMessageHandler(CTRL_MOD_CHANNEL, OnCameraControlModMessage); } catch { }
                try { MyAPIGateway.Multiplayer.UnregisterMessageHandler(CTRL_MESSAGE_ID, OnCameraControlMessage); } catch { }
                _ctrlHandlerRegistered = false;
            }

            // Stop all multi-client connections (listener threads + TCP)
            foreach (var connection in _clientConnections)
            {
                try
                {
                    connection.Dispose();
                    Log.Info($"Disposed connection: {connection.Name}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error disposing connection: {connection.Name}");
                }
            }
            _clientConnections.Clear();

            CleanupClient();
            CleanupModClient();

            try
            {
                _listener?.Stop();
                _listener = null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping listener");
            }

            try
            {
                _modListener?.Stop();
                _modListener = null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping mod listener");
            }
        }

        private class BufferedFrame
        {
            public string AsciiData;
            public string Mode;
            public int Width;
            public int Height;
            public DateTime Timestamp;

            // Store separate frames for different display types (for dual-resolution support)
            public string SingleLcdFrame;  // 181×181 frame for single LCD
            public string GridFrame;       // 362×362 frame for 2×2 grid
        }

        /// <summary>
        /// Processed frame ready to write to LCDs (pre-decoded on background thread)
        /// </summary>
        private class ProcessedFrame
        {
            public long CameraEntityId;
            public string DecodedAscii;
            public string Mode;
            public int Width;
            public int Height;
            public bool IsSingleLcdFrame;
            public bool IsGridFrame;
        }

        /// <summary>
        /// Holds LCD panels for a camera - can have BOTH single LCD AND 2×2 grid simultaneously
        /// </summary>
        private class LcdDisplayInfo
        {
            // MASTER LCDs (receive rendered frames)
            // Single LCD (181×181)
            public MyCubeBlock SingleLcd { get; set; }

            // 2×2 Grid (4 panels of 181×181 each, stitched into 362×362)
            public MyCubeBlock TopLeft { get; set; }
            public MyCubeBlock TopRight { get; set; }
            public MyCubeBlock BottomLeft { get; set; }
            public MyCubeBlock BottomRight { get; set; }

            // SLAVE LCDs (copy from master - lightweight, no re-rendering)
            // Each position can have multiple slaves (e.g., on different grids)
            public List<MyCubeBlock> SlaveTopLeft { get; set; } = new List<MyCubeBlock>();
            public List<MyCubeBlock> SlaveTopRight { get; set; } = new List<MyCubeBlock>();
            public List<MyCubeBlock> SlaveBottomLeft { get; set; } = new List<MyCubeBlock>();
            public List<MyCubeBlock> SlaveBottomRight { get; set; } = new List<MyCubeBlock>();
            public List<MyCubeBlock> SlaveSingle { get; set; } = new List<MyCubeBlock>();

            public bool HasSingleLcd => SingleLcd != null;

            public bool HasGridMode => TopLeft != null && TopRight != null && BottomLeft != null && BottomRight != null;

            public bool HasSlaves => SlaveSingle.Count > 0 || SlaveTopLeft.Count > 0 ||
                                     SlaveTopRight.Count > 0 || SlaveBottomLeft.Count > 0 ||
                                     SlaveBottomRight.Count > 0;

            public bool IsValid()
            {
                // Valid if it has either single LCD or complete grid (or both)
                return HasSingleLcd || HasGridMode;
            }

            public List<MyCubeBlock> GetAllPanels()
            {
                var result = new List<MyCubeBlock>();
                if (SingleLcd != null) result.Add(SingleLcd);
                if (TopLeft != null) result.Add(TopLeft);
                if (TopRight != null) result.Add(TopRight);
                if (BottomLeft != null) result.Add(BottomLeft);
                if (BottomRight != null) result.Add(BottomRight);

                // Include all slaves
                result.AddRange(SlaveSingle);
                result.AddRange(SlaveTopLeft);
                result.AddRange(SlaveTopRight);
                result.AddRange(SlaveBottomLeft);
                result.AddRange(SlaveBottomRight);

                return result;
            }
        }
    }
}

