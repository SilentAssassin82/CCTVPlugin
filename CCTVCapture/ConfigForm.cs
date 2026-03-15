using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CCTVCommon;

namespace CCTVCapture
{
    /// <summary>
    /// Settings form shown before the capture loop starts.
    /// Saves user preferences to a local XML file.
    /// </summary>
    public class ConfigForm : Form
    {
        private readonly ClientSettings _settings;

        // Connection
        private TextBox _txtHost;
        private NumericUpDown _nudPort;

        // Visual mode
        private CheckBox _chkColorMode;
        private CheckBox _chkDesaturate;
        private CheckBox _chkNightVision;
        private CheckBox _chkCropSquare;

        // Quality
        private NumericUpDown _nudCaptureFps;
        private NumericUpDown _nudDisplayFps;
        private ComboBox _cboDither;
        private ComboBox _cboPostProcess;
        private ComboBox _cboGridPostProcess;

        // Aspect ratio
        private NumericUpDown _nudGridSquash;
        private NumericUpDown _nudSingleSquash;

        // Grid alignment
        private NumericUpDown _nudGridVertOffset;
        private NumericUpDown _nudGridHorizOffset;
        private NumericUpDown _nudGridContentShift;
        private NumericUpDown _nudSingleContentShift;

        // Font tint
        private NumericUpDown _nudTintR;
        private NumericUpDown _nudTintG;
        private NumericUpDown _nudTintB;
        private Panel _pnlTintPreview;

        // Misc
        private CheckBox _chkVerbose;

        public ConfigForm(ClientSettings settings)
        {
            _settings = settings ?? new ClientSettings();
            InitializeComponents();
            LoadSettingsToUI();
        }

        private void InitializeComponents()
        {
            Text = "CCTVCapture — Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(420, 740);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);

            int y = 12;
            int labelX = 16;
            int controlX = 170;
            int controlW = 210;
            int rowH = 30;

            // ═══════════════ Connection ═══════════════
            AddSectionLabel("Connection", ref y);

            AddLabel("Host:", labelX, y + 3);
            _txtHost = new TextBox
            {
                Location = new Point(controlX, y),
                Width = controlW,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_txtHost);
            y += rowH;

            AddLabel("Port:", labelX, y + 3);
            _nudPort = CreateNumericUpDown(controlX, y, 1024, 65535, 12345);
            y += rowH + 8;

            // ═══════════════ Visual Mode ═══════════════
            AddSectionLabel("Visual Mode", ref y);

            _chkColorMode = AddCheckBox("Color mode (SE color chars)", labelX, ref y);
            _chkDesaturate = AddCheckBox("Desaturate (B&&W via color chars)", labelX, ref y);
            _chkNightVision = AddCheckBox("Night vision (green tint)", labelX, ref y);
            _chkCropSquare = AddCheckBox("Crop capture to square", labelX, ref y);
            y += 4;

            // ═══════════════ Quality ═══════════════
            AddSectionLabel("Quality", ref y);

            AddLabel("Capture FPS:", labelX, y + 3);
            _nudCaptureFps = CreateNumericUpDown(controlX, y, 1, 30, 4);
            y += rowH;

            AddLabel("Display FPS:", labelX, y + 3);
            _nudDisplayFps = CreateNumericUpDown(controlX, y, 1, 10, 2);
            y += rowH;

            AddLabel("Dither mode:", labelX, y + 3);
            _cboDither = CreateComboBox(controlX, y, controlW, new[] { "None", "Bayer", "FloydSteinberg" });
            y += rowH;

            AddLabel("Post-process:", labelX, y + 3);
            _cboPostProcess = CreateComboBox(controlX, y, controlW, new[] { "None", "LightBlur", "MediumBlur", "Sharpen" });
            y += rowH;

            AddLabel("Grid post-process:", labelX, y + 3);
            _cboGridPostProcess = CreateComboBox(controlX, y, controlW, new[] { "None", "LightBlur", "MediumBlur", "Sharpen" });
            y += rowH + 8;

            // ═══════════════ Aspect Ratio ═══════════════
            AddSectionLabel("Aspect Ratio", ref y);

            AddLabel("Grid squash:", labelX, y + 3);
            _nudGridSquash = CreateDecimalUpDown(controlX, y, 0.50m, 1.50m, 1.00m, 0.05m);
            y += rowH;

            AddLabel("Single squash:", labelX, y + 3);
            _nudSingleSquash = CreateDecimalUpDown(controlX, y, 0.50m, 1.50m, 1.00m, 0.05m);
            y += rowH + 8;

            // ═══════════════ Grid Alignment ═══════════════
            AddSectionLabel("Grid Alignment", ref y);

            AddLabel("Vertical offset:", labelX, y + 3);
            _nudGridVertOffset = CreateNumericUpDown(controlX, y, -30, 30, 5);
            y += rowH;

            AddLabel("Horizontal offset:", labelX, y + 3);
            _nudGridHorizOffset = CreateNumericUpDown(controlX, y, -30, 30, 0);
            y += rowH;

            AddLabel("Grid content shift:", labelX, y + 3);
            _nudGridContentShift = CreateNumericUpDown(controlX, y, -100, 100, 0);
            y += rowH;

            AddLabel("Single content shift:", labelX, y + 3);
            _nudSingleContentShift = CreateNumericUpDown(controlX, y, -100, 100, 0);
            y += rowH + 8;

            // ═══════════════ Font Tint (Grayscale) ═══════════════
            AddSectionLabel("Font Tint (Grayscale)", ref y);

            AddLabel("R:", labelX, y + 3);
            _nudTintR = CreateNumericUpDown(labelX + 24, y, 0, 255, 255);
            _nudTintR.Width = 60;
            AddLabel("G:", labelX + 96, y + 3);
            _nudTintG = CreateNumericUpDown(labelX + 116, y, 0, 255, 255);
            _nudTintG.Width = 60;
            AddLabel("B:", labelX + 188, y + 3);
            _nudTintB = CreateNumericUpDown(labelX + 208, y, 0, 255, 255);
            _nudTintB.Width = 60;

            _pnlTintPreview = new Panel
            {
                Location = new Point(labelX + 280, y),
                Size = new Size(90, 22),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White
            };
            Controls.Add(_pnlTintPreview);

            _nudTintR.ValueChanged += (s, e) => UpdateTintPreview();
            _nudTintG.ValueChanged += (s, e) => UpdateTintPreview();
            _nudTintB.ValueChanged += (s, e) => UpdateTintPreview();
            y += rowH + 8;

            // ═══════════════ Misc ═══════════════
            _chkVerbose = AddCheckBox("Verbose logging", labelX, ref y);
            y += 8;

            // ═══════════════ Buttons ═══════════════
            var btnStart = new Button
            {
                Text = "Save && Start",
                Location = new Point(120, y),
                Size = new Size(110, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.Click += (s, e) => { SaveUIToSettings(); };
            Controls.Add(btnStart);

            var btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(240, y),
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(63, 63, 70),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            AcceptButton = btnStart;
            CancelButton = btnCancel;

            y += 50;
            ClientSize = new Size(ClientSize.Width, y);
        }

        // ─── Helpers ───

        private Label AddLabel(string text, int x, int y)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };
            Controls.Add(lbl);
            return lbl;
        }

