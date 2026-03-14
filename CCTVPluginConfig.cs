using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using NLog;
using Torch;

namespace CCTVPlugin
{
    public class CCTVPluginConfig : ViewModel
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        // Legacy single-client settings (for backward compatibility)
        private int _tcpPort = 12345;
        private int _cameraRescanTicks = 1800; // 30 seconds (reduced overhead for multi-camera setups)
        private bool _enableHeartbeat = false;
        private bool _enableAutoCameraCycling = true;
        private int _cameraCycleIntervalSeconds = 10;
        private ulong _fakeClientSteamId = 0;
        private string _cameraPrefix = "LCD_TVCamera";
        private string _lcdPrefix = "LCD_TV";
        private string _lcdFontTint = "255,255,255";
        private int _captureWidth = 178;
        private int _captureHeight = 178;
        private int _captureFps = 10;
        private int _displayFps = 10; // FPS for displaying frames on LCDs (match capture FPS for maximum LCD refresh)
        private bool _useColorMode = true;
        private bool _useDithering = false;
        private string _ditherMode = "None";
        private float _fontScale = 1.0f;
        private bool _autoAdjustFontSize = true;
        private string _postProcessMode = "None";
        private string _gridPostProcessMode = "None";
        private bool _enableVerboseFrameLogging = false;
        private float _gridFontSize = 0.1f;
        private int _gridContentShift = 0;
        private int _gridVerticalOffset = 5;   // default overlap to close vertical seam
        private int _gridHorizontalOffset = 0;
        private float _singleLcdFontSize = 0.080f;
        private int _singleContentShift = 0;
        private float _proximityCheckRadius = 150f;
        private int _lcdGridResolution = 362;
        private bool _desaturateColorMode = false;
        private bool _nightVisionMode = false;
        private bool _cropCaptureToSquare = true;

        // Suppresses ValidateFpsRatio during XML deserialization so property
        // setters don't fire against stale default values before all elements
        // have been loaded.  Set to true by Load() after deserialization.
        private bool _loaded = false;

        // Multi-client support
        private List<CCTVClientInstanceConfig> _fakeClientInstances = new List<CCTVClientInstanceConfig>();
        private bool _useMultiClientMode = false;

        [XmlElement("TcpPort")]
        public int TcpPort
        {
            get => _tcpPort;
            set
            {
                if (value < 1024 || value > 65535)
                {
                    Log.Warn($"Invalid port {value}, must be between 1024-65535. Using default 12345.");
                    _tcpPort = 12345;
                }
                else
                {
                    _tcpPort = value;
                }
                OnPropertyChanged();
            }
        }

        [XmlElement("CameraRescanTicks")]
        public int CameraRescanTicks
        {
            get => _cameraRescanTicks;
            set
            {
                _cameraRescanTicks = Math.Max(60, value); // Min 1 second
                OnPropertyChanged();
            }
        }

