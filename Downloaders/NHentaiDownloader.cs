using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace Koxmix.Downloaders
{
    public class NHentaiDownloader : IDownloader
    {
        private static CookieContainer _cookieContainer = new CookieContainer();
        private static HttpClientHandler _handler;
        private static HttpClient _client;
        private static readonly object _initLock = new object();
        private readonly CliRunner _cliRunner = new CliRunner();

        public NHentaiDownloader()
        {
            ReloadClientAndCookies();
        }

        private static void ReloadClientAndCookies()
        {
            lock (_initLock)
            {
                _cookieContainer = new CookieContainer();
                _handler = new HttpClientHandler
                {
                    CookieContainer = _cookieContainer,
                    UseCookies = true,
                    MaxConnectionsPerServer = 500,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                };

                _client = new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(2) };

                string uaPath = Path.Combine(AppUtils.DataDir, "app_useragent.txt");
                string realUa = File.Exists(uaPath)
                    ? File.ReadAllText(uaPath).Trim()
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

                _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", realUa);
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

                try
                {
                    string cookiePath = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                    if (!File.Exists(cookiePath)) return;

                    var baseUri = new Uri("https://nhentai.net");
                    foreach (string line in File.ReadAllLines(cookiePath))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 7)
                        {
                            string domain = parts[0].Trim();
                            string name = parts[5].Trim();
                            string value = parts[6].Trim();

                            if (domain.Contains("nhentai.net") && !domain.StartsWith(".www.", StringComparison.OrdinalIgnoreCase))
                            {
                                try { _cookieContainer.Add(baseUri, new Cookie(name, value, "/", "nhentai.net")); } catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && url.Contains("nhentai.net");

        public async Task DownloadAsync(DownloadContext ctx)
        {
            ReloadClientAndCookies();
            string targetFolder = ctx.TargetFolder;
            Directory.CreateDirectory(targetFolder);

            string galleryId = Regex.Match(ctx.Url, @"g/(\d+)").Groups[1].Value;
            if (string.IsNullOrEmpty(galleryId))
                throw new ArgumentException("Could not find nHentai ID in the URL.");

            var existingZips = new DirectoryInfo(targetFolder).GetFiles($"*[{galleryId}]*.zip").ToList();
            if (existingZips.Count > 0)
            {
                if (ctx.ForceRedownload)
                {
                    foreach (var z in existingZips) FormatUtils.TryDeleteFile(z.FullName);
                }
                else
                {
                    ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                    ctx.OnSuccess?.Invoke(Path.GetFileNameWithoutExtension(existingZips[0].Name), FormatUtils.FormatFileSize(existingZips[0].Length), existingZips[0].FullName);
                    return;
                }
            }

            ctx.OnStatusChanged?.Invoke("Fetching gallery info...");

            string jsonResponse = await TryFetchMetadataHttpAsync(galleryId, ctx.Token);

            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                ctx.OnStatusChanged?.Invoke("Solving Cloudflare challenge via Chromium...");
                jsonResponse = await SolveAndFetchJsonViaWebView2Async(galleryId, ctx.Token);
            }

            if (string.IsNullOrWhiteSpace(jsonResponse) || !jsonResponse.TrimStart().StartsWith("{"))
            {
                await DownloadViaGalleryDlFallbackAsync(ctx, galleryId, targetFolder, null, null);
                return;
            }

            JObject data = JObject.Parse(jsonResponse);

            string mediaId = data["media_id"]?.ToString();
            string rawTitle = data["title"]?["pretty"]?.ToString()
                           ?? data["title"]?["english"]?.ToString()
                           ?? data["title"]?["japanese"]?.ToString()
                           ?? $"Gallery {galleryId}";

            string artist = ExtractPrimaryArtist(data["tags"] as JArray);
            string folderName = FormatUtils.BuildStandardName("nHentai", artist, rawTitle, galleryId);
            string finalPath = Path.Combine(targetFolder, folderName);
            Directory.CreateDirectory(finalPath);

            JArray pages = data["images"]?["pages"] as JArray;
            if (pages == null || pages.Count == 0)
                throw new Exception("No manga pages found.");

            int totalImages = pages.Count;
            long totalBytes = 0;
            int downloadedCount = 0;
            object lockObj = new object();

            ctx.OnStatusChanged?.Invoke($"Found {totalImages} pages. Starting download...");

            string[] imageHosts = { "i.nhentai.net", "i2.nhentai.net", "i3.nhentai.net", "i4.nhentai.net", "i5.nhentai.net", "i7.nhentai.net" };

            using (var semaphore = new SemaphoreSlim(16, 16))
            {
                var downloadTasks = new List<Task>();

                for (int i = 0; i < totalImages; i++)
                {
                    int index = i;
                    int pageNum = index + 1;
                    string extType = pages[index]["t"]?.ToString();
                    string ext = extType switch
                    {
                        "j" => ".jpg",
                        "p" => ".png",
                        "w" => ".webp",
                        "g" => ".gif",
                        _ => ".jpg"
                    };

                    string fileName = $"{pageNum:D4}{ext}";
                    string filePath = Path.Combine(finalPath, fileName);
                    string tempFilePath = filePath + ".part";
                    string pageReferer = $"https://nhentai.net/g/{galleryId}/{pageNum}/";

                    downloadTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(ctx.Token);
                        try
                        {
                            ctx.Token.ThrowIfCancellationRequested();

                            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                            {
                                await Task.Delay(Random.Shared.Next(50, 150), ctx.Token);

                                foreach (string host in imageHosts)
                                {
                                    string imgUrl = $"https://{host}/galleries/{mediaId}/{pageNum}{ext}";
                                    try
                                    {
                                        using var req = new HttpRequestMessage(HttpMethod.Get, imgUrl);
                                        req.Headers.Referrer = new Uri(pageReferer);

                                        using var imgResponse = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.Token);
                                        if (!imgResponse.IsSuccessStatusCode) continue;

                                        using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                                        {
                                            await imgResponse.Content.CopyToAsync(fs, ctx.Token);
                                        }

                                        File.Move(tempFilePath, filePath, true);
                                        lock (lockObj) totalBytes += imgResponse.Content.Headers.ContentLength ?? new FileInfo(filePath).Length;
                                        break;
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch { }
                                }
                            }
                            else
                            {
                                lock (lockObj) totalBytes += new FileInfo(filePath).Length;
                            }

                            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                            {
                                int currentDone = Interlocked.Increment(ref downloadedCount);
                                ctx.OnStatusChanged?.Invoke($"Downloading: {currentDone}/{totalImages} pages");
                                ctx.OnProgressChanged?.Invoke($"{Math.Round((double)currentDone / totalImages * 100)}%", null);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        finally { semaphore.Release(); }
                    }, ctx.Token));
                }

                try
                {
                    await Task.WhenAll(downloadTasks);
                }
                catch (OperationCanceledException)
                {
                    try { if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true); } catch { }
                    throw;
                }
            }

            var downloadedFiles = Directory.GetFiles(finalPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (downloadedFiles.Length < totalImages)
            {
                try { if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true); } catch { }
                await DownloadViaGalleryDlFallbackAsync(ctx, galleryId, targetFolder, artist, rawTitle, totalImages);
                return;
            }

            ctx.OnStatusChanged?.Invoke("Packaging manga into .zip...");
            string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(finalPath, deleteSourceFolder: true), ctx.Token);
            if (File.Exists(zipPath)) totalBytes = new FileInfo(zipPath).Length;

            ctx.OnStatusChanged?.Invoke("Completed");
            ctx.OnSuccess?.Invoke(folderName, FormatUtils.FormatFileSize(totalBytes), zipPath);
        }

        private static string ExtractPrimaryArtist(JArray tags)
        {
            if (tags == null) return null;
            var artists = new List<string>();
            var groups = new List<string>();

            foreach (var tag in tags)
            {
                string type = tag["type"]?.ToString();
                string name = tag["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (type == "artist") artists.Add(name);
                else if (type == "group") groups.Add(name);
            }

            if (artists.Count > 0) return string.Join(", ", artists.Take(2));
            if (groups.Count > 0) return string.Join(", ", groups.Take(2));
            return null;
        }

        private static async Task<string> TryFetchMetadataHttpAsync(string galleryId, CancellationToken ct)
        {
            string[] candidateUrls =
            {
                $"https://nhentai.net/g/{galleryId}/1/",
                $"https://nhentai.net/g/{galleryId}/"
            };

            foreach (string url in candidateUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Referrer = new Uri($"https://nhentai.net/g/{galleryId}/");

                    using var res = await _client.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        string html = await res.Content.ReadAsStringAsync(ct);
                        string extracted = ExtractJsonParseFromHtml(html);
                        if (!string.IsNullOrWhiteSpace(extracted))
                            return extracted;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            try
            {
                using var apiReq = new HttpRequestMessage(HttpMethod.Get, $"https://nhentai.net/api/gallery/{galleryId}");
                apiReq.Headers.Referrer = new Uri($"https://nhentai.net/g/{galleryId}/");
                using var apiRes = await _client.SendAsync(apiReq, ct);
                if (apiRes.IsSuccessStatusCode)
                {
                    string text = await apiRes.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
                        return text;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return null;
        }

        private static string ExtractJsonParseFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;

            var matches = Regex.Matches(html, @"JSON\.parse\(\s*(""[^""\\]*(?:\\.[^""\\]*)*"")\s*\)");
            foreach (Match m in matches)
            {
                try
                {
                    string decoded = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(m.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(decoded) && decoded.TrimStart().StartsWith("{") && decoded.Contains("\"media_id\""))
                    {
                        return decoded;
                    }
                }
                catch { }
            }

            return null;
        }

        private static Task<string> SolveAndFetchJsonViaWebView2Async(string galleryId, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (Application.OpenForms.Count == 0)
            {
                tcs.TrySetResult(null);
                return tcs.Task;
            }

            Form mainForm = Application.OpenForms[0];
            mainForm.BeginInvoke(new Action(async () =>
            {
                WebView2 webView = null;
                try
                {
                    webView = new WebView2
                    {
                        Size = new Size(100, 100),
                        Location = new Point(-2000, -2000),
                        Visible = true
                    };
                    mainForm.Controls.Add(webView);

                    string userDataFolder = Path.Combine(AppUtils.DataDir, "BrowserData");
                    var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await webView.EnsureCoreWebView2Async(env);

                    string realUa = webView.CoreWebView2.Settings.UserAgent;
                    if (!string.IsNullOrEmpty(realUa))
                    {
                        try { File.WriteAllText(Path.Combine(AppUtils.DataDir, "app_useragent.txt"), realUa); } catch { }
                    }

                    var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<CoreWebView2NavigationCompletedEventArgs> navHandler = null;
                    navHandler = (s, e) => navTcs.TrySetResult(e.IsSuccess);
                    webView.NavigationCompleted += navHandler;

                    webView.CoreWebView2.Navigate($"https://nhentai.net/g/{galleryId}/");

                    using var reg = ct.Register(() => navTcs.TrySetCanceled());
                    await Task.WhenAny(navTcs.Task, Task.Delay(8000, ct));

                    for (int attempt = 0; attempt < 4 && !ct.IsCancellationRequested; attempt++)
                    {
                        string rawResult = await webView.ExecuteScriptAsync(
                            "(function() { " +
                            "  if (window._gallery) return JSON.stringify(window._gallery); " +
                            "  try { " +
                            "    var xhr = new XMLHttpRequest(); " +
                            $"   xhr.open('GET', '/api/gallery/{galleryId}', false); " +
                            "    xhr.send(null); " +
                            "    if (xhr.status === 200 && xhr.responseText && xhr.responseText.indexOf('media_id') !== -1) return xhr.responseText; " +
                            "  } catch(e) {} " +
                            "  return document.documentElement ? document.documentElement.outerHTML : ''; " +
                            "})()");

                        if (!string.IsNullOrEmpty(rawResult) && rawResult != "null")
                        {
                            string unescaped = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(rawResult)?.Trim();
                            string finalJson = null;

                            if (!string.IsNullOrEmpty(unescaped))
                            {
                                if (unescaped.StartsWith("{") && unescaped.Contains("\"media_id\""))
                                {
                                    finalJson = unescaped;
                                }
                                else
                                {
                                    finalJson = ExtractJsonParseFromHtml(unescaped);
                                }
                            }

                            if (!string.IsNullOrEmpty(finalJson))
                            {
                                await SyncWebViewCookiesAsync(webView);
                                tcs.TrySetResult(finalJson);
                                return;
                            }
                        }

                        await Task.Delay(1000, ct);
                    }

                    tcs.TrySetResult(null);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
                finally
                {
                    if (webView != null)
                    {
                        try { mainForm.Controls.Remove(webView); } catch { }
                        try { webView.Dispose(); } catch { }
                    }
                }
            }));

            return tcs.Task;
        }

        private static async Task SyncWebViewCookiesAsync(WebView2 webView)
        {
            try
            {
                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://nhentai.net");
                if (cookies == null || cookies.Count == 0) return;

                var baseUri = new Uri("https://nhentai.net");
                lock (_initLock)
                {
                    foreach (var c in cookies)
                    {
                        try { _cookieContainer.Add(baseUri, new Cookie(c.Name, c.Value, "/", "nhentai.net")); } catch { }
                    }
                }

                string cookiePath = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                var existingLines = File.Exists(cookiePath)
                    ? File.ReadAllLines(cookiePath).Where(l => !l.Contains("nhentai.net")).ToList()
                    : new List<string> { "# Netscape HTTP Cookie File" };

                long fakeExpires = (long)(DateTime.UtcNow.AddYears(1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                foreach (var c in cookies)
                {
                    string domain = c.Domain.StartsWith(".") ? c.Domain : "." + c.Domain;
                    string secure = c.IsSecure ? "TRUE" : "FALSE";
                    string path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
                    existingLines.Add($"{domain}\tTRUE\t{path}\t{secure}\t{fakeExpires}\t{c.Name}\t{c.Value}");
                }

                File.WriteAllLines(cookiePath, existingLines, new UTF8Encoding(false));
            }
            catch { }
        }

        private async Task DownloadViaGalleryDlFallbackAsync(DownloadContext ctx, string galleryId, string targetFolder, string knownArtist, string knownTitle, int expectedPages = 0)
        {
            string tempPath = Path.Combine(targetFolder, $"Temp_nHentai_{galleryId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);

            try
            {
                ctx.OnStatusChanged?.Invoke("Downloading via gallery-dl engine...");
                bool isDownloading = true;

                _ = Task.Run(async () =>
                {
                    int lastCount = 0;
                    while (isDownloading && !ctx.Token.IsCancellationRequested)
                    {
                        await Task.Delay(800);
                        if (Directory.Exists(tempPath))
                        {
                            try
                            {
                                int currentCount = Directory.GetFiles(tempPath, "*.*", SearchOption.AllDirectories)
                                    .Count(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                                                !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                                                !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

                                if (currentCount > lastCount)
                                {
                                    lastCount = currentCount;
                                    if (expectedPages > 0)
                                    {
                                        ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount}/{expectedPages} pages...");
                                        ctx.OnProgressChanged?.Invoke($"{Math.Min(99, Math.Round((double)currentCount / expectedPages * 100))}%", null);
                                    }
                                    else
                                    {
                                        ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount} pages...");
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                });

                try
                {
                    await _cliRunner.DownloadGalleryAsync($"https://nhentai.net/g/{galleryId}/", tempPath, ctx.Token, useCookies: true);
                }
                catch when (!ctx.Token.IsCancellationRequested)
                {
                    try
                    {
                        await _cliRunner.DownloadGalleryAsync($"https://nhentai.net/g/{galleryId}/", tempPath, ctx.Token, useCookies: false);
                    }
                    catch (Exception ex) when (!ctx.Token.IsCancellationRequested)
                    {
                        throw new Exception($"[Cloudflare Block] Please click the 'Cookies' button, select nHentai, wait for the homepage to load, and click Save!\nDetails: {ex.Message}");
                    }
                }
                finally
                {
                    isDownloading = false;
                }

                ctx.Token.ThrowIfCancellationRequested();

                var (metaArtist, metaTitle) = FormatUtils.ExtractAndCleanupGalleryDlMetadata(tempPath);
                string finalArtist = !string.IsNullOrWhiteSpace(knownArtist) ? knownArtist : metaArtist;
                string finalTitle = !string.IsNullOrWhiteSpace(knownTitle) ? knownTitle : (metaTitle ?? $"Gallery {galleryId}");

                var downloadedFiles = new DirectoryInfo(tempPath).GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => !f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase) &&
                                !f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                                !f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name)
                    .ToArray();

                if (downloadedFiles.Length == 0)
                {
                    throw new Exception("[Cloudflare Block] Please click the 'Cookies' button, select nHentai, wait for the homepage to load, and click Save!");
                }

                string folderName = FormatUtils.BuildStandardName("nHentai", finalArtist, finalTitle, galleryId);
                string finalPath = Path.Combine(targetFolder, folderName);
                Directory.CreateDirectory(finalPath);

                int pageIdx = 1;
                foreach (var file in downloadedFiles)
                {
                    try
                    {
                        string cleanPageName = $"{pageIdx++:D4}{file.Extension}";
                        string newPagePath = Path.Combine(finalPath, cleanPageName);
                        if (File.Exists(newPagePath)) FormatUtils.TryDeleteFile(newPagePath);
                        file.MoveTo(newPagePath);
                    }
                    catch { }
                }

                ctx.OnStatusChanged?.Invoke("Packaging manga into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(finalPath, deleteSourceFolder: true), ctx.Token);
                long totalBytes = File.Exists(zipPath) ? new FileInfo(zipPath).Length : downloadedFiles.Sum(f => f.Length);

                ctx.OnStatusChanged?.Invoke("Completed");
                ctx.OnSuccess?.Invoke(folderName, FormatUtils.FormatFileSize(totalBytes), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true); } catch { }
            }
        }
    }
}