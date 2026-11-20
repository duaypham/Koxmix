using System;
using System.Drawing;
using System.Windows.Forms;
using Koxmix.Core;

namespace Koxmix.Forms
{
    public partial class SettingsForm : Form
    {
        private string[] _paths = new string[8];

        public SettingsForm()
        {
            InitializeComponent();
            ApplyDarkTheme();
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            _paths = PathManager.GetAllCategoryPaths();

            cboCategory.Items.Clear();
            cboCategory.Items.AddRange(PathManager.CategoryNames);
            cboCategory.SelectedIndex = 0;
        }

        private void cboCategory_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cboCategory.SelectedIndex >= 0 && cboCategory.SelectedIndex < _paths.Length)
                txtPath.Text = _paths[cboCategory.SelectedIndex];
        }

        private void txtPath_TextChanged(object sender, EventArgs e)
        {
            if (cboCategory.SelectedIndex >= 0 && cboCategory.SelectedIndex < _paths.Length)
                _paths[cboCategory.SelectedIndex] = txtPath.Text;
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog { Description = "Select save location for " + cboCategory.SelectedItem };
            if (fbd.ShowDialog() == DialogResult.OK) txtPath.Text = fbd.SelectedPath;
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            PathManager.SaveAllCategoryPaths(_paths);
            this.Close();
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = Color.FromArgb(32, 32, 32);
            this.ForeColor = Color.FromArgb(230, 230, 230);
            ApplyThemeRecursive(this);
        }

        private void ApplyThemeRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Label lbl) { lbl.ForeColor = Color.FromArgb(220, 220, 220); lbl.BackColor = Color.Transparent; }
                else if (c is TextBox tb) { tb.BackColor = Color.FromArgb(45, 45, 45); tb.ForeColor = Color.White; tb.BorderStyle = BorderStyle.FixedSingle; }
                else if (c is ComboBox cb) { cb.BackColor = Color.FromArgb(45, 45, 45); cb.ForeColor = Color.White; cb.FlatStyle = FlatStyle.Flat; }
                else if (c is Button btn) { btn.BackColor = Color.FromArgb(50, 50, 50); btn.ForeColor = Color.White; btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderSize = 0; btn.Cursor = Cursors.Hand; }
                if (c.HasChildren) ApplyThemeRecursive(c);
            }
        }
    }
}