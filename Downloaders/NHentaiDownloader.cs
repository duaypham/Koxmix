using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Models;
using Koxmix.Utils;
using Newtonsoft.Json.Linq;

namespace Koxmix.Downloaders
{
    public class NHentaiDownloader : IDownloader
    {
        private static readonly CookieContainer _cookieContainer = new CookieContainer();
        private static HttpClientHandler _handler;
        private static HttpClient _client;
        private static readonly object _initLock = new object();

        public NHentaiDownloader()
        {
            lock (_initLock)
            {
                if (_client == null)
                {
                    _handler = new HttpClientHandler
                    {
                        CookieContainer = _cookieContainer,
                        UseCookies = true,
                        MaxConnectionsPerServer = 500,
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };

                    _client = new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(2) };

                    string uaPath = Path.Combine(AppUtils.DataDir, "app_useragent.txt");
                    string realUa = File.Exists(uaPath)
                        ? File.ReadAllText(uaPath).Trim()
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

                    _client.DefaultRequestHeaders.Add("User-Agent", realUa);
                    _client.DefaultRequestHeaders.Add("Referer", "https://nhentai.net/");
                }
            }
            LoadCookiesFromFile();
        }

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && url.Contains("nhentai.net");

        public async Task DownloadAsync(DownloadContext ctx)
        {
            LoadCookiesFromFile();
            string targetFolder = ctx.TargetFolder;
            Directory.CreateDirectory(targetFolder);

            string galleryId = Regex.Match(ctx.Url, @"g/(\d+)").Groups[1].Value;
            if (string.IsNullOrEmpty(galleryId))
                throw new ArgumentException("Không tìm thấy ID của nHentai trong URL.");

            var existingZips = new DirectoryInfo(targetFolder).GetFiles($"*[nHentai_{galleryId}]*.zip").ToList();
            var existingDirs = new DirectoryInfo(targetFolder).GetDirectories($"*[nHentai_{galleryId}]*").ToList();
            if (existingZips.Count > 0)
            {
                if (ctx.ForceRedownload)
                {
                    foreach (var z in existingZips) FormatUtils.TryDeleteFile(z.FullName);
                    if (existingDirs.Count > 0) { try { existingDirs[0].Delete(true); } catch { } }
                }
                else
                {
                    ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                    string cleanName = Regex.Replace(Path.GetFileNameWithoutExtension(existingZips[0].Name), @"^\[nHentai_\d+\]\s*", "[nHentai] ");
                    ctx.OnSuccess?.Invoke(cleanName, FormatUtils.FormatFileSize(existingZips[0].Length), existingZips[0].FullName);
                    return;
                }
            }

            ctx.OnStatusChanged?.Invoke("Fetching gallery info...");

            string apiUrl = $"https://nhentai.net/api/gallery/{galleryId}";
            HttpResponseMessage response = await _client.GetAsync(apiUrl, ctx.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    throw new Exception($"[Cloudflare Block] Vui lòng bấm nút 'Cookies', chọn nHentai và lưu lại để cập nhật!\nHTTP {(int)response.StatusCode}");
                }
                throw new Exception($"[API Error] HTTP {(int)response.StatusCode}");
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JObject data = JObject.Parse(jsonResponse);

            string mediaId = data["media_id"]?.ToString();
            string rawTitle = data["title"]?["english"]?.ToString() ?? data["title"]?["japanese"]?.ToString() ?? "Comic";

            string safeTitle = FormatUtils.SanitizeFileName(rawTitle, 120);
            string folderName = $"[nHentai_{galleryId}] {safeTitle}";
            string finalPath = Path.Combine(targetFolder, folderName);
            Directory.CreateDirectory(finalPath);

            JArray pages = (JArray)data["images"]?["pages"];
            if (pages == null || pages.Count == 0)
                throw new Exception("Không tìm thấy trang truyện nào.");

            int totalImages = pages.Count;
            long totalBytes = 0;
            int downloadedCount = 0;
            object lockObj = new object();

            ctx.OnStatusChanged?.Invoke($"Found {totalImages} pages. Starting download...");

            using (var semaphore = new SemaphoreSlim(8, 8))
            {
                var downloadTasks = new List<Task>();

                for (int i = 0; i < totalImages; i++)
                {
                    int index = i;
                    string extType = pages[index]["t"]?.ToString();
                    string ext = extType == "j" ? ".jpg" : (extType == "p" ? ".png" : ".gif");

                    string imgUrl = $"https://i.nhentai.net/galleries/{mediaId}/{index + 1}{ext}";
                    string fileName = $"{index + 1:D3}{ext}";
                    string filePath = Path.Combine(finalPath, fileName);
                    string tempFilePath = filePath + ".part";

                    downloadTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(ctx.Token);
                        try
                        {
                            ctx.Token.ThrowIfCancellationRequested();

                            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                            {
                                await Task.Delay(Random.Shared.Next(80, 200), ctx.Token);
                                using var imgResponse = await _client.GetAsync(imgUrl, HttpCompletionOption.ResponseHeadersRead, ctx.Token);
                                imgResponse.EnsureSuccessStatusCode();
                                using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                                {
                                    await imgResponse.Content.CopyToAsync(fs, ctx.Token);
                                }

                                File.Move(tempFilePath, filePath, true);
                                lock (lockObj) totalBytes += imgResponse.Content.Headers.ContentLength ?? new FileInfo(filePath).Length;
                            }
                            else
                            {
                                lock (lockObj) totalBytes += new FileInfo(filePath).Length;
                            }

                            int currentDone = Interlocked.Increment(ref downloadedCount);
                            ctx.OnStatusChanged?.Invoke($"Downloading: {currentDone}/{totalImages} pages");
                            ctx.OnProgressChanged?.Invoke($"{Math.Round((double)currentDone / totalImages * 100)}%", null);
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

            var downloadedFiles = Directory.GetFiles(finalPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (downloadedFiles.Length < totalImages)
            {
                throw new HttpRequestException($"Tải thiếu trang ({downloadedFiles.Length}/{totalImages}) do lỗi kết nối mạng, đang thử tải bù...");
            }

            ctx.OnStatusChanged?.Invoke("Packaging manga into .zip...");
            string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(finalPath, deleteSourceFolder: true), ctx.Token);
            if (File.Exists(zipPath)) totalBytes = new FileInfo(zipPath).Length;

            ctx.OnStatusChanged?.Invoke("Completed");
            ctx.OnSuccess?.Invoke($"[nHentai] {safeTitle}", FormatUtils.FormatFileSize(totalBytes), zipPath);
        }

        private void LoadCookiesFromFile()
        {
            try
            {
                string uaPath = Path.Combine(AppUtils.DataDir, "app_useragent.txt");
                if (File.Exists(uaPath) && _client != null)
                {
                    string realUa = File.ReadAllText(uaPath).Trim();
                    if (!string.IsNullOrEmpty(realUa))
                    {
                        lock (_initLock)
                        {
                            _client.DefaultRequestHeaders.Remove("User-Agent");
                            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", realUa);
                        }
                    }
                }

                string cookiePath = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                if (!File.Exists(cookiePath)) return;

                foreach (string line in File.ReadAllLines(cookiePath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 7)
                    {
                        string domain = parts[0].Trim();
                        string name = parts[5].Trim();
                        string value = parts[6].Trim();

                        if (domain.Contains("nhentai.net"))
                        {
                            if (domain.StartsWith(".")) domain = domain.Substring(1);
                            _cookieContainer.Add(new Cookie(name, value, "/", domain));
                        }
                    }
                }
            }
            catch { }
        }
    }
}