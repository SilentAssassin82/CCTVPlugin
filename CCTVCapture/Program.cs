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
        private static StreamReader _reader;
        private static StreamWriter _writer;

        // Camera coordinate index
        private static Dictionary<string, (double X, double Y, double Z)> _cameraIndex = new Dictionary<string, (double, double, double)>();

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

        static void Main(string[] args)
        {
            // Parse command-line arguments
            _verboseLogging = args.Any(a => a == "-v" || a == "--verbose");

            // Parse --port and --host arguments for multi-client support
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int port))
                    {
                        _serverPort = port;
                        Console.WriteLine($"[ARG] Using custom port: {_serverPort}");
                    }
                }
                else if ((args[i] == "--host" || args[i] == "-h") && i + 1 < args.Length)
                {
                    _serverHost = args[i + 1];
                    Console.WriteLine($"[ARG] Using custom host: {_serverHost}");
                }
            }

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
                _client.ReceiveTimeout = 0;    // reads are non-blocking (DataAvailable check)
                _client.NoDelay = true;

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

                Console.WriteLine("Connected to Torch plugin!");

                // --- HMAC challenge-response handshake ---
                // Read HELLO which contains the nonce: "HELLO <name> v1.0 CHALLENGE:<nonce>"
                string hello = _reader.ReadLine();
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
                _writer.WriteLine($"AUTH {hmacResponse}");
                Console.WriteLine(">> AUTH sent");
                // --- end handshake ---

                // Test connection
                _writer.WriteLine("PING");
                string response = _reader.ReadLine();  // reads PONG
                Console.WriteLine($"<< {response}");

                // Initialise heartbeat tracking after successful handshake
                _lastPingSent = DateTime.Now;
                _lastPongReceived = DateTime.Now;
                _connectionDead = false;

                // Request config from server (will arrive asynchronously in main loop)
                _writer.WriteLine("GETCONFIG");
                Console.WriteLine(">> GETCONFIG (waiting for response in message loop...)");

                Console.WriteLine($"Initial settings (before server config):");
                Console.WriteLine($"  Resolution: {_captureWidth}x{_captureHeight}");
                Console.WriteLine($"  FPS: {1000 / _captureIntervalMs}");
                Console.WriteLine($"  Color: {_useColorMode}, Dithering: {_useDithering}");
                Console.WriteLine("\nWaiting for commands...");
                Console.WriteLine("Press Ctrl+C to exit\n");

                // Request camera list
                _writer.WriteLine("LISTCAMERAS");
                Thread.Sleep(100);

                // Read initial responses (may include CONFIG, CAMERAS, etc.)
                bool earlyConfigReceived = false;
                while (_stream.DataAvailable)
                {
                    string line = _reader.ReadLine();
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
                _writer.WriteLine("CAMERA 1");

                // Wait up to 3 seconds for ALL startup messages (CONFIG, CAMERAS, OK)
                DateTime startupWaitStart = DateTime.Now;
                bool configReceived = earlyConfigReceived;
                while ((DateTime.Now - startupWaitStart).TotalSeconds < 3)
                {
                    if (_stream.DataAvailable)
                    {
                        string line = _reader.ReadLine();
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

                // Determine LCD mode based on capture resolution.
                // Output frames are rendered at _lcdSingleRes (single) and _lcdGridRes (grid);
                // capture resolution only affects initial screen grab quality.
                _currentCameraHasSingleLcd = _captureWidth >= _lcdSingleRes && _captureHeight >= _lcdSingleRes;
                _currentCameraHasGrid      = _captureWidth >= _lcdGridRes && _captureHeight >= _lcdGridRes;
                if (_currentCameraHasGrid)
                    Console.WriteLine($"[INFO] ✅ Dual-frame mode ACTIVATED: {_lcdSingleRes}×{_lcdSingleRes} (single) + {_lcdGridRes}×{_lcdGridRes} (grid)");
                else if (_currentCameraHasSingleLcd)
                    Console.WriteLine($"[INFO] ✅ Single-LCD mode: {_lcdSingleRes}×{_lcdSingleRes} output (capture {_captureWidth}×{_captureHeight})");
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
                        if (_stream.DataAvailable)
                        {
                            string line = _reader.ReadLine();
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
                                _writer.WriteLine("PING");
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
                try { _writer?.Close(); } catch { }
                try { _reader?.Close(); } catch { }
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                _writer = null;
                _reader = null;
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

        static void ParseServerConfig(string configLine)
        {
            try
            {
                // Format: CONFIG Key1=Value1 Key2=Value2 ...
                string[] parts = configLine.Substring(7).Split(' ');
                foreach (string part in parts)
                {
                    string[] kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;

                    string key = kv[0].Trim();
                    string val = kv[1].Trim();

                    switch (key)
                    {
                        case "CaptureWidth":
                                if (int.TryParse(val, out int w))
                                    _captureWidth = Math.Max(64, Math.Min(700, w));
                                break;
                            case "CaptureHeight":
                                if (int.TryParse(val, out int h))
                                    _captureHeight = Math.Max(64, Math.Min(700, h));
                            break;
                        case "CaptureFps":
                            if (int.TryParse(val, out int fps) && fps > 0)
                                _captureIntervalMs = 1000 / Math.Max(1, Math.Min(30, fps));
                            break;
                        case "UseColorMode":
                            if (bool.TryParse(val, out bool color))
                                _useColorMode = color;
                            break;
                        case "UseDithering":
                            if (bool.TryParse(val, out bool dither))
                            {
                                _useDithering = dither;
                                // Backward compat: if DitherMode hasn't been set yet,
                                // map the legacy bool to Bayer (the original default)
                                if (dither && _ditherMode == CCTVCommon.DitherMode.None)
                                    _ditherMode = CCTVCommon.DitherMode.Bayer;
                                else if (!dither)
                                    _ditherMode = CCTVCommon.DitherMode.None;
                            }
                            break;
                        case "DitherMode":
                            if (Enum.TryParse<CCTVCommon.DitherMode>(val, out var ditherMode))
                            {
                                _ditherMode = ditherMode;
                                _useDithering = ditherMode != CCTVCommon.DitherMode.None;
                            }
                            break;
                        case "PostProcessMode":
                            if (Enum.TryParse<CCTVCommon.PostProcessMode>(val, out var mode))
                                _postProcessMode = mode;
                            break;
                        case "GridPostProcessMode":
                            if (Enum.TryParse<CCTVCommon.PostProcessMode>(val, out var gridMode))
                                _gridPostProcessMode = gridMode;
                            break;
                        case "LcdGridResolution":
                            if (int.TryParse(val, out int gridRes))
                            {
                                int clamped = Math.Max(64, Math.Min(700, gridRes));
                                _lcdGridRes = (clamped % 2 != 0) ? clamped - 1 : clamped;
                                _lcdSingleRes = _lcdGridRes / 2;
                            }
                            break;
                        case "DesaturateColorMode":
                            if (bool.TryParse(val, out bool desat))
                                _desaturateColorMode = desat;
                            break;
                        case "NightVisionMode":
                            if (bool.TryParse(val, out bool nv))
                                _nightVisionMode = nv;
                            break;
                        case "CropCaptureToSquare":
                            if (bool.TryParse(val, out bool crop))
                                _cropToSquare = crop;
                            break;
                        case "HorizontalSquash":
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hsquash))
                                _horizontalSquash = hsquash;
                            break;
                        case "SingleHorizontalSquash":
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float shsquash))
                                _singleHorizontalSquash = shsquash;
                            break;
                    }
                }

                Console.WriteLine("[CONFIG] Applied server settings");
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
                    int singleW = Math.Max(1, (int)(_lcdSingleRes * singleComp));
                    Bitmap resized = new Bitmap(capture, singleW, _lcdSingleRes);
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
                    int gridW = Math.Max(1, (int)(_lcdGridRes * gridComp));
                    Bitmap resized = new Bitmap(capture, gridW, _lcdGridRes);
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
                Task<(string compressed, string mode)> singleTask = null;
                Task<(string compressed, string mode)> gridTask = null;

                // Start single-LCD ASCII conversion on background thread
                if (singleFrame != null)
                {
                    Bitmap frameToConvert = singleFrame; // Capture for lambda
                    int res = _lcdSingleRes;
                    singleTask = Task.Run(() =>
                    {
                        try
                        {
                            string compressed;
                            string mode;

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
                                compressed = AsciiConverter.CompressAscii(colorChars);
                                mode = "COLORGZ";
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
                                compressed = AsciiConverter.CompressAscii(ascii);
                                mode = "GRAYGZ";
                            }

                            return (compressed, mode);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] {res}×{res} conversion failed: {ex.Message}");
                            throw;
                        }
                    });
                }

                // Start grid ASCII conversion on background thread
                if (gridFrame != null)
                {
                    Bitmap frameToConvert = gridFrame; // Capture for lambda
                    int res = _lcdGridRes;
                    gridTask = Task.Run(() =>
                    {
                        try
                        {
                            string compressed;
                            string mode;

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
                                compressed = AsciiConverter.CompressAscii(colorChars);
                                mode = "COLORGZ";
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
                                compressed = AsciiConverter.CompressAscii(ascii);
                                mode = "GRAYGZ";
                            }

                            return (compressed, mode);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] {res}×{res} conversion failed: {ex.Message}");
                            throw;
                        }
                    });
                }

                // Wait for both tasks to complete and send results
                if (singleTask != null)
                {
                    var result = singleTask.Result;
                    string singleFrameCommand = $"FRAME {_lcdSingleRes} {_lcdSingleRes} {result.mode} {result.compressed}";

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {_lcdSingleRes} {_lcdSingleRes} {result.mode} ... ({singleFrameCommand.Length} bytes) [Single LCD]");

                    _writer.WriteLine(singleFrameCommand);
                }

                if (gridTask != null)
                {
                    var result = gridTask.Result;
                    string gridFrameCommand = $"FRAME {_lcdGridRes} {_lcdGridRes} {result.mode} {result.compressed}";

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {_lcdGridRes} {_lcdGridRes} {result.mode} ... ({gridFrameCommand.Length} bytes) [Grid]");

                    _writer.WriteLine(gridFrameCommand);
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

                    string compressed;
                    string frameMode;

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
                        compressed = AsciiConverter.CompressAscii(colorChars);
                        frameMode = "COLORGZ";
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
                        compressed = AsciiConverter.CompressAscii(ascii);
                        frameMode = "GRAYGZ";
                    }

                    string frameCommand = $"FRAME {_captureWidth} {_captureHeight} {frameMode} {compressed}";

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {_captureWidth} {_captureHeight} {frameMode} ... ({frameCommand.Length} bytes) [Legacy]");

                    _writer.WriteLine(frameCommand);
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
    }
}
