using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Koxmix.Controls;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;

namespace Koxmix.Forms
{
    public partial class MainForm : Form
    {
        private const int MaxDisplayedCards = 50;

        private readonly HistoryManager _historyManager = new HistoryManager();
        private readonly DownloadRouter _router = new DownloadRouter();
        private readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(2, 2);

        private FlowLayoutPanel _pnlCards;
        private DownloadCardItem _selectedCard;
        private bool _isProcessingLinks = false;

        private ContextMenuStrip _contextMenu;
        private ToolStripMenuItem _menuOpenFile;
        private ToolStripMenuItem _menuOpenFolder;
        private ToolStripMenuItem _menuCopyPath;
        private ToolStripMenuItem _menuStop;
        private ToolStripMenuItem _menuRedownload;
        private ToolStripMenuItem _menuRemoveFromList;
        private ToolStripMenuItem _menuDeleteFile;

        public MainForm()
        {
            InitializeComponent();

            SetupDarkLayout();
            InitializeDownloader();
            InitContextMenu();

            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;
            this.FormClosing += MainForm_FormClosing;

            LoadHistoryData();
            CleanupZombieTempFolders();

            _ = Task.Run(async () => await UpdateChecker.CheckForUpdatesAsync(this));
        }

        private void BindCardEvents(DownloadCardItem card)
        {
            void Card_MouseDown(object sender, MouseEventArgs ev)
            {
                if (ev.Button == MouseButtons.Right || ev.Button == MouseButtons.Left)
                {
                    if (_selectedCard != null && _selectedCard != card) _selectedCard.IsSelected = false;
                    _selectedCard = card;
                    card.IsSelected = true;

                    if (ev.Button == MouseButtons.Right)
                    {
                        _contextMenu.Show(Cursor.Position);
                    }
                }
            }

            void Card_MouseDoubleClick(object sender, MouseEventArgs ev)
            {
                if (ev.Button == MouseButtons.Left)
                {
                    string target = card.Tag as string ?? card.TargetPath;
                    if (!string.IsNullOrEmpty(target))
                    {
                        try
                        {
                            if (File.Exists(target)) Process.Start("explorer.exe", $"/select,\"{target}\"");
                            else if (Directory.Exists(target)) Process.Start("explorer.exe", $"\"{target}\"");
                        }
                        catch { }
                    }
                }
            }

            void AttachEventsToControl(Control ctrl)
            {
                ctrl.MouseDown += Card_MouseDown;
                ctrl.MouseDoubleClick += Card_MouseDoubleClick;
                foreach (Control child in ctrl.Controls)
                {
                    AttachEventsToControl(child);
                }
            }

            AttachEventsToControl(card);
        }

