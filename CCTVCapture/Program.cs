using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CCTVCapture
{
    class Program
    {
        private static TcpClient _client;
        private static NetworkStream _stream;

        // Camera coordinate index
        private static Dictionary<string, (double X, double Y, double Z)> _cameraIndex = new Dictionary<string, (double, double, double)>();

        // Client-side user preferences (loaded from local XML, shown in ConfigForm)
        private static CCTVCommon.ClientSettings _clientSettings;

        private static string _serverHost = "localhost";
        private static int _serverPort = 12345;
        private static int _captureWidth = 178;
        private static int _captureHeight = 178;
        private static int _captureIntervalMs = 500;
        private static bool _useColorMode = true;
        private static bool _useDithering = false;
        private static CCTVCommon.DitherMode _ditherMode = CCTVCommon.DitherMode.None;
        private static CCTVCommon.PostProcessMode _postProcessMode = CCTVCommon.PostProcessMode.None;
        private static CCTVCommon.PostProcessMode _gridPostProcessMode = CCTVCommon.PostProcessMode.LightBlur;
        private static bool _desaturateColorMode = false;
        private static bool _nightVisionMode = false;
        private static bool _cropToSquare = true;
        private static float _horizontalSquash = 1.0f;
        private static float _singleHorizontalSquash = 1.0f;

        // Track current camera's LCD setup (for dual-resolution rendering)
        private static bool _currentCameraHasSingleLcd = false;
        private static bool _currentCameraHasGrid = false;
        private static int _lcdGridRes = 362;   // Render resolution for 2×2 grid (configurable)
        private static int _lcdSingleRes = 181; // Render resolution for single LCD (always lcdGridRes / 2)
        private static int _grayGridRes = 362;  // Grayscale grid resolution (independent of color)
        private static int _graySingleRes = 181; // Grayscale single resolution (always grayGridRes / 2)

        // Verbose logging toggle (enable with -v flag)
        private static bool _verboseLogging = false;
        private static int _frameCounter = 0;

        // Spectator mode tracking — prevents repeated F8 sends on every camera cycle.
        // On a single-machine setup CCTVCapture shares the SE window with the player,
        // so sending F8 on each cycle would toggle spectator mode mid-seat and lock them in.
        // Once we've entered spectator mode, suppress further F8s until a CAMERA switch
        // resets this flag (indicating the view needs re-acquiring after a teleport).
        private static bool _spectatorModeActive = false;

        // Capture backoff: when the graphics subsystem is unavailable (SE reconnecting,
        // driver busy), we back off exponentially instead of hammering CopyFromScreen
        // which can crash the graphics driver.
        private static int _consecutiveCaptureFailures = 0;
        private static int _captureBackoffMs = 0;
        private static DateTime _lastCaptureBackoffLog = DateTime.MinValue;

        // Connection health: heartbeat and reconnection
        private static DateTime _lastPingSent = DateTime.MinValue;
        private static DateTime _lastPongReceived = DateTime.MinValue;
        private static int _heartbeatIntervalMs = 15000;  // Send PING every 15 seconds
        private static int _heartbeatTimeoutMs = 45000;   // Declare dead if no PONG for 45 seconds
        private static bool _connectionDead = false;       // Set by write failures to break the loop
        private static int _maxReconnectAttempts = 10;
        private static int _reconnectDelayMs = 5000;       // 5 seconds between reconnect attempts

        [STAThread]
        static void Main(string[] args)
        {
            // Check for --nogui flag (skip settings UI, use saved/default settings)
            bool noGui = args.Any(a => a == "--nogui");

            // Load persisted client settings (or defaults on first run)
            _clientSettings = CCTVCommon.ClientSettings.Load();
            _clientSettings.Validate();

            // Parse command-line arguments — CLI overrides saved settings
            _verboseLogging = args.Any(a => a == "-v" || a == "--verbose") || _clientSettings.VerboseLogging;

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int port))
                        _clientSettings.Port = port;
                }
                else if ((args[i] == "--host" || args[i] == "-h") && i + 1 < args.Length)
                {
                    _clientSettings.Host = args[i + 1];
                }
            }

            // Show settings UI (unless --nogui)
#if CCTVCAPTURE
            if (!noGui)
            {
                if (!ConfigFormLauncher.ShowAndApply(_clientSettings))
                {
                    Console.WriteLine("[INFO] User cancelled — exiting.");
                    return;
                }
                // Settings were saved by the form on OK
            }
