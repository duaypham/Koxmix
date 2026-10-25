using System.Drawing;
using System.Windows.Forms;

namespace Koxmix.Forms
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            tableLayoutPanel1 = new TableLayoutPanel();
            panel1 = new Panel();
            btnBrowser = new Button();
            label2 = new Label();
            radAudio = new RadioButton();
            radVideo = new RadioButton();
            label1 = new Label();
            btnClearList = new Button();
            btnSettings = new Button();
            btnDownload = new Button();
            txtUrl = new TextBox();
            statusStrip1 = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            tableLayoutPanel1.SuspendLayout();
            panel1.SuspendLayout();
            statusStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20F));
            tableLayoutPanel1.Controls.Add(panel1, 0, 0);
            tableLayoutPanel1.Controls.Add(statusStrip1, 0, 2);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 3;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 87F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            tableLayoutPanel1.Size = new Size(707, 489);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // panel1
            // 
            panel1.Controls.Add(btnBrowser);
            panel1.Controls.Add(label2);
            panel1.Controls.Add(radAudio);
            panel1.Controls.Add(radVideo);
            panel1.Controls.Add(label1);
            panel1.Controls.Add(btnClearList);
            panel1.Controls.Add(btnSettings);
            panel1.Controls.Add(btnDownload);
            panel1.Controls.Add(txtUrl);
            panel1.Dock = DockStyle.Fill;
            panel1.Location = new Point(3, 3);
            panel1.Name = "panel1";
            panel1.Size = new Size(701, 81);
            panel1.TabIndex = 0;
            // 
            // btnBrowser
            // 
            btnBrowser.Location = new Point(584, 8);
            btnBrowser.Name = "btnBrowser";
            btnBrowser.Size = new Size(94, 29);
            btnBrowser.TabIndex = 9;
            btnBrowser.Text = "Cookies";
            btnBrowser.UseVisualStyleBackColor = true;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(42, 51);
            label2.Name = "label2";
            label2.Size = new Size(80, 20);
            label2.TabIndex = 8;
            label2.Text = "For videos:";
            // 
            // radAudio
            // 
            radAudio.AutoSize = true;
            radAudio.Location = new Point(319, 49);
            radAudio.Name = "radAudio";
            radAudio.Size = new Size(132, 24);
            radAudio.TabIndex = 7;
            radAudio.Text = "Download MP3";
            radAudio.UseVisualStyleBackColor = true;
            // 
            // radVideo
            // 
            radVideo.AutoSize = true;
            radVideo.Checked = true;
            radVideo.Location = new Point(147, 49);
            radVideo.Name = "radVideo";
            radVideo.Size = new Size(148, 24);
            radVideo.TabIndex = 6;
            radVideo.TabStop = true;
            radVideo.Text = "Download Videos";
            radVideo.UseVisualStyleBackColor = true;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(16, 12);
            label1.Name = "label1";
            label1.Size = new Size(106, 20);
            label1.TabIndex = 4;
            label1.Text = "Paste link here:";
            // 
            // btnClearList
            // 
            btnClearList.Location = new Point(472, 49);
            btnClearList.Name = "btnClearList";
            btnClearList.Size = new Size(94, 29);
            btnClearList.TabIndex = 3;
            btnClearList.Text = "Clear List";
            btnClearList.UseVisualStyleBackColor = true;
            btnClearList.Click += btnClearList_Click;
            // 
            // btnSettings
            // 
            btnSettings.Location = new Point(584, 47);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(94, 29);
            btnSettings.TabIndex = 2;
            btnSettings.Text = "Settings";
            btnSettings.UseVisualStyleBackColor = true;
            btnSettings.Click += btnSettings_Click;
            // 
            // btnDownload
            // 
            btnDownload.Location = new Point(472, 7);
            btnDownload.Name = "btnDownload";
            btnDownload.Size = new Size(94, 29);
            btnDownload.TabIndex = 1;
            btnDownload.Text = "Download";
            btnDownload.UseVisualStyleBackColor = true;
            btnDownload.Click += btnDownload_Click;
            // 
            // txtUrl
            // 
            txtUrl.Location = new Point(128, 9);
            txtUrl.Name = "txtUrl";
            txtUrl.Size = new Size(323, 27);
            txtUrl.TabIndex = 0;
            // 
            // statusStrip1
            // 
            statusStrip1.ImageScalingSize = new Size(20, 20);
            statusStrip1.Items.AddRange(new ToolStripItem[] { lblStatus });
            statusStrip1.Location = new Point(0, 463);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(707, 26);
            statusStrip1.TabIndex = 2;
            statusStrip1.Text = "statusStrip1";
            // 
            // lblStatus
            // 
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(142, 20);
            lblStatus.Text = "Ready to download.";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = SystemColors.ControlLight;
            ClientSize = new Size(707, 489);
            Controls.Add(tableLayoutPanel1);
            Name = "MainForm";
            Text = "Koxmix";
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel1.PerformLayout();
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private TableLayoutPanel tableLayoutPanel1;
        private Panel panel1;
        private Button btnDownload;
        private TextBox txtUrl;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel lblStatus;
        private Button btnSettings;
        private Label label1;
        private Button btnClearList;
        private RadioButton radAudio;
        private RadioButton radVideo;
        private Label label2;
        private Button btnBrowser;
    }
}