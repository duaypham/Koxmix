using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Koxmix.Engines;
using Koxmix.Utils;

namespace Koxmix.Controls
{
    [DesignerCategory("Code")]
    public class DownloadCardItem : UserControl
    {
        private PictureBox picThumbnail;
        private PictureBox picPlatformIcon;
        private Label lblTitle;
        private Label lblMeta;
        private Label lblStatus;

        private int _progressPercentage = 0;
        private bool _isSelected = false;
        private bool _isHovered = false;
        private string _placeholderText = "MEDIA";

        private static readonly Color ColorCardBg = Color.FromArgb(32, 32, 32);
        private static readonly Color ColorCardHover = Color.FromArgb(44, 44, 44);
        private static readonly Color ColorCardSelected = Color.FromArgb(52, 52, 52);
        private static readonly Color ColorAccent = Color.FromArgb(14, 116, 232);
        private static readonly Color ColorSuccess = Color.FromArgb(46, 204, 113);
        private static readonly Color ColorError = Color.FromArgb(231, 76, 60);

        // Bộ đệm Font tĩnh dùng chung cho toàn bộ thẻ (Chống rò rỉ GDI Handle)
        private static readonly Font FontPlaceholder = new Font("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font FontTitle = new Font("Segoe UI", 10.5f, FontStyle.Regular);
        private static readonly Font FontSub = new Font("Segoe UI", 9f, FontStyle.Regular);

        // Bộ đệm Icon nền tảng và HttpClient dùng chung
        private static readonly Dictionary<string, Image> _iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static readonly HttpClient _thumbClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                UpdateThemeColors();
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string TargetPath { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Url { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsMp3 { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get; private set; } = "Untitled";

        public DownloadCardItem()
        {
            InitializeComponentLayout();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        private void InitializeComponentLayout()
        {
            this.BackColor = ColorCardBg;
            this.Size = new Size(650, 84);
            this.Height = 84;
            this.Margin = new Padding(0, 0, 0, 4);
            this.Cursor = Cursors.Hand;

            picThumbnail = new PictureBox
            {
                Location = new Point(12, 12),
                Size = new Size(106, 60),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            picThumbnail.Paint += (s, e) =>
            {
                if (picThumbnail.Image == null)
                {
                    using var b = new SolidBrush(Color.FromArgb(38, 38, 38));
                    e.Graphics.FillRectangle(b, picThumbnail.ClientRectangle);
                    TextRenderer.DrawText(e.Graphics, _placeholderText, FontPlaceholder,
                        picThumbnail.ClientRectangle, Color.FromArgb(90, 90, 90),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };

            lblTitle = new Label
            {
                Location = new Point(130, 15),
                Size = new Size(515, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = FontTitle,
                ForeColor = Color.FromArgb(240, 240, 240),
                BackColor = ColorCardBg,
                AutoEllipsis = true,
                Text = "Fetching info..."
            };

            picPlatformIcon = new PictureBox
            {
                Location = new Point(130, 48),
                Size = new Size(20, 20),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };

            lblStatus = new Label
            {
                Location = new Point(155, 50),
                Size = new Size(270, 18),
                Font = FontSub,
                ForeColor = Color.FromArgb(170, 170, 170),
                BackColor = ColorCardBg,
                AutoEllipsis = true,
                Text = "Waiting in queue..."
            };

            lblMeta = new Label
            {
                Location = new Point(430, 50),
                Size = new Size(205, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = FontSub,
                ForeColor = Color.FromArgb(140, 140, 140),
                BackColor = ColorCardBg,
                TextAlign = ContentAlignment.MiddleRight,
                Text = ""
            };

            this.Controls.Add(picThumbnail);
            this.Controls.Add(lblTitle);
            this.Controls.Add(picPlatformIcon);
            this.Controls.Add(lblStatus);
            this.Controls.Add(lblMeta);

            ForwardMouseEvents(this);
        }

        private void UpdateThemeColors()
        {
            Color current = _isSelected ? ColorCardSelected : (_isHovered ? ColorCardHover : ColorCardBg);
            this.BackColor = current;
            lblTitle.BackColor = current;
            lblStatus.BackColor = current;
            lblMeta.BackColor = current;
            Invalidate();
        }

        private void ForwardMouseEvents(Control root)
        {
            foreach (Control c in root.Controls)
            {
                c.MouseEnter += (s, e) => { _isHovered = true; UpdateThemeColors(); };
                c.MouseLeave += (s, e) => { _isHovered = false; UpdateThemeColors(); };
                c.MouseDown += (s, e) => this.OnMouseDown(e);
                c.MouseDoubleClick += (s, e) => this.OnMouseDoubleClick(e);
            }
            this.MouseEnter += (s, e) => { _isHovered = true; UpdateThemeColors(); };
            this.MouseLeave += (s, e) => { _isHovered = false; UpdateThemeColors(); };
        }

        public async Task FetchThumbnailAsync(string url, string localPath = null)
        {
            if (!string.IsNullOrEmpty(localPath))
            {
                string ext = Path.GetExtension(localPath).ToLowerInvariant();
                if (ext == ".zip") _placeholderText = "ZIP";
                else if (ext == ".gif") _placeholderText = "GIF";
                else if (ext == ".mp3" || ext == ".m4a" || ext == ".flac") _placeholderText = "MP3";
                else if (Directory.Exists(localPath)) _placeholderText = "PLAYLIST";
                else _placeholderText = "MEDIA";
            }
            else if (UrlUtils.IsPlaylist(url))
            {
                _placeholderText = "PLAYLIST";
            }

            if (!IsDisposed)
            {
                if (InvokeRequired) BeginInvoke(new Action(() => picThumbnail?.Invalidate()));
                else picThumbnail?.Invalidate();
            }

            await Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                    {
                        string ext = Path.GetExtension(localPath).ToLowerInvariant();
                        if (ext == ".zip")
                        {
                            var bmp = ThumbnailExtractor.ExtractThumbnailFromZip(localPath);
                            if (bmp != null) { SetThumbnail(bmp); return; }
                        }
                        else if (ext == ".jpg" || ext == ".png" || ext == ".jpeg" || ext == ".bmp")
                        {
                            var bmp = ThumbnailExtractor.LoadBitmapSafely(localPath);
                            if (bmp != null) { SetThumbnail(bmp); return; }
                        }
                        else if (ext == ".webp" || ext == ".avif" || ext == ".gif" || ext == ".mp4" || ext == ".webm" || ext == ".mkv" || ext == ".avi" || ext == ".flv" || ext == ".mov")
                        {
                            var bmp = ThumbnailExtractor.ExtractVideoFrame(localPath);
                            if (bmp != null) { SetThumbnail(bmp); return; }
                        }
                        else if (ext == ".mp3" || ext == ".m4a" || ext == ".flac")
                        {
                            var bmp = ThumbnailExtractor.ExtractAudioCover(localPath);
                            if (bmp != null) { SetThumbnail(bmp); return; }
                        }
                    }

                    if (!string.IsNullOrEmpty(localPath) && Directory.Exists(localPath))
                    {
                        var dir = new DirectoryInfo(localPath);

                        var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".png", ".jpeg", ".bmp" };
                        var firstImg = dir.GetFiles("*.*", SearchOption.AllDirectories).FirstOrDefault(f => imgExts.Contains(f.Extension));
                        if (firstImg != null)
                        {
                            var bmp = ThumbnailExtractor.LoadBitmapSafely(firstImg.FullName);
                            if (bmp != null) SetThumbnail(bmp);
                            return;
                        }

                        var vidExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".avif", ".gif", ".mp4", ".webm", ".mkv", ".avi", ".flv", ".mov" };
                        var firstVid = dir.GetFiles("*.*", SearchOption.AllDirectories).FirstOrDefault(f => vidExts.Contains(f.Extension));
                        if (firstVid != null)
                        {
                            var bmp = ThumbnailExtractor.ExtractVideoFrame(firstVid.FullName);
                            if (bmp != null) SetThumbnail(bmp);
                            return;
                        }

                        var audExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac" };
                        var firstAud = dir.GetFiles("*.*", SearchOption.AllDirectories).FirstOrDefault(f => audExts.Contains(f.Extension));
                        if (firstAud != null)
                        {
                            var bmp = ThumbnailExtractor.ExtractAudioCover(firstAud.FullName);
                            if (bmp != null) SetThumbnail(bmp);
                            return;
                        }
                        return;
                    }

                    if (!string.IsNullOrEmpty(url) && (url.Contains("youtube.com") || url.Contains("youtu.be")))
                    {
                        string ytId = UrlUtils.ExtractMediaId(url);
                        if (!string.IsNullOrEmpty(ytId))
                        {
                            var data = await _thumbClient.GetByteArrayAsync($"https://img.youtube.com/vi/{ytId}/mqdefault.jpg");
                            using var ms = new MemoryStream(data);
                            using var tempBmp = new Bitmap(ms);
                            SetThumbnail(new Bitmap(tempBmp));
                        }
                    }
                }
                catch { }
            });
        }

        public void SetTitle(string title)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetTitle(title))); return; }
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
            lblTitle.Text = Title;
        }

