using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using NLog;
using System.IO;
using System.Windows.Controls;

namespace CCTVPlugin
{
    public class Plugin : TorchPluginBase, IWpfPlugin
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        private CCTVPlugin _controller;
        private CCTVPluginConfig _config;
        private string _configPath;

        public CCTVPluginConfig Config => _config;

        public UserControl GetControl() => new CCTVPluginControl(this);

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);

            Log.Info("CCTVPlugin: Plugin.Init called");

            // Load or create config
            _configPath = Path.Combine(StoragePath, "CCTVPlugin.cfg");
            _config = CCTVPluginConfig.Load(_configPath);
            if (!File.Exists(_configPath))
                _config.Save(_configPath); // Create default config if none exists

            Log.Info($"Using TCP port: {_config.TcpPort}");
            Log.Info($"Camera rescan interval: {_config.CameraRescanTicks} ticks");
            Log.Info($"Heartbeat: {(_config.EnableHeartbeat ? "enabled" : "disabled")}");
            Log.Info($"Auto camera cycling: {(_config.EnableAutoCameraCycling ? "enabled" : "disabled")}");
            Log.Info($"Spectator SteamID: {(_config.SpectatorSteamId == 0 ? "NOT SET - TELEPORT DISABLED!" : _config.SpectatorSteamId.ToString())}");
            Log.Info($"Camera Prefix: {_config.CameraPrefix}");
            Log.Info($"LCD Prefix: {_config.LcdPrefix}");
            Log.Info($"LCD Font Tint: {_config.LcdFontTint} (R,G,B)");
            Log.Info($"Capture: {_config.CaptureWidth}x{_config.CaptureHeight} @ {_config.CaptureFps} FPS, Color: {_config.UseColorMode}");
            Log.Info($"Display FPS: {_config.DisplayFps} (capture:display ratio = {_config.CaptureFps}:{_config.DisplayFps})");
            if (_config.EnableAutoCameraCycling)
            {
                Log.Info($"Camera cycle interval: {_config.CameraCycleIntervalSeconds} seconds");
            }

            if (_config.SpectatorSteamId == 0)
            {
                Log.Warn("⚠️ SpectatorSteamId is not set in config! Auto-teleport will be DISABLED to prevent controlling real players!");
                Log.Warn("⚠️ Set SpectatorSteamId in CCTVPlugin.cfg to the SteamID of your fake client account.");
            }
            else
            {
                Log.Info("💡 TIP: ");
                Log.Info("💡 )");
            }

            _controller = new CCTVPlugin(
                _config.TcpPort, 
                _config.CameraRescanTicks, 
                _config.EnableHeartbeat,
                _config.EnableAutoCameraCycling,
                _config.CameraCycleIntervalSeconds,
                _config.SpectatorSteamId,
                _config.CameraPrefix,
                _config.LcdPrefix,
                _config.LcdFontTint,
                _config.CaptureWidth,
                _config.CaptureHeight,
                _config.CaptureFps,
                _config.UseColorMode,
                _config.UseDithering,
                _config.FontScale,
                _config.AutoAdjustFontSize,
                _config,
                torch);
            _controller.Start();
        }

        public override void Update()
        {
            base.Update();
            _controller?.Update();
        }

        public override void Dispose()
        {
            _controller?.Dispose();
            base.Dispose();
        }

        public void SaveConfig()
        {
            _config?.Save(_configPath);
            // Push updated config to connected fake client (if running)
            try
            {
                _controller?.SendConfigToClient();
            }
            catch { }
        }
    }
}
