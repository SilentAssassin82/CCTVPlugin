using System.Windows;
using System.Windows.Controls;

namespace CCTVPlugin
{
    public partial class CCTVPluginControl : UserControl
    {
        private readonly Plugin _plugin;

        public CCTVPluginControl(Plugin plugin)
        {
            _plugin = plugin;
            DataContext = plugin.Config;
            InitializeComponent();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            _plugin.SaveConfig();
            SaveStatus.Text = "Settings saved. Restart Torch for changes to take effect.";
        }
    }
}
