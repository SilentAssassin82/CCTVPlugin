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
        private static CCTVCommon.PostProcessMode _postProcessMode = CCTVCommon.PostProcessMode.None;

        // Track current camera's LCD setup (for dual-resolution rendering)
        private static bool _currentCameraHasSingleLcd = false;
        private static bool _currentCameraHasGrid = false;
        private static int _lcdGridRes = 362;   // Render resolution for 2×2 grid (configurable)
        private static int _lcdSingleRes = 181; // Render resolution for single LCD (always lcdGridRes / 2)

        // Verbose logging toggle (enable with -v flag)
        private static bool _verboseLogging = false;
        private static int _frameCounter = 0;

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
            Console.WriteLine($"Connecting to {_serverHost}:{_serverPort}...");

            try
            {
                // Connect to Torch plugin
                _client = new TcpClient(_serverHost, _serverPort);
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

                Console.WriteLine("Connected to Torch plugin!");

                // Test connection
                _writer.WriteLine("PING");
                string response = _reader.ReadLine();  // reads PONG
                Console.WriteLine($"<< {response}");
                string ready = _reader.ReadLine();      // reads READY (server sends PONG then READY)
                Console.WriteLine($"<< {ready}");

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

                while (_client.Connected)
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

                                // Handle camera index
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
                                // Handle SPECTATOR command - re-enter spectator mode
                                else if (line == "SPECTATOR")
                                {
                                    Console.WriteLine($"[INFO] Re-entering spectator mode...");
                                    if (WindowsInputHelper.SendF8KeyToSpaceEngineers())
                                    {
                                        Console.WriteLine("[SUCCESS] F8 sent - spectator mode re-activated");
                                        Thread.Sleep(500);
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

                        // Capture and send frame
                        if ((DateTime.Now - lastCapture).TotalMilliseconds >= _captureIntervalMs)
                        {
                            CaptureAndSendFrame();
                            frameCount++;
                            lastCapture = DateTime.Now;

                            if (frameCount % 10 == 0)
                                Console.WriteLine($"[INFO] Frames sent: {frameCount}");
                        }

                        Thread.Sleep(10); // Small sleep to prevent CPU spinning
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Loop error: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] {ex.Message}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
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
                                _captureWidth = Math.Max(64, Math.Min(512, w));
                            break;
                        case "CaptureHeight":
                            if (int.TryParse(val, out int h))
                                _captureHeight = Math.Max(64, Math.Min(512, h));
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
                                _useDithering = dither;
                            break;
                        case "PostProcessMode":
                            if (Enum.TryParse<CCTVCommon.PostProcessMode>(val, out var mode))
                                _postProcessMode = mode;
                            break;
                        case "LcdGridResolution":
                            if (int.TryParse(val, out int gridRes))
                            {
                                int clamped = Math.Max(64, Math.Min(362, gridRes));
                                _lcdGridRes = (clamped % 2 != 0) ? clamped - 1 : clamped;
                                _lcdSingleRes = _lcdGridRes / 2;
                            }
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

                Bitmap capture = ScreenCapture.CaptureGameViewport(_captureWidth, _captureHeight);

                if (capture == null)
                {
                    Console.WriteLine("[ERROR] Screen capture returned null!");
                    return;
                }

                // Apply post-processing filter if enabled
                Bitmap processed = capture;
                if (_postProcessMode != CCTVCommon.PostProcessMode.None)
                {
                    processed = AsciiConverter.ApplyPostProcess(capture, _postProcessMode);
                    if (processed != capture)
                        capture.Dispose(); // Dispose original if a new bitmap was created
                }

                // ⚡ PARALLEL DUAL-FRAME RENDERING: Render both resolutions simultaneously
                // This is 30-50% faster than sequential rendering for dual-frame mode

                // IMPORTANT: Create resized bitmaps on main thread FIRST
                // (Bitmap is not thread-safe - can't read from multiple threads)
                Bitmap singleFrame = null;
                Bitmap gridFrame = null;

                if (_currentCameraHasSingleLcd)
                    singleFrame = new Bitmap(processed, _lcdSingleRes, _lcdSingleRes);

                if (_currentCameraHasGrid)
                    gridFrame = new Bitmap(processed, _lcdGridRes, _lcdGridRes);

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
                                string colorChars = _useDithering
                                    ? AsciiConverter.ConvertToColorCharsDithered(frameToConvert, res, res)
                                    : AsciiConverter.ConvertToColorChars(frameToConvert, res, res);
                                compressed = AsciiConverter.CompressAscii(colorChars);
                                mode = "COLORGZ";
                            }
                            else
                            {
                                string ascii = _useDithering
                                    ? AsciiConverter.ConvertToAsciiDithered(frameToConvert, res, res)
                                    : AsciiConverter.ConvertToAscii(frameToConvert, res, res, useBlockMode: true);
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
                                string colorChars = _useDithering
                                    ? AsciiConverter.ConvertToColorCharsDithered(frameToConvert, res, res)
                                    : AsciiConverter.ConvertToColorChars(frameToConvert, res, res);
                                compressed = AsciiConverter.CompressAscii(colorChars);
                                mode = "COLORGZ";
                            }
                            else
                            {
                                string ascii = _useDithering
                                    ? AsciiConverter.ConvertToAsciiDithered(frameToConvert, res, res)
                                    : AsciiConverter.ConvertToAscii(frameToConvert, res, res, useBlockMode: true);
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
                    string compressed;
                    string frameMode;

                    if (_useColorMode)
                    {
                        string colorChars = _useDithering
                            ? AsciiConverter.ConvertToColorCharsDithered(processed, _captureWidth, _captureHeight)
                            : AsciiConverter.ConvertToColorChars(processed, _captureWidth, _captureHeight);
                        compressed = AsciiConverter.CompressAscii(colorChars);
                        frameMode = "COLORGZ";
                    }
                    else
                    {
                        string ascii = _useDithering
                            ? AsciiConverter.ConvertToAsciiDithered(processed, _captureWidth, _captureHeight)
                            : AsciiConverter.ConvertToAscii(processed, _captureWidth, _captureHeight, useBlockMode: true);
                        compressed = AsciiConverter.CompressAscii(ascii);
                        frameMode = "GRAYGZ";
                    }

                    string frameCommand = $"FRAME {_captureWidth} {_captureHeight} {frameMode} {compressed}";

                    if (shouldLog)
                        Console.WriteLine($">> FRAME {_captureWidth} {_captureHeight} {frameMode} ... ({frameCommand.Length} bytes) [Legacy]");

                    _writer.WriteLine(frameCommand);
                }

                processed.Dispose();
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
