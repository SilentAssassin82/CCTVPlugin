using System.Windows.Forms;
using CCTVCommon;

namespace CCTVCapture
{
    /// <summary>
    /// Isolates WinForms dependencies so Program.cs can be compiled
    /// by the Torch plugin project (which lacks System.Windows.Forms).
    /// </summary>
    internal static class ConfigFormLauncher
    {
        /// <summary>
        /// Shows the settings dialog and returns true if the user clicked "Save &amp; Start".
        /// </summary>
        internal static bool ShowAndApply(ClientSettings settings)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var form = new ConfigForm(settings))
            {
                return form.ShowDialog() == DialogResult.OK;
            }
        }
    }
}