#endif

            // Apply client settings as initial values
            ApplyClientSettings();

            Console.WriteLine("=== CCTVCapture CCTV Screen Capture ===");
            if (_verboseLogging)
                Console.WriteLine("[VERBOSE MODE ENABLED]");

            for (int attempt = 0; attempt <= _maxReconnectAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    Console.WriteLine($"\n[RECONNECT] Attempt {attempt}/{_maxReconnectAttempts} in {_reconnectDelayMs / 1000}s...");
                    Thread.Sleep(_reconnectDelayMs);
                }

            Console.WriteLine($"Connecting to {_serverHost}:{_serverPort}...");

            try
            {
                // Connect to Torch plugin
                _client = new TcpClient(_serverHost, _serverPort);

                // Enable TCP KeepAlive so the OS detects dead connections.
                // Without this, a silently-dropped connection can go unnoticed
                // for up to 2 hours (Windows default).
                _client.Client.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.KeepAlive, true);
                // KeepAlive probes: start after 30s idle, retry every 5s, 3 retries
                // (IOControlCode byte layout: [onoff 4B][time_ms 4B][interval_ms 4B])
                byte[] keepAliveValues = new byte[12];
                BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);       // on
                BitConverter.GetBytes(30000).CopyTo(keepAliveValues, 4);   // 30s idle
                BitConverter.GetBytes(5000).CopyTo(keepAliveValues, 8);    // 5s interval
                _client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);

                _client.SendTimeout = 5000;    // 5s write timeout
                _client.ReceiveTimeout = 30000; // 30s read timeout — prevents blocking forever if server stalls
                _client.NoDelay = true;

                _stream = _client.GetStream();

                Console.WriteLine("Connected to Torch plugin!");

                // --- HMAC challenge-response handshake (plain text, pre-binary-protocol) ---
                // Read HELLO which contains the nonce: "HELLO <name> v1.0 CHALLENGE:<nonce>"
                // The handshake is still newline-terminated plain text; binary framing starts after AUTH.
                byte[] helloBuf = new byte[512];
                int helloLen = 0;
                while (helloLen < helloBuf.Length)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) break;
                    if (b == '\n') break;
                    helloBuf[helloLen++] = (byte)b;
                }
                string hello = Encoding.UTF8.GetString(helloBuf, 0, helloLen).TrimEnd('\r');
                Console.WriteLine($"<< {hello}");
                string nonce = null;
                if (hello != null)
                {
                    int idx = hello.IndexOf("CHALLENGE:", StringComparison.Ordinal);
                    if (idx >= 0)
                        nonce = hello.Substring(idx + 10).Trim();
                }
                if (nonce == null)
                {
                    Console.WriteLine("❌ No challenge received — server may be outdated or untrusted. Disconnecting.");
                    return;
                }
                byte[] nonceBytes = System.Text.Encoding.UTF8.GetBytes(nonce);
                byte[] keyBytes   = System.Text.Encoding.UTF8.GetBytes(CCTVCommon.BuildToken.Value);
                string hmacResponse;
                using (var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes))
                    hmacResponse = Convert.ToBase64String(hmac.ComputeHash(nonceBytes));

                // Send AUTH as plain text (still part of the handshake, pre-binary)
                byte[] authBytes = Encoding.UTF8.GetBytes($"AUTH {hmacResponse}\n");
                _stream.Write(authBytes, 0, authBytes.Length);
                Console.WriteLine(">> AUTH sent");
                // --- end handshake — binary framing starts here ---

                // Test connection with binary PING
                SendText("PING");
                Console.WriteLine(">> PING");

                // Initialise heartbeat tracking after successful handshake
                _lastPingSent = DateTime.Now;
                _lastPongReceived = DateTime.Now;
                _connectionDead = false;

                // Request config from server (will arrive asynchronously in main loop)
                SendText("GETCONFIG");
                Console.WriteLine(">> GETCONFIG (waiting for response in message loop...)");

                Console.WriteLine($"Initial settings (before server config):");
                Console.WriteLine($"  Resolution: {_captureWidth}x{_captureHeight}");
                Console.WriteLine($"  FPS: {1000 / _captureIntervalMs}");
                Console.WriteLine($"  Color: {_useColorMode}, Dithering: {_useDithering}");
                Console.WriteLine("\nWaiting for commands...");
                Console.WriteLine("Press Ctrl+C to exit\n");

                // Request camera list
                SendText("LISTCAMERAS");
                Thread.Sleep(100);

                // Read initial responses (may include CONFIG, CAMERAS, etc.)
                bool earlyConfigReceived = false;
                while (_client.Available >= 8)
                {
                    string line = ReadOneBinaryTextFrame();
                    if (line != null)
                    {
                        Console.WriteLine($"<< {line}");

                        // Parse CONFIG if it arrives early
                        if (line.StartsWith("CONFIG "))
                        {
                            ParseServerConfig(line);
                            Console.WriteLine($"✅ [CONFIG] Applied (early): {_captureWidth}x{_captureHeight} @ {1000 / _captureIntervalMs} FPS");
                            Console.WriteLine($"✅ [CONFIG] Color: {_useColorMode}, Dithering: {_useDithering}, PostProcess: {_postProcessMode}");
                            earlyConfigReceived = true;
                        }
                    }
                }

                // Check if Space Engineers is running
                string seTitle = WindowsInputHelper.GetSpaceEngineersWindowTitle();
                if (!string.IsNullOrEmpty(seTitle))
                {
                    Console.WriteLine($"[INFO] Found Space Engineers: {seTitle}");
                    Console.WriteLine("[INFO] Sending F8 to enter spectator mode...");

                    if (WindowsInputHelper.SendF8KeyToSpaceEngineers())
                    {
                        Console.WriteLine("[SUCCESS] F8 sent - entering spectator mode");
                        Thread.Sleep(1000); // Wait for spectator mode to activate
                    }
                    else
                    {
                        Console.WriteLine("[WARN] Could not send F8 - you may need to press F8 manually");
                    }
                }
                else
                {
                    Console.WriteLine("[WARN] Space Engineers window not found");
                    Console.WriteLine("[INFO] Please press F8 manually to enter spectator mode");
                }

                // Auto-switch to camera 1
                Console.WriteLine("\n[INFO] Switching to camera 1...");
                SendText("CAMERA 1");

                // Wait up to 3 seconds for ALL startup messages (CONFIG, CAMERAS, OK)
                DateTime startupWaitStart = DateTime.Now;
                bool configReceived = earlyConfigReceived;
                while ((DateTime.Now - startupWaitStart).TotalSeconds < 3)
                {
                    if (_client.Available >= 8)
                    {
                        string line = ReadOneBinaryTextFrame();
                        if (line != null)
                        {
                            Console.WriteLine($"<< {line}");

                            // Parse CONFIG as soon as it arrives (if not already received)
                            if (line.StartsWith("CONFIG ") && !configReceived)
                            {
                                ParseServerConfig(line);
                                Console.WriteLine($"✅ [CONFIG] Applied: {_captureWidth}x{_captureHeight} @ {1000 / _captureIntervalMs} FPS");
                                Console.WriteLine($"✅ [CONFIG] Color: {_useColorMode}, Dithering: {_useDithering}, PostProcess: {_postProcessMode}");
                                configReceived = true;
                            }

                            // Stop waiting once we get the OK response
                            if (line.StartsWith("OK "))
                            {
                                break;
                            }
                        }
                    }
                    Thread.Sleep(50);
                }

                if (!configReceived)
                {
                    Console.WriteLine("[WARN] No CONFIG received from server after 3 seconds, using defaults");
                }

                // Send client preferences to server (alignment offsets, display FPS).
                // These override the server's global defaults for this connection only.
                SendClientPrefs();

                // Determine LCD mode based on capture resolution and active mode.
                // Color mode uses _lcdSingleRes/_lcdGridRes; grayscale uses _graySingleRes/_grayGridRes.
                int activeSingleRes = _useColorMode ? _lcdSingleRes : _graySingleRes;
                int activeGridRes   = _useColorMode ? _lcdGridRes   : _grayGridRes;
                _currentCameraHasSingleLcd = _captureWidth >= activeSingleRes && _captureHeight >= activeSingleRes;
                _currentCameraHasGrid      = _captureWidth >= activeGridRes && _captureHeight >= activeGridRes;
                if (_currentCameraHasGrid)
                    Console.WriteLine($"[INFO] ✅ Dual-frame mode ACTIVATED: {activeSingleRes}×{activeSingleRes} (single) + {activeGridRes}×{activeGridRes} (grid) [{(_useColorMode ? "color" : "grayscale")}]");
                else if (_currentCameraHasSingleLcd)
                    Console.WriteLine($"[INFO] ✅ Single-LCD mode: {activeSingleRes}×{activeSingleRes} output (capture {_captureWidth}×{_captureHeight}) [{(_useColorMode ? "color" : "grayscale")}]");
                else
                    Console.WriteLine($"[INFO] Legacy single-frame mode: {_captureWidth}×{_captureHeight}");

                Console.WriteLine("[INFO] Starting frame capture...\n");

                // Start capture loop
                int frameCount = 0;
                DateTime lastCapture = DateTime.Now;

                while (_client.Connected && !_connectionDead)
                {
                    try
                    {
                        // Check for incoming messages
                        if (_client.Available >= 8)
                        {
                            string line = ReadOneBinaryTextFrame();
                            if (line != null)
                            {
                                Console.WriteLine($"<< {line}");

                                // Handle CONFIG response
                                if (line.StartsWith("CONFIG "))
                                {
                                    ParseServerConfig(line);
                                    Console.WriteLine($"✅ [CONFIG] Applied: {_captureWidth}x{_captureHeight} @ {1000 / _captureIntervalMs} FPS");
                                    Console.WriteLine($"✅ [CONFIG] Color: {_useColorMode}, Dithering: {_useDithering}, PostProcess: {_postProcessMode}");

                                    // Update LCD mode based on new resolution
                                                    _currentCameraHasSingleLcd = _captureWidth >= _lcdSingleRes && _captureHeight >= _lcdSingleRes;
                                                    _currentCameraHasGrid      = _captureWidth >= _lcdGridRes && _captureHeight >= _lcdGridRes;
                                                    if (_currentCameraHasGrid)
                                                        Console.WriteLine($"✅ [CONFIG] Dual-frame mode: {_lcdSingleRes}×{_lcdSingleRes} (single) + {_lcdGridRes}×{_lcdGridRes} (grid)");
                                                    else if (_currentCameraHasSingleLcd)
                                                        Console.WriteLine($"✅ [CONFIG] Single-LCD mode: {_lcdSingleRes}×{_lcdSingleRes} output (capture {_captureWidth}×{_captureHeight})");
                                    continue;
                                }

                                // Handle PONG heartbeat response
                                if (line.Trim() == "PONG")
                                {
                                    _lastPongReceived = DateTime.Now;
                                    if (_verboseLogging)
                                        Console.WriteLine("[HEARTBEAT] PONG received");
                                    continue;
                                }
                                if (line.StartsWith("INDEX ") && !line.Contains("COMPLETE"))
                                {
                                    string[] parts = line.Split(' ');
                                    if (parts.Length >= 5)
                                    {
                                        string cameraName = parts[1];
                                        double x = double.Parse(parts[2]);
                                        double y = double.Parse(parts[3]);
                                        double z = double.Parse(parts[4]);

                                        _cameraIndex[cameraName] = (x, y, z);
                                        Console.WriteLine($"[INDEX] Camera '{cameraName}' at ({x:F0}, {y:F0}, {z:F0})");
                                    }
                                }
                                else if (line == "INDEX_COMPLETE")
                                {
                                    Console.WriteLine($"[INFO] Camera index loaded: {_cameraIndex.Count} cameras");
                                }
                                // CAMERA switch notification — the server is changing views.
                                // Spectator mode stays active (GOTO repositions the camera
                                // without needing to exit/re-enter spectator mode via F8).
                                else if (line.StartsWith("CAMERA "))
                                {
                                    // _spectatorModeActive intentionally NOT reset here.
                                    // F8 is a toggle — resending it would EXIT spectator mode.
                                }
                                // Handle SPECTATOR command - re-enter spectator mode
                                else if (line == "SPECTATOR")
                                {
                                    if (!_spectatorModeActive)
                                    {
                                        Console.WriteLine($"[INFO] Re-entering spectator mode...");
                                        if (WindowsInputHelper.SendF8KeyToSpaceEngineers())
                                        {
                                            Console.WriteLine("[SUCCESS] F8 sent - spectator mode re-activated");
                                            _spectatorModeActive = true;
                                            Thread.Sleep(500);
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("[INFO] SPECTATOR: already active, skipping F8");
                                    }
                                }
                                // Handle GOTO commands (notification only - server handles teleport)
                                else if (line.StartsWith("GOTO "))
                                {
                                    string cameraName = line.Substring(5);
                                    Console.WriteLine($"[INFO] Server teleporting to camera '{cameraName}'...");
                                    // Note: Server-side teleport via character.SetPosition()
                                    // CCTVCapture.exe just captures the screen after teleport
                                }
                                // Handle old TELEPORT commands (legacy - deprecated)
                                else if (line.StartsWith("TELEPORT "))
                                {
                                    Console.WriteLine($"[WARN] TELEPORT command is deprecated - server now handles teleportation");
                                }
                            }
                        }

                        // --- Heartbeat: send periodic PING and detect dead connections ---
                        if ((DateTime.Now - _lastPingSent).TotalMilliseconds >= _heartbeatIntervalMs)
                        {
                            try
                            {
                                SendText("PING");
                                   _lastPingSent = DateTime.Now;
                                if (_verboseLogging)
                                    Console.WriteLine("[HEARTBEAT] PING sent");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[HEARTBEAT] PING write failed — connection dead: {ex.Message}");
                                _connectionDead = true;
                                break;
                            }
                        }

                        // Check heartbeat timeout: no PONG received within the timeout window
                        if ((DateTime.Now - _lastPongReceived).TotalMilliseconds >= _heartbeatTimeoutMs)
                        {
                            Console.WriteLine($"[HEARTBEAT] No PONG received for {_heartbeatTimeoutMs / 1000}s — connection dead");
                            _connectionDead = true;
                            break;
                        }

                        // Capture and send frame (with backoff if graphics subsystem is down)
                        int effectiveInterval = _captureIntervalMs + _captureBackoffMs;
                        if ((DateTime.Now - lastCapture).TotalMilliseconds >= effectiveInterval)
                        {
                            CaptureAndSendFrame();
                            frameCount++;
                            lastCapture = DateTime.Now;

                            if (frameCount % 10 == 0)
                                Console.WriteLine($"[INFO] Frames sent: {frameCount}");
                        }

                        Thread.Sleep(10); // Small sleep to prevent CPU spinning
                    }
                    catch (IOException)
                    {
                        Console.WriteLine("[ERROR] Loop: connection lost (IO error)");
                        _connectionDead = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Loop error: {ex.Message}");
                        if (ex.InnerException is SocketException || ex.InnerException is IOException)
                        {
                            _connectionDead = true;
                            break;
                        }
                        Thread.Sleep(1000);
                    }
                }

                // Connection ended — clean up before potential reconnect
                Console.WriteLine("[INFO] Connection ended, cleaning up...");
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                _stream = null;
                _client = null;

                // If the connection died, loop back and reconnect
                if (_connectionDead)
                {
                    Console.WriteLine("[INFO] Will attempt to reconnect...");
                    _spectatorModeActive = false;
                    continue;
                }

                // Clean disconnect (server shut down gracefully) — stop
                break;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[ERROR] Connection failed: {ex.Message}");
                try { _client?.Close(); } catch { }
                _client = null;
                continue; // Try reconnecting
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] {ex.Message}");
                break;
            }
            } // end reconnect loop

            Console.WriteLine("\n[INFO] CCTVCapture exiting.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        /// <summary>
        /// Apply client settings as the initial runtime values (before server CONFIG arrives).
        /// </summary>
        static void ApplyClientSettings()
        {
            _serverHost = _clientSettings.Host;
            _serverPort = _clientSettings.Port;
            _verboseLogging = _clientSettings.VerboseLogging;
            _useColorMode = _clientSettings.UseColorMode;
            _desaturateColorMode = _clientSettings.DesaturateColorMode;
            _nightVisionMode = _clientSettings.NightVisionMode;
            _cropToSquare = _clientSettings.CropCaptureToSquare;
            _horizontalSquash = _clientSettings.HorizontalSquash;
            _singleHorizontalSquash = _clientSettings.SingleHorizontalSquash;
            _captureIntervalMs = 1000 / Math.Max(1, _clientSettings.PreferredCaptureFps);

            if (Enum.TryParse<CCTVCommon.DitherMode>(_clientSettings.DitherMode, out var dm))
            {
                _ditherMode = dm;
                _useDithering = dm != CCTVCommon.DitherMode.None;
            }
            if (Enum.TryParse<CCTVCommon.PostProcessMode>(_clientSettings.PostProcessMode, out var pp))
                _postProcessMode = pp;
            if (Enum.TryParse<CCTVCommon.PostProcessMode>(_clientSettings.GridPostProcessMode, out var gpp))
                _gridPostProcessMode = gpp;

            Console.WriteLine($"[CLIENT] Settings applied: {_serverHost}:{_serverPort}");
            Console.WriteLine($"[CLIENT]   Color={_useColorMode} Desat={_desaturateColorMode} NV={_nightVisionMode} Crop={_cropToSquare}");
            Console.WriteLine($"[CLIENT]   CaptureFPS={_clientSettings.PreferredCaptureFps} DisplayFPS={_clientSettings.PreferredDisplayFps} Dither={_ditherMode} PostProc={_postProcessMode} GridPostProc={_gridPostProcessMode}");
            Console.WriteLine($"[CLIENT]   Squash grid={_horizontalSquash:F2} single={_singleHorizontalSquash:F2}");
            Console.WriteLine($"[CLIENT]   GridVOffset={_clientSettings.GridVerticalOffset} GridHOffset={_clientSettings.GridHorizontalOffset} GridShift={_clientSettings.GridContentShift} SingleShift={_clientSettings.SingleContentShift}");
        }

        /// <summary>
        /// Send CLIENTPREFS to the server so it can apply per-connection overrides
        /// for alignment offsets and display FPS.
        /// </summary>
        static void SendClientPrefs()
        {
            try
            {
                int effectiveDisplayFps = _clientSettings.EffectiveDisplayFps;
                string prefs = $"CLIENTPREFS " +
                    $"DisplayFps={effectiveDisplayFps} " +
                    $"GridVerticalOffset={_clientSettings.GridVerticalOffset} " +
                    $"GridHorizontalOffset={_clientSettings.GridHorizontalOffset} " +
                    $"GridContentShift={_clientSettings.GridContentShift} " +
                    $"SingleContentShift={_clientSettings.SingleContentShift} " +
                    $"LcdFontTint={_clientSettings.LcdFontTint}";
                SendText(prefs);
                Console.WriteLine($">> CLIENTPREFS sent (DisplayFPS={effectiveDisplayFps}, " +
                    $"GridVOff={_clientSettings.GridVerticalOffset}, GridHOff={_clientSettings.GridHorizontalOffset}, " +
                    $"GridShift={_clientSettings.GridContentShift}, SingleShift={_clientSettings.SingleContentShift}, " +
                    $"Tint={_clientSettings.LcdFontTint})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to send CLIENTPREFS: {ex.Message}");
            }
        }

        static void ParseServerConfig(string configLine)
        {
            try
            {
                // Format: CONFIG Key1=Value1 Key2=Value2 ...
                // Server-enforced: resolution, grid resolution (client cannot override).
                // Server-max-bounded: CaptureFps, DisplayFps (client can lower via preferences).
                // Client-controlled: visual settings — applied from _clientSettings, server
                //   values are ignored so the user's local preferences take precedence.
                string[] parts = configLine.Substring(7).Split(' ');
                foreach (string part in parts)
                {
                    string[] kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;

                    string key = kv[0].Trim();
                    string val = kv[1].Trim();

                    switch (key)
                    {
                        // ── Server-enforced (resolution must match LCD layout) ──
                        case "CaptureWidth":
                                if (int.TryParse(val, out int w))
                                    _captureWidth = Math.Max(64, Math.Min(700, w));
                                break;
                            case "CaptureHeight":
                                if (int.TryParse(val, out int h))
                                    _captureHeight = Math.Max(64, Math.Min(700, h));
                            break;
                        case "LcdGridResolution":
                            if (int.TryParse(val, out int gridRes))
                            {
                                int clamped = Math.Max(64, Math.Min(700, gridRes));
                                _lcdGridRes = (clamped % 2 != 0) ? clamped - 1 : clamped;
                                _lcdSingleRes = _lcdGridRes / 2;
                            }
                            break;
                        case "GrayscaleGridResolution":
                            if (int.TryParse(val, out int grayRes))
                            {
                                int clamped = Math.Max(64, Math.Min(700, grayRes));
                                _grayGridRes = (clamped % 2 != 0) ? clamped - 1 : clamped;
                                _graySingleRes = _grayGridRes / 2;
                            }
                            break;

                        // ── Server-max-bounded (client can lower, not exceed) ──
                        case "CaptureFps":
                            if (int.TryParse(val, out int serverFps) && serverFps > 0)
                            {
                                int serverMax = Math.Max(1, Math.Min(30, serverFps));
                                _clientSettings.ServerMaxFps = serverMax;
                                int effectiveFps = _clientSettings.EffectiveFps;
                                _captureIntervalMs = 1000 / effectiveFps;
                                if (_clientSettings.PreferredCaptureFps > serverMax)
                                    Console.WriteLine($"[CONFIG] Capture FPS clamped to server max: {effectiveFps} (preferred {_clientSettings.PreferredCaptureFps}, server max {serverMax})");
                            }
                            break;
                        case "DisplayFps":
                            if (int.TryParse(val, out int serverDispFps) && serverDispFps > 0)
                            {
                                int serverMaxDisp = Math.Max(1, Math.Min(10, serverDispFps));
                                _clientSettings.ServerMaxDisplayFps = serverMaxDisp;
                                if (_clientSettings.PreferredDisplayFps > serverMaxDisp)
                                    Console.WriteLine($"[CONFIG] Display FPS clamped to server max: {_clientSettings.EffectiveDisplayFps} (preferred {_clientSettings.PreferredDisplayFps}, server max {serverMaxDisp})");
                            }
                            break;

                        // ── Client-controlled visual settings ──
                        // These are intentionally NOT overridden by the server.
                        // The client's local preferences (from ConfigForm) take precedence.
                        case "UseColorMode":
                        case "UseDithering":
                        case "DitherMode":
                        case "PostProcessMode":
                        case "GridPostProcessMode":
                        case "DesaturateColorMode":
                        case "NightVisionMode":
                        case "CropCaptureToSquare":
                        case "HorizontalSquash":
                        case "SingleHorizontalSquash":
                            // Ignored — using client preferences
                            break;
                    }
                }

                Console.WriteLine($"[CONFIG] Server settings applied (resolution: {_captureWidth}x{_captureHeight}, color grid: {_lcdGridRes}, gray grid: {_grayGridRes}, FPS: {1000 / _captureIntervalMs})");
                Console.WriteLine($"[CONFIG] Client visual preferences retained: Color={_useColorMode} Desat={_desaturateColorMode} NV={_nightVisionMode} Dither={_ditherMode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to parse server config: {ex.Message}");
            }
        }

        static void CaptureAndSendFrame()
        {
            try
            {
                _frameCounter++;
                bool shouldLog = _verboseLogging || (_frameCounter % 10 == 0);

                // Capture screen
                if (_verboseLogging)
                    Console.WriteLine("[DEBUG] Capturing screen...");

                // Capture with the wider of the two squash values so both LCD types
                // have enough horizontal content.  Per-LCD compensation is applied below.
                float maxSquash = Math.Max(_horizontalSquash, _singleHorizontalSquash);
                Bitmap capture = ScreenCapture.CaptureGameViewport(_captureWidth, _captureHeight, _cropToSquare, maxSquash);

                if (capture == null)
                {
                    // Graphics subsystem unavailable — SE may be reconnecting or driver busy.
                    // Back off exponentially: 500ms → 1s → 2s → 4s (capped) to avoid
                    // hammering the GPU during DirectX surface rebuilds.
                    _consecutiveCaptureFailures++;
                    _captureBackoffMs = Math.Min(4000, 500 * (1 << Math.Min(_consecutiveCaptureFailures - 1, 3)));

                    if ((DateTime.Now - _lastCaptureBackoffLog).TotalSeconds >= 5)
                    {
                        Console.WriteLine($"[WARN] Screen capture unavailable (fail #{_consecutiveCaptureFailures}, backoff {_captureBackoffMs}ms) — SE reconnecting?");
                        _lastCaptureBackoffLog = DateTime.Now;
                    }
                    return;
                }

                // Capture succeeded — reset backoff
                if (_consecutiveCaptureFailures > 0)
                {
                    Console.WriteLine($"[INFO] Screen capture recovered after {_consecutiveCaptureFailures} failures");
                    _consecutiveCaptureFailures = 0;
                    _captureBackoffMs = 0;
                }

                // ⚡ PARALLEL DUAL-FRAME RENDERING: Render both resolutions simultaneously
                // This is 30-50% faster than sequential rendering for dual-frame mode

                // Select resolutions based on active mode (color vs grayscale)
                int effectiveSingleRes = _useColorMode ? _lcdSingleRes : _graySingleRes;
                int effectiveGridRes   = _useColorMode ? _lcdGridRes   : _grayGridRes;

                // IMPORTANT: Create resized bitmaps on main thread FIRST
                // (Bitmap is not thread-safe - can't read from multiple threads)
                // Post-processing is applied per resolution after resize:
                //   singleFrame uses _postProcessMode (default: None for sharp single LCD)
                //   gridFrame   uses _gridPostProcessMode (default: LightBlur for smooth grid)
                Bitmap singleFrame = null;
                Bitmap gridFrame = null;

                if (_currentCameraHasSingleLcd)
                {
                    // Compensate: capture has maxSquash baked in, single LCD wants _singleHorizontalSquash.
                    // Making the bitmap narrower lets the converter stretch undo the excess squash.
                    float singleComp = (maxSquash > 0f) ? (_singleHorizontalSquash / maxSquash) : 1f;
                    int singleW = Math.Max(1, (int)(effectiveSingleRes * singleComp));
                    Bitmap resized = new Bitmap(capture, singleW, effectiveSingleRes);
                    if (_postProcessMode != CCTVCommon.PostProcessMode.None)
                    {
                        singleFrame = AsciiConverter.ApplyPostProcess(resized, _postProcessMode);
                        if (singleFrame != resized) resized.Dispose();
                    }
                    else
                    {
                        singleFrame = resized;
                    }
                    if (_useColorMode && _desaturateColorMode)
                        AsciiConverter.DesaturateBitmap(singleFrame, _nightVisionMode);
                }

                if (_currentCameraHasGrid)
                {
                    // Compensate: capture has maxSquash, grid wants _horizontalSquash.
                    float gridComp = (maxSquash > 0f) ? (_horizontalSquash / maxSquash) : 1f;
                    int gridW = Math.Max(1, (int)(effectiveGridRes * gridComp));
                    Bitmap resized = new Bitmap(capture, gridW, effectiveGridRes);
                    if (_gridPostProcessMode != CCTVCommon.PostProcessMode.None)
                    {
                        gridFrame = AsciiConverter.ApplyPostProcess(resized, _gridPostProcessMode);
                        if (gridFrame != resized) resized.Dispose();
                    }
                    else
                    {
                        gridFrame = resized;
                    }
                    if (_useColorMode && _desaturateColorMode)
                        AsciiConverter.DesaturateBitmap(gridFrame, _nightVisionMode);
                }

                // Now parallelize the CPU-heavy ASCII conversion (thread-safe)
                Task<(byte[] gzBytes, byte flags)> singleTask = null;
                Task<(byte[] tl, byte[] tr, byte[] bl, byte[] br, bool isColor)> gridTask = null;

                // Start single-LCD ASCII conversion on background thread
                if (singleFrame != null)
                {
                    Bitmap frameToConvert = singleFrame; // Capture for lambda
                    int res = effectiveSingleRes;
                    singleTask = Task.Run(() =>
                    {
                        try
                        {
                            byte[] gzBytes;
                            byte flags;

                            if (_useColorMode)
                            {
                                string colorChars;
                                switch (_ditherMode)
                                {
                                    case CCTVCommon.DitherMode.Bayer:
                                        colorChars = AsciiConverter.ConvertToColorCharsOrdered(frameToConvert, res, res);
                                        break;
                                    case CCTVCommon.DitherMode.FloydSteinberg:
                                        colorChars = AsciiConverter.ConvertToColorCharsDithered(frameToConvert, res, res);
                                        break;
                                    default:
                                        colorChars = AsciiConverter.ConvertToColorChars(frameToConvert, res, res);
                                        break;
                                }
                                gzBytes = AsciiConverter.CompressAsciiBytes(colorChars);
                                flags = (byte)(CCTVCommon.BinaryProtocol.FLAG_COLOR | CCTVCommon.BinaryProtocol.FLAG_GZ);
                            }
                            else
                            {
                                string ascii;
                                switch (_ditherMode)
                                {
                                    case CCTVCommon.DitherMode.Bayer:
                                        ascii = AsciiConverter.ConvertToAsciiOrdered(frameToConvert, res, res);
                                        break;
                                    case CCTVCommon.DitherMode.FloydSteinberg:
                                        ascii = AsciiConverter.ConvertToAsciiDithered(frameToConvert, res, res);
                                        break;
                                    default:
                                        ascii = AsciiConverter.ConvertToAscii(frameToConvert, res, res, useBlockMode: true);
                                        break;
                                }
                                gzBytes = AsciiConverter.CompressAsciiBytes(ascii);
                                flags = CCTVCommon.BinaryProtocol.FLAG_GZ;
                            }

                            return (gzBytes, flags);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] {res}×{res} conversion failed: {ex.Message}");
                            throw;
                        }
                    });
                }

                // Start grid ASCII conversion on background thread.
                // The task converts the full frame, splits it into 4 quadrants, and compresses
                // each independently so the game thread only needs 4× WriteText() calls.
                if (gridFrame != null)
                {
                    Bitmap frameToConvert = gridFrame; // Capture for lambda
                    int res = effectiveGridRes;
                    // Capture alignment settings by value — they are read-only once set
                    int captureVOffset = _clientSettings.GridVerticalOffset;
                    int captureHOffset = _clientSettings.GridHorizontalOffset;
                    int captureShift   = _clientSettings.GridContentShift;
                    gridTask = Task.Run(() =>
                    {
                        try
                        {
                            string fullContent;
                            bool isColorLocal;

                            if (_useColorMode)
                            {
                                string colorChars;
                                switch (_ditherMode)
                                {
                                    case CCTVCommon.DitherMode.Bayer:
                                        colorChars = AsciiConverter.ConvertToColorCharsOrdered(frameToConvert, res, res);
                                        break;
                                    case CCTVCommon.DitherMode.FloydSteinberg:
                                        colorChars = AsciiConverter.ConvertToColorCharsDithered(frameToConvert, res, res);
                                        break;
                                    default:
                                        colorChars = AsciiConverter.ConvertToColorChars(frameToConvert, res, res);
                                        break;
                                }
                                fullContent  = colorChars;
                                isColorLocal = true;
                            }
                            else
                            {
                                string ascii;
                                switch (_ditherMode)
                                {
                                    case CCTVCommon.DitherMode.Bayer:
                                        ascii = AsciiConverter.ConvertToAsciiOrdered(frameToConvert, res, res, forGrid: true);
                                        break;
                                    case CCTVCommon.DitherMode.FloydSteinberg:
                                        ascii = AsciiConverter.ConvertToAsciiDithered(frameToConvert, res, res, forGrid: true);
                                        break;
                                    default:
                                        ascii = AsciiConverter.ConvertToAscii(frameToConvert, res, res, useBlockMode: true, forGrid: true);
                                        break;
                                }
                                fullContent  = ascii;
                                isColorLocal = false;
                            }

                            // Split into 4 quadrants and compress each independently.
                            // CompressAscii reuses [ThreadStatic] buffers — sequential calls on
                            // the same thread are safe (SetLength(0) resets between calls).
                            var (tl, tr, bl, br) = SplitIntoQuads(fullContent, res, captureVOffset, captureHOffset, captureShift);
                            byte[] tlBytes = AsciiConverter.CompressAsciiBytes(tl);
                            byte[] trBytes = AsciiConverter.CompressAsciiBytes(tr);
                            byte[] blBytes = AsciiConverter.CompressAsciiBytes(bl);
                            byte[] brBytes = AsciiConverter.CompressAsciiBytes(br);

                            return (tlBytes, trBytes, blBytes, brBytes, isColorLocal);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] {res}×{res} grid conversion failed: {ex.Message}");
                            throw;
                        }
                    });
                }

                // Wait for both tasks to complete and send results
                if (singleTask != null)
                {
                    var result = singleTask.Result;

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {effectiveSingleRes}\u00d7{effectiveSingleRes} flags=0x{result.flags:X2} ({result.gzBytes.Length} bytes) [Single LCD]");

                    SendFrameBinary(effectiveSingleRes, effectiveSingleRes, result.flags, result.gzBytes);
                }

                if (gridTask != null)
                {
                    var (tlBytes, trBytes, blBytes, brBytes, isColorResult) = gridTask.Result;
                    byte colorFlag = isColorResult ? CCTVCommon.BinaryProtocol.FLAG_COLOR : (byte)0;

                    if (shouldLog)
                        Console.WriteLine($">> QUAD TL/TR/BL/BR color={isColorResult} [{tlBytes.Length + trBytes.Length + blBytes.Length + brBytes.Length} bytes total] [Grid]");

                    SendQuadBinary(CCTVCommon.BinaryProtocol.QUAD_TL, colorFlag, tlBytes);
                    SendQuadBinary(CCTVCommon.BinaryProtocol.QUAD_TR, colorFlag, trBytes);
                    SendQuadBinary(CCTVCommon.BinaryProtocol.QUAD_BL, colorFlag, blBytes);
                    SendQuadBinary(CCTVCommon.BinaryProtocol.QUAD_BR, colorFlag, brBytes);
                }

                // Clean up resized bitmaps
                singleFrame?.Dispose();
                gridFrame?.Dispose();

                // Fallback: If LCD types unknown, send configured resolution
                if (!_currentCameraHasSingleLcd && !_currentCameraHasGrid)
                {
                    Bitmap fallbackSrc = capture;
                    Bitmap fallbackProcessed = null;
                    if (_postProcessMode != CCTVCommon.PostProcessMode.None)
                    {
                        fallbackProcessed = AsciiConverter.ApplyPostProcess(capture, _postProcessMode);
                        fallbackSrc = fallbackProcessed;
                    }
                    if (_useColorMode && _desaturateColorMode)
                        AsciiConverter.DesaturateBitmap(fallbackSrc, _nightVisionMode);

                    byte[] gzBytes;
                    byte flags;

                    if (_useColorMode)
                    {
                        string colorChars;
                        switch (_ditherMode)
                        {
                            case CCTVCommon.DitherMode.Bayer:
                                colorChars = AsciiConverter.ConvertToColorCharsOrdered(fallbackSrc, _captureWidth, _captureHeight);
                                break;
                            case CCTVCommon.DitherMode.FloydSteinberg:
                                colorChars = AsciiConverter.ConvertToColorCharsDithered(fallbackSrc, _captureWidth, _captureHeight);
                                break;
                            default:
                                colorChars = AsciiConverter.ConvertToColorChars(fallbackSrc, _captureWidth, _captureHeight);
                                break;
                        }
                        gzBytes = AsciiConverter.CompressAsciiBytes(colorChars);
                        flags = (byte)(CCTVCommon.BinaryProtocol.FLAG_COLOR | CCTVCommon.BinaryProtocol.FLAG_GZ);
                    }
                    else
                    {
                        string ascii;
                        switch (_ditherMode)
                        {
                            case CCTVCommon.DitherMode.Bayer:
                                ascii = AsciiConverter.ConvertToAsciiOrdered(fallbackSrc, _captureWidth, _captureHeight);
                                break;
                            case CCTVCommon.DitherMode.FloydSteinberg:
                                ascii = AsciiConverter.ConvertToAsciiDithered(fallbackSrc, _captureWidth, _captureHeight);
                                break;
                            default:
                                ascii = AsciiConverter.ConvertToAscii(fallbackSrc, _captureWidth, _captureHeight, useBlockMode: true);
                                break;
                        }
                        gzBytes = AsciiConverter.CompressAsciiBytes(ascii);
                        flags = CCTVCommon.BinaryProtocol.FLAG_GZ;
                    }

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {_captureWidth}×{_captureHeight} flags=0x{flags:X2} ({gzBytes.Length} bytes) [Legacy]");

                    SendFrameBinary(_captureWidth, _captureHeight, flags, gzBytes);
                    fallbackProcessed?.Dispose();
                }

                capture.Dispose();
            }
            catch (IOException)
            {
                Console.WriteLine("[ERROR] Frame write failed (connection lost)");
                _connectionDead = true;
            }
            catch (SocketException)
            {
                Console.WriteLine("[ERROR] Frame write failed (socket error)");
                _connectionDead = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Frame capture: {ex.Message}");
            }
        }

        static void ExecuteAdminTeleport(double x, double y, double z)
        {
            try
            {
                IntPtr seWindow = WindowsInputHelper.FindSpaceEngineersWindow();
                if (seWindow == IntPtr.Zero)
                {
                    Console.WriteLine("[ERROR] Could not find SE window");
                    return;
                }

                Console.WriteLine($"[INFO] Focusing SE window...");
                // SE window will be focused by Alt+F10
                Thread.Sleep(2000); // Wait 2 seconds for window to be truly ready

                Console.WriteLine($"[INFO] Sending Alt+F10...");
                WindowsInputHelper.SendKeyCombo(WindowsInputHelper.VK_MENU, WindowsInputHelper.VK_F10);
                Thread.Sleep(2000); // Wait 2 seconds for admin menu to fully open

                Console.WriteLine("[DEBUG] Admin menu should be open now");
                Thread.Sleep(500);

                // Tab to Teleport section
                Console.WriteLine("[DEBUG] Tabbing to teleport section...");
                for (int i = 0; i < 6; i++)
                {
                    Console.WriteLine($"[DEBUG] Tab {i + 1}/6");
                    WindowsInputHelper.SendKey(WindowsInputHelper.VK_TAB);
                    Thread.Sleep(300); // 300ms between each tab
                }

                Thread.Sleep(500); // Extra wait after tabs

                // Type X coordinate
                Console.WriteLine($"[DEBUG] Entering X coordinate: {x:F0}");
                WindowsInputHelper.SendText($"{x:F0}");
                Thread.Sleep(500);

                Console.WriteLine($"[DEBUG] Tab to Y");
                WindowsInputHelper.SendKey(WindowsInputHelper.VK_TAB);
                Thread.Sleep(300);

                Console.WriteLine($"[DEBUG] Entering Y coordinate: {y:F0}");
                WindowsInputHelper.SendText($"{y:F0}");
                Thread.Sleep(500);

                Console.WriteLine($"[DEBUG] Tab to Z");
                WindowsInputHelper.SendKey(WindowsInputHelper.VK_TAB);
                Thread.Sleep(300);

                Console.WriteLine($"[DEBUG] Entering Z coordinate: {z:F0}");
                WindowsInputHelper.SendText($"{z:F0}");
                Thread.Sleep(500);

                // Tab to Teleport button
                Console.WriteLine("[DEBUG] Tabbing to teleport button...");
                for (int i = 0; i < 2; i++)
                {
                    WindowsInputHelper.SendKey(WindowsInputHelper.VK_TAB);
                    Thread.Sleep(300);
                }

                Console.WriteLine("[DEBUG] Pressing Enter to teleport...");
                WindowsInputHelper.SendKey(WindowsInputHelper.VK_RETURN);
                Thread.Sleep(1000); // Wait for teleport to execute

                // Close admin menu
                Console.WriteLine("[DEBUG] Closing admin menu...");
                WindowsInputHelper.SendKeyCombo(WindowsInputHelper.VK_MENU, WindowsInputHelper.VK_F10);

                Console.WriteLine("[SUCCESS] ✅ Teleport sequence complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Admin teleport failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Splits a full grid ASCII/color frame string into 4 quadrant strings using the
        /// same offset/shift logic the plugin applies in WriteGridLCDs, so the game thread
        /// only needs 4× WriteText() calls with no line-scanning or extraction work.
        /// </summary>
        private static (string tl, string tr, string bl, string br) SplitIntoQuads(
            string content, int gridRes, int vOffset, int hOffset, int shift)
        {
            int[] lineStarts  = new int[gridRes + 2];
            int[] lineLengths = new int[gridRes + 2];
            int lineCount = 0, ls = 0;
            for (int i = 0; i <= content.Length; i++)
            {
                if (i == content.Length || content[i] == '\n')
                {
                    if (lineCount < lineStarts.Length)
                    {
                        lineStarts[lineCount]  = ls;
                        lineLengths[lineCount] = i - ls;
                        lineCount++;
                    }
                    ls = i + 1;
                }
            }

            if (lineCount < 4)
                return (content, string.Empty, string.Empty, string.Empty);

            int quadW          = gridRes / 2;
            int effectiveQuadH = lineCount / 2;

            // Mirror WriteGridLCDs offset calculation exactly
            int tlStartY = Math.Max(0, Math.Min(vOffset, effectiveQuadH - 1));
            int blStartY = Math.Max(0, effectiveQuadH - vOffset);
            int tlStartX = Math.Max(0, Math.Min(hOffset, quadW - 1) + shift);
            int trStartX = Math.Max(0, quadW - hOffset + shift);

            string tl = ExtractQuadrantStr(content, lineStarts, lineLengths, lineCount, tlStartX, tlStartY, quadW, effectiveQuadH);
            string tr = ExtractQuadrantStr(content, lineStarts, lineLengths, lineCount, trStartX, tlStartY, quadW, effectiveQuadH);
            string bl = ExtractQuadrantStr(content, lineStarts, lineLengths, lineCount, tlStartX, blStartY, quadW, effectiveQuadH);
            string br = ExtractQuadrantStr(content, lineStarts, lineLengths, lineCount, trStartX, blStartY, quadW, effectiveQuadH);

            return (tl, tr, bl, br);
        }

        /// <summary>
        /// Extracts a rectangular region from a pre-scanned content string.
        /// Mirrors the plugin's ExtractQuadrant logic so both sides produce identical text.
        /// </summary>
        private static string ExtractQuadrantStr(
            string content, int[] lineStarts, int[] lineLengths, int lineCount,
            int startX, int startY, int quadW, int quadH)
        {
            var sb = new StringBuilder(quadW * quadH + quadH);
            for (int y = 0; y < quadH && (startY + y) < lineCount; y++)
            {
                int li  = startY + y;
                int ls2 = lineStarts[li];
                int ll  = lineLengths[li];

                if (startX < ll)
                {
                    int sliceW = Math.Min(quadW, ll - startX);
                    sb.Append(content, ls2 + startX, sliceW);
                    if (sliceW < quadW)
                        sb.Append(' ', quadW - sliceW);
                }
                else
                {
                    sb.Append(' ', quadW);
                }

                if (y < quadH - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        static void SendText(string message)
        {
            CCTVCommon.BinaryProtocol.WriteTextFrame(_stream, message);
        }

        static string ReadOneBinaryTextFrame()
        {
            var (type, len) = CCTVCommon.BinaryProtocol.ReadHeader(_stream);
            byte[] buf = new byte[Math.Max(len, 1)];
            if (len > 0)
                CCTVCommon.BinaryProtocol.ReadExactly(_stream, buf, 0, len);
            return type == CCTVCommon.BinaryProtocol.MSG_TEXT
                ? Encoding.UTF8.GetString(buf, 0, len)
                : null;
        }

        static void SendFrameBinary(int width, int height, byte flags, byte[] gzBytes)
        {
            byte[] payload = new byte[9 + gzBytes.Length];
            payload[0] = (byte)( width         & 0xFF);
            payload[1] = (byte)((width  >>  8) & 0xFF);
            payload[2] = (byte)((width  >> 16) & 0xFF);
            payload[3] = (byte)((width  >> 24) & 0xFF);
            payload[4] = (byte)( height         & 0xFF);
            payload[5] = (byte)((height >>  8) & 0xFF);
            payload[6] = (byte)((height >> 16) & 0xFF);
            payload[7] = (byte)((height >> 24) & 0xFF);
            payload[8] = flags;
            Buffer.BlockCopy(gzBytes, 0, payload, 9, gzBytes.Length);
            CCTVCommon.BinaryProtocol.WriteFrame(_stream, CCTVCommon.BinaryProtocol.MSG_FRAME, payload, 0, payload.Length);
        }

        static void SendQuadBinary(byte quadId, byte colorFlag, byte[] gzBytes)
        {
            byte[] payload = new byte[2 + gzBytes.Length];
            payload[0] = quadId;
            payload[1] = colorFlag;
            Buffer.BlockCopy(gzBytes, 0, payload, 2, gzBytes.Length);
            CCTVCommon.BinaryProtocol.WriteFrame(_stream, CCTVCommon.BinaryProtocol.MSG_QUAD, payload, 0, payload.Length);
        }
    }
}