        private void EnforceCardLimit()
        {
            while (_pnlCards.Controls.Count > MaxDisplayedCards)
            {
                Control toRemove = null;
                for (int i = _pnlCards.Controls.Count - 1; i >= 0; i--)
                {
                    var ctrl = _pnlCards.Controls[i];
                    if (ctrl.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
                        continue;

                    toRemove = ctrl;
                    break;
                }

                if (toRemove == null) break;
                if (_selectedCard == toRemove) _selectedCard = null;

                _pnlCards.Controls.Remove(toRemove);
                toRemove.Dispose();
            }
        }

        private void LoadHistoryData()
        {
            var historyData = _historyManager.LoadHistory();
            var recentHistory = historyData.Skip(Math.Max(0, historyData.Count - MaxDisplayedCards)).Reverse().ToList();

            _pnlCards.SuspendLayout();
            foreach (var data in recentHistory)
            {
                if (data.Length < 4) continue;
                string url = data[0], title = data[1], targetPath = data[2], statusOrSize = data[3];

                var card = new DownloadCardItem
                {
                    Url = url,
                    TargetPath = targetPath,
                    IsMp3 = HistoryManager.IsMp3HistoryEntry(title, targetPath),
                    Margin = new Padding(0, 0, 0, 4)
                };

                bool isCancelled = statusOrSize == "Cancelled";
                if (isCancelled)
                {
                    card.SetError("Cancelled");
                    card.Tag = null;
                }
                else
                {
                    card.Tag = targetPath;
                    card.SetSuccess(statusOrSize);
                }

                card.SetTitle(title);
                card.SetPlatform(url);

                BindCardEvents(card);
                _pnlCards.Controls.Add(card);

                bool isGallery = title.StartsWith("[nHentai]") || title.StartsWith("[Hitomi]") || title.StartsWith("[FB]");
                if (isGallery && Directory.Exists(targetPath))
                {
                    _ = Task.Run(async () =>
                    {
                        string dPath = await ThumbnailExtractor.GetOrGenerateGalleryThumbnailAsync(targetPath);
                        this.BeginInvoke(new Action(() =>
                        {
                            if (card.IsDisposed) return;
                            _ = card.FetchThumbnailAsync(url, dPath);
                        }));
                    });
                }
                else if (!isCancelled)
                {
                    _ = card.FetchThumbnailAsync(url, targetPath);
                }
                else
                {
                    _ = card.FetchThumbnailAsync(url);
                }
            }
            _pnlCards.ResumeLayout();
            ForceCardResize();
        }

        private bool IsDownloadModeMp3() => radAudio != null && radAudio.Checked;
        private bool GetEffectiveMp3Mode(string url)
        {
            if (!IsDownloadModeMp3() || string.IsNullOrEmpty(url)) return false;

            if (url.Contains("pixiv.net") ||
                url.Contains("nhentai.net") ||
                url.Contains("hitomi.la") ||
                url.Contains("x.com") ||
                url.Contains("twitter.com") ||
                url.Contains("reddit.com") ||
                url.Contains("redd.it") ||
                UrlUtils.IsFacebookAlbumOrPost(url))
            {
                return false;
            }
            return true;
        }

        #region Initialize & Update Core Engines

        private async void InitializeDownloader()
        {
            lblStatus.Text = "Checking and updating core engines (yt-dlp, gallery-dl, ffmpeg)...";
            btnDownload.Enabled = false;

            await Task.Run(() => AppUtils.EnsureAndUpdateAllToolsAsync(status =>
            {
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        this.BeginInvoke(new Action(() => lblStatus.Text = status));
                    }
                }
                catch { }
            }));

            lblStatus.Text = "Ready to download.";
            btnDownload.Enabled = true;
        }

        #endregion

        #region UI Layer - Batch Download Processing

        private async void btnDownload_Click(object sender, EventArgs e)
        {
            if (_isProcessingLinks) return;

            string[] lines = txtUrl.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            _isProcessingLinks = true;
            btnDownload.Enabled = false;
            txtUrl.Enabled = false;
            txtUrl.Clear();

            var uniqueLines = lines.Select(l => l.Trim())
                                   .Where(l => !string.IsNullOrEmpty(l))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            int validLinks = 0;
            int insertOffset = 0;

            _pnlCards.SuspendLayout();
            await Task.Delay(50);

            foreach (string urlRaw in uniqueLines)
            {
                string url = urlRaw;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult) ||
                    (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps) ||
                    !uriResult.Host.Contains("."))
                {
                    continue;
                }

                validLinks++;
                bool wantMp3 = GetEffectiveMp3Mode(url);
                string initialTargetFolder = PathManager.GetTargetPathForUrl(url, wantMp3);

                DownloadCardItem existingCard = null;
                bool isIncomingPlaylist = UrlUtils.IsPlaylist(url);
                string incomingMediaId = isIncomingPlaylist ? UrlUtils.ExtractPlaylistId(url) : UrlUtils.ExtractMediaId(url);

