using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Linq;

namespace CCTVCapture
{
    /// <summary>
    /// Helper class for sending keyboard input to Space Engineers window
    /// </summary>
    public static class WindowsInputHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // SendInput structures
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        public const ushort VK_F1 = 0x70;
        public const ushort VK_F8 = 0x77;
        public const ushort VK_F10 = 0x79;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_MENU = 0x12; // Alt key

        /// <summary>
        /// Finds the Space Engineers window
        /// </summary>
        public static IntPtr FindSpaceEngineersWindow()
        {
            var processes = Process.GetProcessesByName("SpaceEngineers");
            if (processes.Length == 0)
            {
                Console.WriteLine("[WARN] Space Engineers process not found");
                return IntPtr.Zero;
            }

            var mainWindow = processes[0].MainWindowHandle;
            if (mainWindow == IntPtr.Zero)
            {
                Console.WriteLine("[WARN] Space Engineers main window handle is null");
            }

            return mainWindow;
        }

        /// <summary>
        /// Sends F8 key to Space Engineers to toggle spectator mode
        /// </summary>
        public static bool SendF8KeyToSpaceEngineers()
        {
            IntPtr seWindow = FindSpaceEngineersWindow();
            if (seWindow == IntPtr.Zero)
            {
                Console.WriteLine("[ERROR] Could not find SE window to send F8");
                return false;
            }

            // Focus the window first
            SetForegroundWindow(seWindow);
            System.Threading.Thread.Sleep(200);

            return SendKey(VK_F8);
        }

        /// <summary>
        /// Sends a key press using SendInput (works with DirectX games)
        /// </summary>
        public static bool SendKey(ushort virtualKeyCode)
        {
            try
            {
                INPUT[] inputs = new INPUT[2];

                // Key down
                inputs[0] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = virtualKeyCode,
                            wScan = 0,
                            dwFlags = KEYEVENTF_KEYDOWN,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                // Key up
                inputs[1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = virtualKeyCode,
                            wScan = 0,
                            dwFlags = KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to send key: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a key combination (e.g., Alt+F10) using SendInput
        /// </summary>
        public static void SendKeyCombo(ushort modifierKey, ushort key)
        {
            INPUT[] inputs = new INPUT[4];

            // Modifier down
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = modifierKey,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYDOWN,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Key down
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = key,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYDOWN,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Key up
            inputs[2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = key,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Modifier up
            inputs[3] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = modifierKey,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Types text character by character using Unicode SendInput
        /// </summary>
        public static void SendText(string text)
        {
            foreach (char c in text)
            {
                INPUT[] inputs = new INPUT[2];

                // Character down
                inputs[0] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                // Character up
                inputs[1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Gets the title of the SE window to verify it's active
        /// </summary>
        public static string GetSpaceEngineersWindowTitle()
        {
            IntPtr seWindow = FindSpaceEngineersWindow();
            if (seWindow == IntPtr.Zero)
                return null;

            var title = new System.Text.StringBuilder(256);
            GetWindowText(seWindow, title, 256);
            return title.ToString();
        }
    }
}
