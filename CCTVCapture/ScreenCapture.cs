using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CCTVCapture
{
    /// <summary>
    /// Handles screen capture using GDI+ (works on all Windows versions)
    /// </summary>
    public class ScreenCapture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Captures a specific region of the screen
        /// </summary>
        public static Bitmap CaptureRegion(int x, int y, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }

        /// <summary>
        /// Finds Space Engineers window and returns its bounds
        /// </summary>
        public static Rectangle? FindSpaceEngineersWindow()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return null;

            var title = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, title, 256);

            // Check if it's Space Engineers
            string windowTitle = title.ToString();
            if (!windowTitle.Contains("Space Engineers"))
                return null;

            if (GetWindowRect(hWnd, out RECT rect))
            {
                return new Rectangle(
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top
                );
            }

            return null;
        }

        /// <summary>
        /// Captures the center portion of the Space Engineers window (viewport area)
        /// </summary>
        public static Bitmap CaptureGameViewport(int targetWidth, int targetHeight)
        {
            var window = FindSpaceEngineersWindow();
            if (!window.HasValue)
            {
                // Fallback: capture from configured position
                return CaptureRegion(100, 100, targetWidth, targetHeight);
            }

            // Calculate viewport area (exclude window chrome only)
            // No UI padding needed — CCTVCapture runs with HUD disabled in spectator/camera mode
            int borderWidth = 8;
            int titleBarHeight = 31;

            int viewportX = window.Value.X + borderWidth;
            int viewportY = window.Value.Y + titleBarHeight;
            int viewportWidth = window.Value.Width - (borderWidth * 2);
            int viewportHeight = window.Value.Height - titleBarHeight - borderWidth;

            // Capture center region
            int captureWidth = Math.Min(viewportWidth, 1920);
            int captureHeight = Math.Min(viewportHeight, 1080);
            
            int centerX = viewportX + (viewportWidth - captureWidth) / 2;
            int centerY = viewportY + (viewportHeight - captureHeight) / 2;

            Bitmap fullCapture = CaptureRegion(centerX, centerY, captureWidth, captureHeight);

            // Resize to target dimensions if needed
            if (captureWidth != targetWidth || captureHeight != targetHeight)
            {
                Bitmap resized = new Bitmap(targetWidth, targetHeight);
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(fullCapture, 0, 0, targetWidth, targetHeight);
                }
                fullCapture.Dispose();
                return resized;
            }

            return fullCapture;
        }
    }
}
