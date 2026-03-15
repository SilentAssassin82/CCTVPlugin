using System;
using System.IO;
using System.Xml.Serialization;

namespace CCTVCommon
{
    /// <summary>
    /// Client-side user preferences for CCTVCapture.
    /// Persisted locally as XML next to the executable.
    /// Visual settings are fully client-controlled; FPS is clamped to the server maximum at runtime.
    /// </summary>
    [XmlRoot("CCTVCaptureSettings")]
    public class ClientSettings
    {
        // --- Connection ---
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 12345;

        // --- Visual mode ---
        public bool UseColorMode { get; set; } = true;
        public bool DesaturateColorMode { get; set; } = false;
        public bool NightVisionMode { get; set; } = false;
        public bool CropCaptureToSquare { get; set; } = true;

        // --- Quality ---
        /// <summary>
        /// Client-preferred capture FPS. Clamped to the server's CaptureFps maximum at runtime.
        /// XML element kept as "PreferredFps" for backward compatibility with existing settings files.
        /// </summary>
        [XmlElement("PreferredFps")]
        public int PreferredCaptureFps { get; set; } = 10;

        /// <summary>
        /// Client-preferred display/render FPS (LCD write rate).
        /// Clamped to the server's DisplayFps maximum at runtime.
        /// Typical use: capture at 4 FPS, display at 2 FPS (2:1 ratio).
        /// </summary>
        public int PreferredDisplayFps { get; set; } = 2;

        public string DitherMode { get; set; } = "None";
        public string PostProcessMode { get; set; } = "None";
        public string GridPostProcessMode { get; set; } = "LightBlur";

        // --- Aspect ratio ---
        public float HorizontalSquash { get; set; } = 1.0f;
        public float SingleHorizontalSquash { get; set; } = 1.0f;

        // --- Grid alignment ---
        /// <summary>
        /// Vertical row offset for the 2×2 grid bottom panels.
        /// Positive = overlap at seam, negative = gap. Range: −30 to +30.
        /// </summary>
        public int GridVerticalOffset { get; set; } = 5;

        /// <summary>
        /// Horizontal column offset for the 2×2 grid right panels.
        /// Positive = overlap at seam, negative = gap. Range: −30 to +30.
        /// </summary>
        public int GridHorizontalOffset { get; set; } = 0;

        /// <summary>
        /// Uniform horizontal content shift for all four grid quadrants (in characters).
        /// Positive = image moves right on LCDs. Range: −100 to +100.
        /// </summary>
        public int GridContentShift { get; set; } = 0;

        /// <summary>
        /// Horizontal content shift for single LCD panels (in characters).
        /// Independent of GridContentShift. Range: −100 to +100.
        /// </summary>
        public int SingleContentShift { get; set; } = 0;

        // --- Font tint ---
        /// <summary>
        /// RGB tint for grayscale LCD font color. Format: "R,G,B" (0-255 each).
        /// Pushed to the server via CLIENTPREFS and applied immediately (no restart).
        /// </summary>
        public string LcdFontTint { get; set; } = "255,255,255";

        // --- Misc ---
        public bool VerboseLogging { get; set; } = false;

        /// <summary>
        /// The server-reported maximum capture FPS (not persisted — set at runtime after GETCONFIG).
        /// </summary>
        [XmlIgnore]
        public int ServerMaxFps { get; set; } = 30;

        /// <summary>
        /// The server-reported maximum display FPS (not persisted — set at runtime after GETCONFIG).
        /// </summary>
        [XmlIgnore]
        public int ServerMaxDisplayFps { get; set; } = 10;

        /// <summary>
        /// Effective capture FPS after clamping PreferredCaptureFps to the server maximum.
        /// </summary>
        [XmlIgnore]
        public int EffectiveFps => Math.Max(1, Math.Min(PreferredCaptureFps, ServerMaxFps));

        /// <summary>
        /// Effective display FPS after clamping PreferredDisplayFps to the server maximum.
        /// </summary>
        [XmlIgnore]
        public int EffectiveDisplayFps => Math.Max(1, Math.Min(PreferredDisplayFps, ServerMaxDisplayFps));

        private const string FileName = "CCTVCapture.settings.xml";

        /// <summary>
        /// Resolves the full path to the settings file (next to the executable).
        /// </summary>
        private static string GetFilePath()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, FileName);
        }

        /// <summary>
        /// Load settings from disk, returning defaults if the file is missing or corrupt.
        /// </summary>
        public static ClientSettings Load()
        {
            string path = GetFilePath();
            try
            {
                if (File.Exists(path))
                {
                    var ser = new XmlSerializer(typeof(ClientSettings));
                    using (var reader = new StreamReader(path))
                    {
                        return (ClientSettings)ser.Deserialize(reader);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to load settings from {path}: {ex.Message}");
            }
            return new ClientSettings();
        }

        /// <summary>
        /// Persist current settings to disk.
        /// </summary>
        public void Save()
        {
            string path = GetFilePath();
            try
            {
                var ser = new XmlSerializer(typeof(ClientSettings));
                using (var writer = new StreamWriter(path))
                {
                    ser.Serialize(writer, this);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to save settings to {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Clamp bounded fields (e.g. FPS) to safe ranges.
        /// </summary>
        public void Validate()
        {
            Port = Math.Max(1024, Math.Min(65535, Port));
            PreferredCaptureFps = Math.Max(1, Math.Min(30, PreferredCaptureFps));
            PreferredDisplayFps = Math.Max(1, Math.Min(10, PreferredDisplayFps));
            HorizontalSquash = Math.Max(0.5f, Math.Min(1.5f, HorizontalSquash));
            SingleHorizontalSquash = Math.Max(0.5f, Math.Min(1.5f, SingleHorizontalSquash));
            GridVerticalOffset = Math.Max(-30, Math.Min(30, GridVerticalOffset));
            GridHorizontalOffset = Math.Max(-30, Math.Min(30, GridHorizontalOffset));
            GridContentShift = Math.Max(-100, Math.Min(100, GridContentShift));
            SingleContentShift = Math.Max(-100, Math.Min(100, SingleContentShift));

            if (string.IsNullOrWhiteSpace(Host))
                Host = "localhost";
            if (string.IsNullOrWhiteSpace(DitherMode))
                DitherMode = "None";
            if (string.IsNullOrWhiteSpace(PostProcessMode))
                PostProcessMode = "None";
            if (string.IsNullOrWhiteSpace(GridPostProcessMode))
                GridPostProcessMode = "LightBlur";
            if (string.IsNullOrWhiteSpace(LcdFontTint))
                LcdFontTint = "255,255,255";
        }
    }
}