        [XmlElement("EnableHeartbeat")]
        public bool EnableHeartbeat
        {
            get => _enableHeartbeat;
            set
            {
                _enableHeartbeat = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("EnableAutoCameraCycling")]
        public bool EnableAutoCameraCycling
        {
            get => _enableAutoCameraCycling;
            set
            {
                _enableAutoCameraCycling = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("CameraCycleIntervalSeconds")]
        public int CameraCycleIntervalSeconds
        {
            get => _cameraCycleIntervalSeconds;
            set
            {
                _cameraCycleIntervalSeconds = Math.Max(5, value); // Min 5 seconds
                OnPropertyChanged();
            }
        }

        [XmlElement("SpectatorSteamId")]
        public ulong SpectatorSteamId
        {
            get => _fakeClientSteamId;
            set
            {
                _fakeClientSteamId = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("CameraPrefix")]
        public string CameraPrefix
        {
            get => _cameraPrefix;
            set
            {
                _cameraPrefix = string.IsNullOrWhiteSpace(value) ? "LCD_TVCamera" : value;
                OnPropertyChanged();
            }
        }

        [XmlElement("LcdPrefix")]
        public string LcdPrefix
        {
            get => _lcdPrefix;
            set
            {
                _lcdPrefix = string.IsNullOrWhiteSpace(value) ? "LCD_TV" : value;
                OnPropertyChanged();
            }
        }

        [XmlElement("LcdFontTint")]
        public string LcdFontTint
        {
            get => _lcdFontTint;
            set
            {
                _lcdFontTint = string.IsNullOrWhiteSpace(value) ? "255,255,255" : value;
                OnPropertyChanged();
            }
        }

        [XmlElement("CaptureWidth")]
        public int CaptureWidth
        {
            get => _captureWidth;
            set
            {
                _captureWidth = value;
                OnPropertyChanged();
                // Keep grid resolution in sync
                if (_lcdGridResolution != value)
                    LcdGridResolution = value;
            }
        }

        [XmlElement("CaptureHeight")]
        public int CaptureHeight
        {
            get => _captureHeight;
            set
            {
                _captureHeight = value;
                OnPropertyChanged();
                // Keep grid resolution in sync
                if (_lcdGridResolution != value)
                    LcdGridResolution = value;
            }
        }

        [XmlElement("CaptureFps")]
        public int CaptureFps
        {
            get => _captureFps;
            set
            {
                _captureFps = Math.Max(1, value);
                ValidateFpsRatio();
                OnPropertyChanged();
            }
        }

        [XmlElement("DisplayFps")]
        public int DisplayFps
        {
            get => _displayFps;
            set
            {
                _displayFps = Math.Max(1, Math.Min(value, 10)); // Clamp between 1-10 FPS
                ValidateFpsRatio();
                OnPropertyChanged();
            }
        }

        [XmlElement("UseColorMode")]
        public bool UseColorMode
        {
            get => _useColorMode;
            set
            {
                _useColorMode = value;
                // Setting GridFontSize (via property) auto-calculates LcdGridResolution
                // and syncs SingleLcdFontSize so both display types match.
                GridFontSize = value ? 0.1f : 0.075f;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When enabled with Color Mode, desaturates the captured image before encoding
        /// into SE color chars. Produces square-pixel grayscale (R=G=B) using the color
        /// char pipeline — no 1:2 aspect ratio issues, auto-fit resolution works correctly.
        /// The classic grayscale mode (Color Mode off) is kept for LCD font tint support.
        /// </summary>
        [XmlElement("DesaturateColorMode")]
        public bool DesaturateColorMode
        {
            get => _desaturateColorMode;
            set
            {
                _desaturateColorMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When enabled with Desaturate mode, maps grayscale luminance to a green
        /// night-vision phosphor gradient (black → green → white-green) instead of
        /// neutral gray. The tint is baked into pixel RGB before color char encoding
        /// because SE font tint is ignored for color characters.
        /// </summary>
        [XmlElement("NightVisionMode")]
        public bool NightVisionMode
        {
            get => _nightVisionMode;
            set
            {
                _nightVisionMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When enabled, the capture client crops the viewport to a center square before
        /// resizing. Produces correct 1:1 proportions on the LCD but loses the left/right
        /// edges of a 16:9 viewport. When disabled, the full viewport is stretched to fit
        /// — wider field of view but horizontally compressed.
        /// </summary>
        [XmlElement("CropCaptureToSquare")]
        public bool CropCaptureToSquare
        {
            get => _cropCaptureToSquare;
            set
            {
                _cropCaptureToSquare = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("UseDithering")]
        public bool UseDithering
        {
            get => _ditherMode != "None";
            set
            {
                // Legacy setter: map bool to DitherMode for backward compat
                if (value && _ditherMode == "None")
                    _ditherMode = "Bayer";
                else if (!value)
                    _ditherMode = "None";
                _useDithering = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DitherMode));
            }
        }

        [XmlElement("DitherMode")]
        public string DitherMode
        {
            get => _ditherMode;
            set
            {
                _ditherMode = string.IsNullOrWhiteSpace(value) ? "None" : value;
                _useDithering = _ditherMode != "None";
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseDithering));
            }
        }

        [XmlElement("FontScale")]
        public float FontScale
        {
            get => _fontScale;
            set
            {
                _fontScale = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("AutoAdjustFontSize")]
        public bool AutoAdjustFontSize
        {
            get => _autoAdjustFontSize;
            set
            {
                _autoAdjustFontSize = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("PostProcessMode")]
        public string PostProcessMode
        {
            get => _postProcessMode;
            set
            {
                _postProcessMode = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("GridPostProcessMode")]
        public string GridPostProcessMode
        {
            get => _gridPostProcessMode;
            set
            {
                _gridPostProcessMode = value;
                OnPropertyChanged();
            }
        }

        [XmlElement("EnableVerboseFrameLogging")]
        public bool EnableVerboseFrameLogging
        {
            get => _enableVerboseFrameLogging;
            set
            {
                _enableVerboseFrameLogging = value;
                OnPropertyChanged();
            }
        }

        // Reference baseline: at font 0.1 (color), 181 SE color chars fill one LCD panel.
        // Constant = 181 * 0.1 = 18.1.  Chars per panel at any font F = 18.1 / F.
        private const float FONT_RESOLUTION_CONSTANT = 18.1f;

        [XmlElement("GridFontSize")]
        public float GridFontSize
        {
            get => _gridFontSize;
            set
            {
                _gridFontSize = Math.Max(0.05f, Math.Min(0.2f, value));
                OnPropertyChanged();

                // Auto-calculate grid resolution so the content exactly fills each panel
                // at this font size.  No offsets needed — simple 4-way equal split.
                int charsPerPanel = (int)Math.Round(FONT_RESOLUTION_CONSTANT / _gridFontSize);
                int autoGrid = charsPerPanel * 2;
                // Ensure even
                if (autoGrid % 2 != 0) autoGrid--;
                LcdGridResolution = autoGrid;
            }
        }

        /// <summary>
        /// Horizontal content shift for the 2×2 grid panels (in characters).
        /// Moves the extraction window uniformly across all four quadrants.
        /// Positive values shift the image RIGHT on the LCDs. Negative values shift it LEFT.
        /// Range: −100 to +100 chars. Default: 0.
        /// </summary>
        [XmlElement("GridContentShift")]
        public int GridContentShift
        {
            get => _gridContentShift;
            set
            {
                _gridContentShift = Math.Max(-100, Math.Min(100, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Vertical row offset for the 2×2 grid bottom panels (BL/BR).
        /// Positive values shift the bottom panels' start UP (overlap with top panels),
        /// negative values shift them DOWN. Closes or widens the vertical seam.
        /// Range: −15 to +15 rows. Default: 0 (no offset).
        /// </summary>
        [XmlElement("GridVerticalOffset")]
        public int GridVerticalOffset
        {
            get => _gridVerticalOffset;
            set
            {
                _gridVerticalOffset = Math.Max(-30, Math.Min(30, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Horizontal column offset for the 2×2 grid right panels (TR/BR).
        /// Positive values shift the right panels' start LEFT (overlap with left panels),
        /// negative values shift them RIGHT. Closes or widens the horizontal seam.
        /// Range: −15 to +15 columns. Default: 0 (no offset).
        /// </summary>
        [XmlElement("GridHorizontalOffset")]
        public int GridHorizontalOffset
        {
            get => _gridHorizontalOffset;
            set
            {
                _gridHorizontalOffset = Math.Max(-30, Math.Min(30, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Font size base for single LCD panels. Independent of GridFontSize so single
        /// and grid displays can be tuned separately.
        /// Grayscale renders at SingleLcdFontSize×2; colour at SingleLcdFontSize×1.
        /// Auto-set by UseColorMode: 0.1 for colour, 0.080 for grayscale.
        /// </summary>
        [XmlElement("SingleLcdFontSize")]
        public float SingleLcdFontSize
        {
            get => _singleLcdFontSize;
            set
            {
                _singleLcdFontSize = Math.Max(0.05f, Math.Min(0.2f, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Horizontal content shift for single LCD panels (in characters).
        /// Independent of GridContentShift so each display type can be centred separately.
        /// Positive values shift the image RIGHT. Negative values shift it LEFT.
        /// Range: −100 to +100 chars. Default: 0.
        /// </summary>
        [XmlElement("SingleContentShift")]
        public int SingleContentShift
        {
            get => _singleContentShift;
            set
            {
                _singleContentShift = Math.Max(-100, Math.Min(100, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Output render resolution for the 2×2 LCD grid (width and height in characters).
        /// Must be an even number between 64 and 700. The single-LCD resolution is always half of this value.
        /// Auto-calculated from GridFontSize so the content exactly fills each panel.
        /// </summary>
        [XmlElement("LcdGridResolution")]
        public int LcdGridResolution
        {
            get => _lcdGridResolution;
            set
            {
                // Clamp to even number in [64, 700]
                int clamped = Math.Max(64, Math.Min(700, value));
                _lcdGridResolution = (clamped % 2 != 0) ? clamped - 1 : clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LcdSingleResolution));

                // Reverse-sync: recalculate GridFontSize so the content exactly fills
                // each panel at this resolution.  SingleLcdFontSize is kept in lockstep
                // so both display types use the same font and the content shift sliders
                // work consistently across grid and single panels.
                int charsPerPanel = _lcdGridResolution / 2;
                float autoFont = FONT_RESOLUTION_CONSTANT / charsPerPanel;
                autoFont = Math.Max(0.05f, Math.Min(0.2f, autoFont));
                if (Math.Abs(_gridFontSize - autoFont) > 0.001f)
                {
                    _gridFontSize = autoFont;
                    OnPropertyChanged(nameof(GridFontSize));
                }
                if (Math.Abs(_singleLcdFontSize - autoFont) > 0.001f)
                {
                    _singleLcdFontSize = autoFont;
                    OnPropertyChanged(nameof(SingleLcdFontSize));
                }

                // Keep capture resolution in sync — they must always match
                if (_captureWidth != _lcdGridResolution)
                {
                    _captureWidth = _lcdGridResolution;
                    OnPropertyChanged(nameof(CaptureWidth));
                }
                if (_captureHeight != _lcdGridResolution)
                {
                    _captureHeight = _lcdGridResolution;
                    OnPropertyChanged(nameof(CaptureHeight));
                }
            }
        }

        /// <summary>
        /// Output render resolution for a single LCD panel. Always half of LcdGridResolution.
        /// </summary>
        [XmlIgnore]
        public int LcdSingleResolution => _lcdGridResolution / 2;

        /// <summary>
        /// Radius (metres) within which at least one player must be present for LCD frames to be written.
        /// Set to 0 to disable the check and always write frames.
        /// </summary>
        [XmlElement("ProximityCheckRadius")]
        public float ProximityCheckRadius
        {
            get => _proximityCheckRadius;
            set
            {
                _proximityCheckRadius = Math.Max(0f, value);
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Warns and auto-corrects when DisplayFps exceeds CaptureFps.
        /// DisplayFps can now equal CaptureFps (1:1 ratio) for maximum LCD refresh.
        /// Skipped while _loaded is false (during XML deserialization) to avoid
        /// firing against stale default values before all elements are loaded.
        /// </summary>
        private void ValidateFpsRatio()
        {
            if (!_loaded) return;

            if (_displayFps > _captureFps)
            {
                Log.Warn($"⚠️ DisplayFps ({_displayFps}) exceeds CaptureFps ({_captureFps}). " +
                         $"Auto-correcting DisplayFps to {_captureFps}.");
                _displayFps = _captureFps;
            }
        }

        /// <summary>
        /// Enable multi-client mode.
        /// When true, uses ClientInstances list instead.
        /// </summary>
        [XmlElement("UseMultiClientMode")]
        public bool UseMultiClientMode
        {
            get => _useMultiClientMode;
            set
            {
                _useMultiClientMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// List of CCTVCapture instances. Each instance can handle a different set of cameras.
        /// Only used when UseMultiClientMode is true.
        /// </summary>
        [XmlArray("ClientInstances")]
        [XmlArrayItem("Instance")]
        public List<CCTVClientInstanceConfig> ClientInstances
        {
            get => _fakeClientInstances;
            set
            {
                _fakeClientInstances = value ?? new List<CCTVClientInstanceConfig>();
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets the appropriate client instances based on mode.
        /// Returns legacy single-client config if multi-client mode is disabled.
        /// </summary>
        public List<CCTVClientInstanceConfig> GetClientInstances()
        {
            if (_useMultiClientMode && _fakeClientInstances != null && _fakeClientInstances.Count > 0)
            {
                Log.Info($"GetClientInstances: multi-client mode, {_fakeClientInstances.Count} instance(s):");
                foreach (var inst in _fakeClientInstances)
                    Log.Info($"  - [{inst.Name}] Port={inst.TcpPort} SteamId={inst.SpectatorSteamId} Suffix='{inst.CameraSuffix}' Enabled={inst.Enabled}");
                return _fakeClientInstances;
            }

            Log.Warn($"GetClientInstances: falling back to legacy single-client mode (MultiClientMode={_useMultiClientMode}, InstanceCount={_fakeClientInstances?.Count ?? -1})");
            // Legacy mode: Create single instance from old settings
            return new List<CCTVClientInstanceConfig>
            {
                new CCTVClientInstanceConfig
                {
                    Name = "Default",
                    TcpPort = _tcpPort,
                    SpectatorSteamId = _fakeClientSteamId,
                    CameraPrefix = _cameraPrefix,
                    LcdPrefix = _lcdPrefix,
                    Enabled = true,
                    Description = "Legacy single-client mode"
                }
            };
        }

        public static CCTVPluginConfig Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(CCTVPluginConfig));
                    using (var reader = new StreamReader(path))
                    {
                        var config = (CCTVPluginConfig)serializer.Deserialize(reader);
                            config._loaded = true;
                            config.ValidateFpsRatio();
                            Log.Info($"Loaded config from {path}");
                            return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to load config from {path}, using defaults");
            }

            var defaults = new CCTVPluginConfig();
            defaults._loaded = true;
            return defaults;
        }

        public void Save(string path)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(CCTVPluginConfig));
                using (var writer = new StreamWriter(path))
                {
                    serializer.Serialize(writer, this);
                }
                Log.Info($"Saved config to {path}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to save config to {path}");
            }
        }
    }
}
