using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Koxmix.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Koxmix.Forms
{
    [System.ComponentModel.DesignerCategory("Code")]
    public class LoginBrowserForm : Form
    {
        private WebView2 _webView;
        private ComboBox _cboSites;
        private Button _btnSaveCookies;
        private Label _lblStatus;

        private class SiteItem
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public override string ToString() => Name;
        }

        public LoginBrowserForm()
        {
            InitializeComponent();
            this.FormClosed += LoginBrowserForm_FormClosed;
            _ = InitializeBrowserAsync();
        }

        private void InitializeComponent()
        {
            this.Text = "🌐 Koxmix - Login Browser (Cookie Extraction)";
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;

            Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(40, 40, 40) };

            Label lblHint = new Label
            {
                Text = "Select Site:",
                Location = new Point(10, 15),
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.Transparent
            };

            _cboSites = new ComboBox
            {
                Location = new Point(100, 12),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            _cboSites.Items.AddRange(new SiteItem[]
            {
                new SiteItem { Name = "YouTube", Url = "https://www.youtube.com/" },
                new SiteItem { Name = "TikTok", Url = "https://www.tiktok.com/login" },
                new SiteItem { Name = "Pixiv", Url = "https://accounts.pixiv.net/login" },
                new SiteItem { Name = "X (Twitter)", Url = "https://x.com/login" },
                new SiteItem { Name = "Reddit", Url = "https://www.reddit.com/login/" },
                new SiteItem { Name = "nHentai", Url = "https://nhentai.net/login/" },
                new SiteItem { Name = "Hitomi.la", Url = "https://hitomi.la/" },
                new SiteItem { Name = "Facebook", Url = "https://www.facebook.com/" },
                new SiteItem { Name = "Bilibili", Url = "https://www.bilibili.com/" }
            });
            _cboSites.SelectedIndex = 0;

            _cboSites.SelectedIndexChanged += async (s, e) =>
            {
                if (_webView?.CoreWebView2 != null && _cboSites.SelectedItem is SiteItem item)
                {
                    _lblStatus.Text = $"Loading {item.Name}...";
                    await Task.Delay(100);
                    _webView.CoreWebView2.Navigate(item.Url);
                }
            };

            _btnSaveCookies = new Button
            {
                Text = "💾 SAVE COOKIES & CLOSE",
                Location = new Point(295, 10),
                Width = 180,
                Height = 30,
                BackColor = Color.FromArgb(46, 204, 113),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnSaveCookies.FlatAppearance.BorderSize = 0;
            _btnSaveCookies.Click += BtnSaveCookies_Click;

            _lblStatus = new Label
            {
                Text = "Loading Chromium engine...",
                Location = new Point(490, 15),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200),
                BackColor = Color.Transparent
            };

            topPanel.Controls.Add(lblHint);
            topPanel.Controls.Add(_cboSites);
            topPanel.Controls.Add(_btnSaveCookies);
            topPanel.Controls.Add(_lblStatus);

            _webView = new WebView2 { Dock = DockStyle.Fill };

            this.Controls.Add(_webView);
            this.Controls.Add(topPanel);
        }

        private async Task InitializeBrowserAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(AppUtils.DataDir, "BrowserData");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                string realUa = _webView.CoreWebView2.Settings.UserAgent;
                File.WriteAllText(Path.Combine(AppUtils.DataDir, "app_useragent.txt"), realUa);

                _webView.NavigationStarting += (s, e) => { _lblStatus.Text = "Loading page..."; };
                _webView.NavigationCompleted += (s, e) => { _lblStatus.Text = "Ready!"; };

                _lblStatus.Text = "Ready! Please log in to the desired site and click Save.";

                if (_cboSites.SelectedItem is SiteItem item)
                {
                    _webView.CoreWebView2.Navigate(item.Url);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initialize WebView2.\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnSaveCookies_Click(object sender, EventArgs e)
        {
            if (_webView?.CoreWebView2 == null) return;

            _btnSaveCookies.Enabled = false;
            _lblStatus.Text = "Extracting Cookies...";

            try
            {
                var rawCookies = new List<CoreWebView2Cookie>();
                var baseCookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(null);
                if (baseCookies != null) rawCookies.AddRange(baseCookies);

                string[] forceUrls =
                {
                    "https://youtube.com", "https://www.youtube.com", "https://youtu.be",
                    "https://tiktok.com", "https://www.tiktok.com",
                    "https://pixiv.net", "https://www.pixiv.net", "https://accounts.pixiv.net",
                    "https://x.com", "https://twitter.com",
                    "https://reddit.com", "https://www.reddit.com",
                    "https://nhentai.net", "https://hitomi.la",
                    "https://facebook.com", "https://www.facebook.com",
                    "https://bilibili.com", "https://www.bilibili.com", "https://space.bilibili.com"
                };

                foreach (var url in forceUrls)
                {
                    var specificCookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    if (specificCookies != null)
                    {
                        foreach (var c in specificCookies)
                        {
                            if (!rawCookies.Any(existing => existing.Name == c.Name && existing.Domain == c.Domain))
                            {
                                rawCookies.Add(c);
                            }
                        }
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("# Netscape HTTP Cookie File");
                sb.AppendLine("# Extracted by Koxmix WebView2 (Filtered)");
                sb.AppendLine();

                long fakeExpires = (long)(DateTime.UtcNow.AddYears(1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                bool foundPixivSession = false;

                foreach (var c in rawCookies)
                {
                    if (c.Name.StartsWith("_ga") || c.Name.StartsWith("_pubcid")) continue;
                    if (c.Name == "PHPSESSID") foundPixivSession = true;

                    string flag = "TRUE";
                    string path = c.Path;
                    string secure = c.IsSecure ? "TRUE" : "FALSE";

                    string rootDomain = c.Domain;
                    if (!rootDomain.StartsWith(".")) rootDomain = "." + rootDomain.TrimStart('.');

                    sb.Append($"{rootDomain}\t{flag}\t{path}\t{secure}\t{fakeExpires}\t{c.Name}\t{c.Value}\n");

                    if (!rootDomain.StartsWith(".www."))
                    {
                        string wwwDomain = ".www" + rootDomain;
                        sb.Append($"{wwwDomain}\t{flag}\t{path}\t{secure}\t{fakeExpires}\t{c.Name}\t{c.Value}\n");
                    }
                }

                string cookiePath = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                File.WriteAllText(cookiePath, sb.ToString(), new UTF8Encoding(false));

                string msg = "Successfully saved clean Cookies!";
                if (foundPixivSession)
                    msg += "\n\n[Success] Captured Pixiv PHPSESSID and cloned domains. Ready for R-18 downloads!";

                MessageBox.Show(msg, "Completed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cookie extraction failed:\n\n" + ex, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnSaveCookies.Enabled = true;
            }
        }

        private void LoginBrowserForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                if (_webView != null)
                {
                    _webView.Dispose();
                    _webView = null;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch { }
        }
    }
}