                foreach (Control c in _pnlCards.Controls)
                {
                    if (c is DownloadCardItem cardItem && cardItem.IsMp3 == wantMp3)
                    {
                        if (string.Equals(cardItem.Url, url, StringComparison.OrdinalIgnoreCase))
                        {
                            existingCard = cardItem;
                            break;
                        }

                        if (!string.IsNullOrEmpty(incomingMediaId) &&
                            UrlUtils.IsPlaylist(cardItem.Url) == isIncomingPlaylist &&
                            string.Equals(PathManager.GetTargetPathForUrl(cardItem.Url, cardItem.IsMp3), initialTargetFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            string cardMediaId = isIncomingPlaylist ? UrlUtils.ExtractPlaylistId(cardItem.Url) : UrlUtils.ExtractMediaId(cardItem.Url);
                            if (string.Equals(incomingMediaId, cardMediaId, StringComparison.OrdinalIgnoreCase))
                            {
                                existingCard = cardItem;
                                break;
                            }
                        }
                    }
                }

                if (existingCard != null)
                {
                    _pnlCards.Controls.SetChildIndex(existingCard, insertOffset++);

                    if (existingCard.Tag is CancellationTokenSource oldCts && !oldCts.IsCancellationRequested)
                    {
                        continue;
                    }

                    existingCard.TargetPath = initialTargetFolder;
                    StartDownloadForCard(existingCard, url, forceRedownload: false);
                }
                else
                {
                    var card = new DownloadCardItem
                    {
                        Url = url,
                        TargetPath = initialTargetFolder,
                        IsMp3 = wantMp3,
                        Width = _pnlCards.ClientSize.Width,
                        Margin = new Padding(0, 0, 0, 4)
                    };

                    card.SetTitle(url);
                    card.SetPlatform(url);
                    card.SetStatusText("Waiting in queue...");

                    _ = card.FetchThumbnailAsync(url);
                    BindCardEvents(card);

                    _pnlCards.Controls.Add(card);
                    _pnlCards.Controls.SetChildIndex(card, insertOffset++);

                    StartDownloadForCard(card, url, forceRedownload: false);
                }
            }

            EnforceCardLimit();
            _pnlCards.ResumeLayout();

            if (validLinks == 0)
            {
                MessageBox.Show("Please enter valid URLs.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            btnDownload.Enabled = true;
            txtUrl.Enabled = true;
            txtUrl.Focus();
            _isProcessingLinks = false;
        }

        private void StartDownloadForCard(DownloadCardItem card, string url, bool forceRedownload = false)
        {
            var cts = new CancellationTokenSource();
            card.Tag = cts;

            bool effectiveMp3 = card.IsMp3;
            string categoryFolder = PathManager.GetTargetPathForUrl(url, effectiveMp3);
            card.TargetPath = categoryFolder;

            if (string.IsNullOrWhiteSpace(card.Title) || card.Title == "Untitled" || card.Title == "Fetching info...")
            {
                card.SetTitle(url);
            }
            card.SetStatusText("Waiting in queue...");

            DownloadContext ctx = null;
            ctx = new DownloadContext
            {
                Url = url,
                IsMp3 = effectiveMp3,
                ForceRedownload = forceRedownload,
                TargetFolder = categoryFolder,
                StartTime = DateTime.Now,
                Token = cts.Token,

                OnStatusChanged = status =>
                {
                    if (!IsDisposed && !card.IsDisposed)
                        this.BeginInvoke(new Action(() => { if (!card.IsDisposed) card.SetStatusText(status); }));
                },

                OnProgressChanged = (progressStr, speed) =>
                {
                    if (!IsDisposed && !card.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (card.IsDisposed) return;
                            int percent = 0;
                            if (!string.IsNullOrEmpty(progressStr))
                            {
                                double.TryParse(progressStr.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double p);
                                percent = (int)p;
                            }
                            card.SetProgress(percent, speed);
                        }));
                    }
                },

