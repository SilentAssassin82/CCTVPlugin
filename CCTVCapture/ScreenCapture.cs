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
        /// Captures a specific region of the screen.
        /// Returns null if the graphics subsystem is unavailable (e.g. during SE reconnect
        /// when DirectX surfaces are being rebuilt).
        /// </summary>
        public static Bitmap CaptureRegion(int x, int y, int width, int height)
        {
            try
            {
                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                return bitmap;
            }
            catch (ExternalException)
            {
                // GDI+ failure — graphics driver busy or surfaces being rebuilt (SE reconnecting).
                // Also covers Win32Exception (subclass of ExternalException).
                // Return null so the caller can back off instead of crashing the driver.
                return null;
            }
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
        public static Bitmap CaptureGameViewport(int targetWidth, int targetHeight, bool cropToSquare = true)
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
            if (fullCapture == null) return null;

            if (cropToSquare)
            {
                // Crop to square (center crop) before resizing so a 16:9 source
                // isn't squashed into a 1:1 target.  This lets SE run at a normal
                // resolution without aspect-ratio distortion on the LCD output.
                int cropSize = Math.Min(captureWidth, captureHeight);
                int cropX = (captureWidth - cropSize) / 2;
                int cropY = (captureHeight - cropSize) / 2;

                Bitmap cropped = new Bitmap(cropSize, cropSize);
                using (Graphics g = Graphics.FromImage(cropped))
                {
                    g.DrawImage(fullCapture,
                        new Rectangle(0, 0, cropSize, cropSize),
                        new Rectangle(cropX, cropY, cropSize, cropSize),
                        GraphicsUnit.Pixel);
                }
                fullCapture.Dispose();

                // Resize cropped square to target dimensions
                if (cropSize != targetWidth || cropSize != targetHeight)
                {
                    Bitmap resized = new Bitmap(targetWidth, targetHeight);
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(cropped, 0, 0, targetWidth, targetHeight);
                    }
                    cropped.Dispose();
                    return resized;
                }

                return cropped;
            }
            else
            {
                // Stretch full viewport to target (wider FOV, distorted proportions)
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
}
