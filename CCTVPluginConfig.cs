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
        private int _captureFps = 2;
        private int _displayFps = 2; // FPS for displaying frames on LCDs (can be lower than capture FPS)
        private bool _useColorMode = true;
        private bool _useDithering = false;
        private float _fontScale = 1.0f;
        private bool _autoAdjustFontSize = true;
        private string _postProcessMode = "None";
        private bool _enableVerboseFrameLogging = false;
        private float _gridFontSize = 0.1f;
        private float _proximityCheckRadius = 150f;
        private int _lcdGridResolution = 362;

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
            }
        }

        [XmlElement("CaptureFps")]
        public int CaptureFps
        {
            get => _captureFps;
            set
            {
                _captureFps = value;
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
                OnPropertyChanged();
            }
        }

        [XmlElement("UseDithering")]
        public bool UseDithering
        {
            get => _useDithering;
            set
            {
                _useDithering = value;
                OnPropertyChanged();
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

        [XmlElement("GridFontSize")]
        public float GridFontSize
        {
            get => _gridFontSize;
            set
            {
                _gridFontSize = Math.Max(0.05f, Math.Min(0.2f, value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Output render resolution for the 2×2 LCD grid (width and height in characters).
        /// Must be an even number between 64 and 362. The single-LCD resolution is always half of this value.
        /// </summary>
        [XmlElement("LcdGridResolution")]
        public int LcdGridResolution
        {
            get => _lcdGridResolution;
            set
            {
                // Clamp to even number in [64, 362]
                int clamped = Math.Max(64, Math.Min(362, value));
                _lcdGridResolution = (clamped % 2 != 0) ? clamped - 1 : clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LcdSingleResolution));
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
                        Log.Info($"Loaded config from {path}");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to load config from {path}, using defaults");
            }

            return new CCTVPluginConfig();
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