                OnSuccess = (name, size, path) =>
                {
                    if (!IsDisposed && !card.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (card.IsDisposed) return;
                            url = ctx.Url;
                            card.Url = ctx.Url;
                            card.SetTitle(name);
                            card.SetSuccess(size);

                            card.Tag = path;
                            card.TargetPath = path;

                            bool isGallery = name.StartsWith("[nHentai]") || name.StartsWith("[Hitomi]") || name.StartsWith("[FB]");

                            if (isGallery && Directory.Exists(path))
                            {
                                _ = Task.Run(async () =>
                                {
                                    string dPath = await ThumbnailExtractor.GetOrGenerateGalleryThumbnailAsync(path);
                                    this.BeginInvoke(new Action(() =>
                                    {
                                        if (card.IsDisposed) return;
                                        _ = card.FetchThumbnailAsync(url, dPath);
                                        _historyManager.AddToHistory(url, name, path, size);
                                    }));
                                });
                            }
                            else
                            {
                                _ = card.FetchThumbnailAsync(url, path);
                                _historyManager.AddToHistory(url, name, path, size);
                            }
                        }));
                    }
                },

                OnError = ex =>
                {
                    if (!IsDisposed && !card.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (card.IsDisposed) return;
                            card.SetError("Download failed");
                            card.Tag = null;
                            MessageBox.Show(ex.ToString(), "Error Details", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                }
            };

            _ = Task.Run(async () =>
            {
                bool hasSemaphoreLock = false;
                try
                {
                    await _downloadSemaphore.WaitAsync(cts.Token);
                    hasSemaphoreLock = true;
                    cts.Token.ThrowIfCancellationRequested();

                    await _router.ProcessDownloadAsync(ctx);
                }
                catch (Exception ex) when (ex is OperationCanceledException || cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (card != null && !card.IsDisposed)
                            {
                                card.SetError("Cancelled");
                                card.Tag = null;
                            }
                        }));
                    }
                    catch { }
                }
                finally
                {
                    if (hasSemaphoreLock) _downloadSemaphore.Release();
                }
            });
        }

        #endregion

        #region UI Utilities & Form Events

        private void CleanupZombieTempFolders()
        {
            Task.Run(() =>
            {
                try
                {
                    foreach (var folder in PathManager.GetAllRootFolders())
                    {
                        if (Directory.Exists(folder))
                        {
                            var tempDirs = Directory.GetDirectories(folder, "Temp_*", SearchOption.TopDirectoryOnly);
                            foreach (var dir in tempDirs)
                            {
                                var dirInfo = new DirectoryInfo(dir);
                                if (DateTime.Now - dirInfo.CreationTime > TimeSpan.FromHours(2))
                                {
                                    try { dirInfo.Delete(true); } catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_pnlCards != null)
            {
                foreach (Control c in _pnlCards.Controls)
                {
                    if (c.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
                    {
                        try { cts.Cancel(); } catch { }
                    }
                }
            }

            Thread.Sleep(500);

            string[] orphanProcesses = { "yt-dlp", "gallery-dl", "ffmpeg" };
            foreach (string pName in orphanProcesses)
            {
                try
                {
                    var procs = Process.GetProcessesByName(pName);
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.MainModule != null && p.MainModule.FileName.Contains(AppUtils.DataDir))
                            {
                                p.Kill();
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private void btnSettings_Click(object sender, EventArgs e) => new SettingsForm().ShowDialog();

        private void btnClearList_Click(object sender, EventArgs e)
        {
            if (_pnlCards.Controls.Count == 0) return;

            if (MessageBox.Show("Are you sure you want to clear completed/stopped cards from the list?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var controlsToRemove = new List<Control>();

                foreach (Control c in _pnlCards.Controls)
                {
                    if (c.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
                        continue;
                    controlsToRemove.Add(c);
                }

                _pnlCards.SuspendLayout();
                foreach (Control c in controlsToRemove)
                {
                    _pnlCards.Controls.Remove(c);
                    c.Dispose();
                }
                _pnlCards.ResumeLayout();

                _historyManager.ClearHistory();
                _selectedCard = null;
                GC.Collect();
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                if (this.ActiveControl is TextBox tb && tb != txtUrl) return;

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;

                        if (btnDownload.Enabled)
                        {
                            txtUrl.Text = Clipboard.GetText().Trim();
                            btnDownload.PerformClick();
                        }
                    }
                }
                catch { }
            }
        }

        #endregion

       #region Context Menu Handling

        private void InitContextMenu()
        {
            _contextMenu = new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
                BackColor = Color.FromArgb(32, 32, 32),
                ForeColor = Color.White,
                ShowImageMargin = false
            };

            _menuOpenFile = new ToolStripMenuItem("▶ Open File / Folder", null, MenuOpenFile_Click);
            _menuOpenFolder = new ToolStripMenuItem("📁 Open File Location", null, MenuOpenFolder_Click);
            _menuCopyPath = new ToolStripMenuItem("📋 Copy Path", null, MenuCopyPath_Click);
            _menuStop = new ToolStripMenuItem("⏹ Stop Download", null, MenuStop_Click);
            _menuRedownload = new ToolStripMenuItem("🔄 Redownload", null, MenuRedownload_Click);
            _menuRemoveFromList = new ToolStripMenuItem("🗑️ Remove from list", null, MenuRemoveFromList_Click);
            _menuDeleteFile = new ToolStripMenuItem("❌ Delete from disk", null, MenuDeleteFile_Click);

            _contextMenu.Items.AddRange(new ToolStripItem[]
            {
                _menuOpenFile,
                _menuOpenFolder,
                _menuCopyPath,
                new ToolStripSeparator(),
                _menuStop,
                _menuRedownload,
                _menuRemoveFromList,
                _menuDeleteFile
            });

            _contextMenu.Opening += ContextMenu_Opening;
        }

        private void ContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_selectedCard == null)
            {
                e.Cancel = true;
                return;
            }

            bool isDownloading = _selectedCard.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested;
            bool isCompleted = _selectedCard.Tag is string;

            string targetPath = _selectedCard.TargetPath;
            bool fileExists = !string.IsNullOrEmpty(targetPath) && File.Exists(targetPath);
            bool dirExists = !string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath);

            _menuOpenFile.Enabled = isCompleted && fileExists;
            _menuOpenFile.Text = fileExists ? "▶ Open File" : "▶ Open File / Folder";

            _menuOpenFolder.Enabled = isCompleted && (fileExists || dirExists);
            _menuCopyPath.Enabled = !string.IsNullOrEmpty(_selectedCard.Url) || (isCompleted && (fileExists || dirExists));

            _menuStop.Enabled = isDownloading;
            _menuRedownload.Enabled = !isDownloading && !string.IsNullOrEmpty(_selectedCard.Url);
            _menuRemoveFromList.Enabled = !isDownloading;
            _menuDeleteFile.Enabled = isCompleted && (fileExists || dirExists);
        }

        private void MenuOpenFile_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;
            string targetPath = _selectedCard.TargetPath;

            try
            {
                if (File.Exists(targetPath)) Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
                else if (Directory.Exists(targetPath)) Process.Start("explorer.exe", $"\"{targetPath}\"");
            }
            catch (Exception ex) { MessageBox.Show("Cannot open: " + ex.Message, "Error"); }
        }

        private void MenuOpenFolder_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;
            string targetPath = _selectedCard.TargetPath;

            try
            {
                if (File.Exists(targetPath)) Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                else if (Directory.Exists(targetPath)) Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Cannot open folder: " + ex.Message, "Error"); }
        }

        private void MenuCopyPath_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;

            bool isDownloading = _selectedCard.Tag is CancellationTokenSource;
            string copyText = isDownloading ? _selectedCard.Url : (_selectedCard.TargetPath ?? _selectedCard.Url);

            if (!string.IsNullOrEmpty(copyText))
            {
                Clipboard.SetText(copyText);
            }
        }

        private void MenuStop_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;

            if (_selectedCard.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                _selectedCard.SetError("Cancelled");
                _selectedCard.Tag = null;
            }
        }

        private async void MenuRedownload_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;
            string url = _selectedCard.Url;
            if (string.IsNullOrEmpty(url)) return;

            if (_selectedCard.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                await Task.Delay(1500);
            }

            _pnlCards.Controls.SetChildIndex(_selectedCard, 0);
            StartDownloadForCard(_selectedCard, url, forceRedownload: false);
        }

        private void MenuRemoveFromList_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;

            if (_selectedCard.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            _historyManager.RemoveFromHistory(_selectedCard.Url, _selectedCard.TargetPath);

            if (!_selectedCard.IsDisposed)
            {
                _pnlCards.Controls.Remove(_selectedCard);
                _selectedCard.Dispose();
            }
            _selectedCard = null;
        }

        private void MenuDeleteFile_Click(object sender, EventArgs e)
        {
            if (_selectedCard == null) return;

            if (_selectedCard.Tag is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            string targetPath = _selectedCard.TargetPath;
            string url = _selectedCard.Url;
            var rootFolders = PathManager.GetAllRootFolders();
            bool isRootFolder = rootFolders.Contains(targetPath);

            if (MessageBox.Show("Are you sure you want to PERMANENTLY DELETE this content and its temporary files from your disk?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try
                {
                    string mediaId = UrlUtils.ExtractMediaId(url);

                    if (isRootFolder && !string.IsNullOrEmpty(mediaId))
                    {
                        var files = Directory.GetFiles(targetPath, $"*{mediaId}*", SearchOption.TopDirectoryOnly);
                        foreach (var pf in files)
                        {
                            string ext = Path.GetExtension(pf).ToLowerInvariant();
                            bool isMedia = _selectedCard.IsMp3
                                ? (ext == ".mp3" || ext == ".m4a")
                                : (ext == ".mp4" || ext == ".webm" || ext == ".mkv" || ext == ".srt" || ext == ".vtt");
                            bool isTemp = ext == ".part" || ext == ".ytdl" || ext == ".temp" || ext == ".tmp" || ext == ".crdownload";
                            bool isFrag = Regex.IsMatch(pf, @"\.f\d+\.") || pf.Contains("-Frag");

                            if (isMedia || isTemp || isFrag)
                            {
                                FormatUtils.TryDeleteFile(pf);
                            }
                        }
                    }
                    else if (!isRootFolder)
                    {
                        if (File.Exists(targetPath))
                        {
                            FormatUtils.TryDeleteFile(targetPath);

                            if (!string.IsNullOrEmpty(mediaId))
                            {
                                string dir = Path.GetDirectoryName(targetPath);
                                string subDir = Path.Combine(dir, "Subtitles");
                                if (Directory.Exists(subDir))
                                {
                                    foreach (var s in Directory.GetFiles(subDir, $"*{mediaId}*"))
                                    {
                                        FormatUtils.TryDeleteFile(s);
                                    }
                                }
                            }
                        }
                        else if (Directory.Exists(targetPath))
                        {
                            Directory.Delete(targetPath, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Cannot delete: " + ex.Message, "Error");
                    return;
                }

                _historyManager.RemoveFromHistory(url, targetPath);
                if (!_selectedCard.IsDisposed)
                {
                    _pnlCards.Controls.Remove(_selectedCard);
                    _selectedCard.Dispose();
                }
                _selectedCard = null;
            }
        }

        #endregion

        #region Dark Theme Layout Setup

        private void SetupDarkLayout()
        {
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.FromArgb(230, 230, 230);

            foreach (Control c in panel1.Controls)
            {
                if (c is Label lbl)
                {
                    lbl.ForeColor = Color.FromArgb(200, 200, 200);
                    lbl.BackColor = Color.Transparent;
                }
                else if (c is RadioButton rb)
                {
                    rb.ForeColor = Color.FromArgb(220, 220, 220);
                    rb.BackColor = Color.Transparent;
                }
            }

            txtUrl.Multiline = false;
            txtUrl.ScrollBars = ScrollBars.None;
            txtUrl.BackColor = Color.FromArgb(42, 42, 42);
            txtUrl.ForeColor = Color.White;
            txtUrl.BorderStyle = BorderStyle.FixedSingle;
            txtUrl.Height = 36;

            btnDownload.BackColor = Color.FromArgb(14, 116, 232);
            btnDownload.ForeColor = Color.White;
            btnDownload.FlatStyle = FlatStyle.Flat;
            btnDownload.FlatAppearance.BorderSize = 0;
            btnDownload.Cursor = Cursors.Hand;

            btnBrowser.Click += (s, e) => new LoginBrowserForm().ShowDialog();

            btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowser.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDownload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClearList.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            StyleDarkButton(btnSettings);
            StyleDarkButton(btnClearList);
            StyleDarkButton(btnBrowser);

            _pnlCards = new FlowLayoutPanel
            {
                Location = new Point(12, 105),
                Size = new Size(this.ClientSize.Width - 24, this.ClientSize.Height - 135),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(22, 22, 22),
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            _pnlCards.SizeChanged += (s, e) => ForceCardResize();

            this.Controls.Add(_pnlCards);
            _pnlCards.BringToFront();

            if (lblStatus != null)
            {
                lblStatus.BackColor = Color.Transparent;
                lblStatus.ForeColor = Color.Black;
            }
        }

        private void ForceCardResize()
        {
            if (_pnlCards == null || _pnlCards.Controls.Count == 0) return;

            _pnlCards.SuspendLayout();
            int targetWidth = _pnlCards.ClientSize.Width;
            foreach (Control c in _pnlCards.Controls) c.Width = targetWidth;
            _pnlCards.ResumeLayout();
        }

        private void StyleDarkButton(Button btn)
        {
            if (btn == null) return;
            btn.BackColor = Color.FromArgb(48, 48, 48);
            btn.ForeColor = Color.FromArgb(230, 230, 230);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Cursor = Cursors.Hand;
        }

        #endregion
    }
}