        private void AddSectionLabel(string text, ref int y)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(14, y),
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 151, 230)
            };
            Controls.Add(lbl);
            y += 22;
        }

        private CheckBox AddCheckBox(string text, int x, ref int y)
        {
            var chk = new CheckBox
            {
                Text = text,
                Location = new Point(x + 4, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            Controls.Add(chk);
            y += 24;
            return chk;
        }

        private NumericUpDown CreateNumericUpDown(int x, int y, int min, int max, int val)
        {
            var nud = new NumericUpDown
            {
                Location = new Point(x, y),
                Width = 90,
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            Controls.Add(nud);
            return nud;
        }

        private NumericUpDown CreateDecimalUpDown(int x, int y, decimal min, decimal max, decimal val, decimal inc)
        {
            var nud = new NumericUpDown
            {
                Location = new Point(x, y),
                Width = 90,
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                DecimalPlaces = 2,
                Increment = inc,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            Controls.Add(nud);
            return nud;
        }

        private ComboBox CreateComboBox(int x, int y, int w, string[] items)
        {
            var cbo = new ComboBox
            {
                Location = new Point(x, y),
                Width = w,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
                FlatStyle = FlatStyle.Flat
            };
            cbo.Items.AddRange(items);
            if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
            Controls.Add(cbo);
            return cbo;
        }

        // ─── Data binding ───

        private void LoadSettingsToUI()
        {
            _txtHost.Text = _settings.Host;
            _nudPort.Value = Math.Max(_nudPort.Minimum, Math.Min(_nudPort.Maximum, _settings.Port));
            _chkColorMode.Checked = _settings.UseColorMode;
            _chkDesaturate.Checked = _settings.DesaturateColorMode;
            _chkNightVision.Checked = _settings.NightVisionMode;
            _chkCropSquare.Checked = _settings.CropCaptureToSquare;
            _nudCaptureFps.Value = Math.Max(_nudCaptureFps.Minimum, Math.Min(_nudCaptureFps.Maximum, _settings.PreferredCaptureFps));
            _nudDisplayFps.Value = Math.Max(_nudDisplayFps.Minimum, Math.Min(_nudDisplayFps.Maximum, _settings.PreferredDisplayFps));

            SelectComboItem(_cboDither, _settings.DitherMode);
            SelectComboItem(_cboPostProcess, _settings.PostProcessMode);
            SelectComboItem(_cboGridPostProcess, _settings.GridPostProcessMode);

            _nudGridSquash.Value = ClampDecimal((decimal)_settings.HorizontalSquash, _nudGridSquash.Minimum, _nudGridSquash.Maximum);
            _nudSingleSquash.Value = ClampDecimal((decimal)_settings.SingleHorizontalSquash, _nudSingleSquash.Minimum, _nudSingleSquash.Maximum);

            _nudGridVertOffset.Value = Math.Max(_nudGridVertOffset.Minimum, Math.Min(_nudGridVertOffset.Maximum, _settings.GridVerticalOffset));
            _nudGridHorizOffset.Value = Math.Max(_nudGridHorizOffset.Minimum, Math.Min(_nudGridHorizOffset.Maximum, _settings.GridHorizontalOffset));
            _nudGridContentShift.Value = Math.Max(_nudGridContentShift.Minimum, Math.Min(_nudGridContentShift.Maximum, _settings.GridContentShift));
            _nudSingleContentShift.Value = Math.Max(_nudSingleContentShift.Minimum, Math.Min(_nudSingleContentShift.Maximum, _settings.SingleContentShift));

            // Font tint R,G,B
            ParseTint(_settings.LcdFontTint, out int tr, out int tg, out int tb);
            _nudTintR.Value = tr;
            _nudTintG.Value = tg;
            _nudTintB.Value = tb;
            UpdateTintPreview();

            _chkVerbose.Checked = _settings.VerboseLogging;
        }

        private void SaveUIToSettings()
        {
            _settings.Host = _txtHost.Text.Trim();
            _settings.Port = (int)_nudPort.Value;
            _settings.UseColorMode = _chkColorMode.Checked;
            _settings.DesaturateColorMode = _chkDesaturate.Checked;
            _settings.NightVisionMode = _chkNightVision.Checked;
            _settings.CropCaptureToSquare = _chkCropSquare.Checked;
            _settings.PreferredCaptureFps = (int)_nudCaptureFps.Value;
            _settings.PreferredDisplayFps = (int)_nudDisplayFps.Value;
            _settings.DitherMode = _cboDither.SelectedItem?.ToString() ?? "None";
            _settings.PostProcessMode = _cboPostProcess.SelectedItem?.ToString() ?? "None";
            _settings.GridPostProcessMode = _cboGridPostProcess.SelectedItem?.ToString() ?? "LightBlur";
            _settings.HorizontalSquash = (float)_nudGridSquash.Value;
            _settings.SingleHorizontalSquash = (float)_nudSingleSquash.Value;
            _settings.GridVerticalOffset = (int)_nudGridVertOffset.Value;
            _settings.GridHorizontalOffset = (int)_nudGridHorizOffset.Value;
            _settings.GridContentShift = (int)_nudGridContentShift.Value;
            _settings.SingleContentShift = (int)_nudSingleContentShift.Value;
            _settings.LcdFontTint = $"{(int)_nudTintR.Value},{(int)_nudTintG.Value},{(int)_nudTintB.Value}";
            _settings.VerboseLogging = _chkVerbose.Checked;

            _settings.Validate();
            _settings.Save();
        }

        private static void SelectComboItem(ComboBox cbo, string value)
        {
            for (int i = 0; i < cbo.Items.Count; i++)
            {
                if (string.Equals(cbo.Items[i].ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    cbo.SelectedIndex = i;
                    return;
                }
            }
            if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
        }

        private static decimal ClampDecimal(decimal v, decimal min, decimal max)
        {
            return Math.Max(min, Math.Min(max, v));
        }

        private static void ParseTint(string tint, out int r, out int g, out int b)
        {
            r = 255; g = 255; b = 255;
            if (string.IsNullOrWhiteSpace(tint)) return;
            string[] parts = tint.Split(',');
            if (parts.Length >= 3)
            {
                int.TryParse(parts[0].Trim(), out r);
                int.TryParse(parts[1].Trim(), out g);
                int.TryParse(parts[2].Trim(), out b);
                r = Math.Max(0, Math.Min(255, r));
                g = Math.Max(0, Math.Min(255, g));
                b = Math.Max(0, Math.Min(255, b));
            }
        }

        private void UpdateTintPreview()
        {
            int r = (int)_nudTintR.Value;
            int g = (int)_nudTintG.Value;
            int b = (int)_nudTintB.Value;
            _pnlTintPreview.BackColor = Color.FromArgb(r, g, b);
        }
    }
}