        public void SetThumbnail(Image img)
        {
            if (IsDisposed)
            {
                img?.Dispose();
                return;
            }
            if (InvokeRequired) { BeginInvoke(new Action(() => SetThumbnail(img))); return; }
            if (picThumbnail.Image != null)
            {
                var old = picThumbnail.Image;
                picThumbnail.Image = null;
                old.Dispose();
            }
            picThumbnail.Image = img;
        }

        public void SetPlatform(string url)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetPlatform(url))); return; }

            string prefix = "web";
            if (!string.IsNullOrEmpty(url))
            {
                if (url.Contains("youtube.com") || url.Contains("youtu.be")) prefix = "youtube";
                else if (url.Contains("facebook.com") || url.Contains("fb.me") || url.Contains("fb.watch")) prefix = "facebook";
                else if (url.Contains("x.com") || url.Contains("twitter.com")) prefix = "x";
                else if (url.Contains("pixiv.net")) prefix = "pixiv";
                else if (url.Contains("bilibili.com")) prefix = "bilibili";
                else if (url.Contains("tiktok.com") || url.Contains("vt.tiktok.com")) prefix = "tiktok";
                else if (url.Contains("reddit.com") || url.Contains("redd.it")) prefix = "reddit";
                else if (url.Contains("nhentai.net")) prefix = "nhentai";
                else if (url.Contains("hitomi.la")) prefix = "hitomi";
            }

            lock (_cacheLock)
            {
                if (_iconCache.TryGetValue(prefix, out Image cachedImg))
                {
                    picPlatformIcon.Image = cachedImg;
                    picPlatformIcon.BackColor = Color.Transparent;
                    return;
                }

                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string resName = asm.GetManifestResourceNames()
                        .FirstOrDefault(r => r.IndexOf($".Logos.{prefix}.", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!string.IsNullOrEmpty(resName))
                    {
                        using var stream = asm.GetManifestResourceStream(resName);
                        if (stream != null)
                        {
                            using var tempImg = Image.FromStream(stream);
                            var finalBmp = new Bitmap(tempImg);
                            _iconCache[prefix] = finalBmp;
                            picPlatformIcon.Image = finalBmp;
                            picPlatformIcon.BackColor = Color.Transparent;
                            return;
                        }
                    }
                }
                catch { }

                picPlatformIcon.Image = null;
                picPlatformIcon.BackColor = Color.FromArgb(90, 90, 90);
            }
        }

        public void SetProgress(int percent, string speed = null)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(percent, speed))); return; }
            _progressPercentage = Math.Max(0, Math.Min(100, percent));
            lblStatus.Text = !string.IsNullOrEmpty(speed) ? $"Downloading ({percent}%) • {speed}" : $"Downloading ({percent}%)...";
            lblStatus.ForeColor = ColorAccent;
            Invalidate();
        }

        public void SetSuccess(string size)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetSuccess(size))); return; }
            _progressPercentage = 100;
            lblStatus.Text = "Completed";
            lblStatus.ForeColor = ColorSuccess;
            if (!string.IsNullOrEmpty(size)) lblMeta.Text = $"💾 {size}";
            Invalidate();
        }

        public void SetError(string shortError)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetError(shortError))); return; }
            lblStatus.Text = shortError;
            lblStatus.ForeColor = ColorError;
            Invalidate();
        }

        public void SetStatusText(string status)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatusText(status))); return; }
            lblStatus.Text = status;
            lblStatus.ForeColor = Color.FromArgb(170, 170, 170);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            using (var borderPen = new Pen(Color.FromArgb(42, 42, 42), 1))
            {
                g.DrawLine(borderPen, 0, this.Height - 1, this.Width, this.Height - 1);
            }

            if (_isSelected)
            {
                using var accentBrush = new SolidBrush(ColorAccent);
                g.FillRectangle(accentBrush, 0, 0, 3, this.Height);
            }

            if (_progressPercentage > 0 && _progressPercentage < 100)
            {
                int barWidth = (int)((float)this.Width * _progressPercentage / 100f);
                using var progressBrush = new SolidBrush(ColorAccent);
                g.FillRectangle(progressBrush, 0, this.Height - 3, barWidth, 3);
            }
        }

        // Giải phóng triệt để Bitmap Thumbnail khỏi RAM khi thẻ bị xóa (nhưng giữ lại Icon Cache dùng chung)
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (picThumbnail != null && picThumbnail.Image != null)
                {
                    picThumbnail.Image.Dispose();
                    picThumbnail.Image = null;
                }
                if (picPlatformIcon != null)
                {
                    picPlatformIcon.Image = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}