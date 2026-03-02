using System;
using System.Xml.Serialization;
using NLog;

namespace CCTVPlugin
{
    /// <summary>
    /// Configuration for a single CCTVCapture instance.
    /// Supports multiple CCTVCapture instances connecting to the same plugin.
    /// Each instance manages cameras based on faction membership or prefix.
    /// </summary>
    public class CCTVClientInstanceConfig
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

        [XmlElement("Name")]
        public string Name { get; set; } = "Default";

        [XmlElement("TcpPort")]
        public int TcpPort { get; set; } = 12345;

        [XmlElement("SpectatorSteamId")]
        public ulong SpectatorSteamId { get; set; } = 0;

        [XmlElement("CameraPrefix")]
        public string CameraPrefix { get; set; } = "LCD_TVCamera";

        /// <summary>
        /// Optional: Filter cameras by suffix (e.g., "Test01", "Test02").
        /// If set, only cameras with matching suffix will be handled.
        /// If empty, all cameras with CameraPrefix will be handled.
        /// Example: CameraPrefix="LCD_TVCamera", CameraSuffix="Test01" → only handles "LCD_TVCamera Test01"
        /// </summary>
        [XmlElement("CameraSuffix")]
        public string CameraSuffix { get; set; } = "";

        [XmlElement("LcdPrefix")]
        public string LcdPrefix { get; set; } = "LCD_TV";

        /// <summary>
        /// Faction tag to match cameras by faction membership.
        /// If set, cameras on grids owned by faction members will be handled by this instance.
        /// Takes priority over prefix matching.
        /// </summary>
        [XmlElement("FactionTag")]
        public string FactionTag { get; set; } = "";

        /// <summary>
        /// Name of the LCD (or LCD grid) that displays the live feed for this instance.
        /// This is the SINGLE display location where cycling cameras are shown.
        /// Examples: "LCD_FactionA_LiveFeed" or "LCD_Public_Feed"
        /// For 2x2 grids, append _TL, _TR, _BL, _BR to this base name.
        /// </summary>
        [XmlElement("LiveFeedLcdName")]
        public string LiveFeedLcdName { get; set; } = "";

        /// <summary>
        /// Alpha channel (0–255) for the LCD background colour.
        /// 255 = fully opaque black background (default, normal wall-mounted screen).
        ///   0 = fully transparent background — only the rendered character pixels are
        ///       visible, letting the occupant see through to the world behind the panel.
        ///       Use this for a vehicle HUD: place a Transparent LCD in front of the
        ///       cockpit, set LcdFontTint to a green tint (e.g. "0,200,80") and
        ///       UseColorMode=false for a night-vision overlay effect.
        /// </summary>
        [XmlElement("LcdBackgroundAlpha")]
        public int LcdBackgroundAlpha { get; set; } = 255;

        [XmlElement("Enabled")]
        public bool Enabled { get; set; } = true;

        [XmlElement("Description")]
        public string Description { get; set; } = "Public cameras";

        /// <summary>
        /// Determines if this instance should handle a camera based on faction or name.
        /// Priority: 1) Faction membership, 2) Camera suffix match, 3) All cameras if no filters
        /// 
        /// NOTE: In multi-client mode, cameraDisplayName is the SUFFIX (e.g., "Test01")
        /// not the full name (e.g., "LCD_TVCamera Test01").
        /// </summary>
        public bool ShouldHandleCamera(string cameraDisplayName, string cameraFactionTag = null)
        {
            if (string.IsNullOrEmpty(cameraDisplayName))
                return false;

            // Priority 1: Check faction tag match (strict filtering)
            if (!string.IsNullOrEmpty(FactionTag))
            {
                // If we have a faction filter, ONLY accept cameras from that faction
                if (!string.IsNullOrEmpty(cameraFactionTag))
                {
                    bool factionMatch = FactionTag.Equals(cameraFactionTag, StringComparison.OrdinalIgnoreCase);

                    // If faction matches, still check suffix if specified
                    if (factionMatch && !string.IsNullOrEmpty(CameraSuffix))
                    {
                        return cameraDisplayName.Equals(CameraSuffix, StringComparison.OrdinalIgnoreCase);
                    }

                    return factionMatch;
                }
                else
                {
                    return false; // Faction filter set but camera has no faction
                }
            }

            // Priority 2: Check camera suffix match (if specified)
            if (!string.IsNullOrEmpty(CameraSuffix))
            {
                return cameraDisplayName.Equals(CameraSuffix, StringComparison.OrdinalIgnoreCase);
            }

            // Priority 3: No filters - accept ALL cameras with the prefix
            // (CameraPrefix matching already happened in RescanCameras, so all cameras here are valid)
            return true;
        }

        /// <summary>
        /// Gets the base name for the live feed LCD (without quadrant suffixes).
        /// </summary>
        public string GetLiveFeedLcdBaseName()
        {
            if (!string.IsNullOrEmpty(LiveFeedLcdName))
                return LiveFeedLcdName;

            // Fallback to default based on name
            return $"LCD_{Name}_LiveFeed";
        }
    }
}
