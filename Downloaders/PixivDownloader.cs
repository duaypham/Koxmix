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
    public class PixivDownloader : IDownloader
    {
        private static readonly CookieContainer _cookieContainer = new CookieContainer();
        private static HttpClientHandler _handler;
        private static HttpClient _client;
        private static readonly object _initLock = new object();

        public PixivDownloader()
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
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

                    _client.DefaultRequestHeaders.Add("User-Agent", realUa);
                    _client.DefaultRequestHeaders.Add("Referer", "https://www.pixiv.net/");
                    _client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
                }
            }
            LoadCookiesFromFile();
        }

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && url.Contains("pixiv.net");

        public async Task DownloadAsync(DownloadContext ctx)
        {
            LoadCookiesFromFile();
            string targetFolder = ctx.TargetFolder;
            Directory.CreateDirectory(targetFolder);

            string pixivId = Regex.Match(ctx.Url, @"(?:illust_id=|artworks/)(\d+)").Groups[1].Value;
            if (string.IsNullOrEmpty(pixivId))
                throw new ArgumentException("Could not find Pixiv ID in the URL.");

            var existingDirs = new DirectoryInfo(targetFolder).GetDirectories($"*({pixivId})*", SearchOption.TopDirectoryOnly).ToList();
            var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*{pixivId}*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (existingFiles.Count > 0)
            {
                if (ctx.ForceRedownload)
                {
                    if (existingDirs.Count > 0) { try { existingDirs[0].Delete(true); } catch { } }
                    foreach (var f in existingFiles) FormatUtils.TryDeleteFile(f.FullName);
                }
                else
                {
                    ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                    var latest = existingFiles.OrderByDescending(f => f.LastWriteTime).First();
                    string cleanTitle = ExtractCleanPixivTitle(latest.Name, pixivId);
                    ctx.OnSuccess?.Invoke(cleanTitle, FormatUtils.FormatFileSize(latest.Length), latest.FullName);
                    return;
                }
            }

            ctx.OnStatusChanged?.Invoke("Fetching Pixiv metadata...");

            string metaUrl = $"https://www.pixiv.net/ajax/illust/{pixivId}";
            HttpResponseMessage metaRes = await _client.GetAsync(metaUrl, ctx.Token);
            string metaJson = await metaRes.Content.ReadAsStringAsync();

            if (!metaRes.IsSuccessStatusCode || metaJson.Contains("\"error\":true"))
            {
                throw new Exception($"[Pixiv] Could not access ID {pixivId}.\nReason: Artwork deleted, or R-18 restriction. Please click 'Cookies', log in to Pixiv again, and save the new Cookie!");
            }

            JObject metaObj = JObject.Parse(metaJson);
            string rawTitle = metaObj["body"]?["illustTitle"]?.ToString() ?? "Artwork";
            string rawAuthor = metaObj["body"]?["userName"]?.ToString() ?? "Unknown";
            int illustType = metaObj["body"]?["illustType"]?.Value<int>() ?? 0;

            if (illustType == 2)
            {
                throw new NotSupportedException("[Pixiv] This artwork is an animated image (Ugoira). Koxmix currently only supports downloading static images (Illust) or manga (Manga).");
            }

            string safeTitle = FormatUtils.SanitizeFileName(rawTitle, 80);
            string safeAuthor = FormatUtils.SanitizeFileName(rawAuthor, 40);

            ctx.OnStatusChanged?.Invoke("Parsing pages...");
            string pagesUrl = $"https://www.pixiv.net/ajax/illust/{pixivId}/pages";
            HttpResponseMessage pagesRes = await _client.GetAsync(pagesUrl, ctx.Token);
            string pagesJson = await pagesRes.Content.ReadAsStringAsync();

            JObject data = JObject.Parse(pagesJson);
            JArray body = (JArray)data["body"];

            if (body == null || body.Count == 0)
                throw new Exception("No image data found.");

            List<string> imageUrls = body.Select(item => item["urls"]["original"].ToString()).ToList();

            bool isMultiple = imageUrls.Count > 1;
            string folderOrFileName = isMultiple
                ? $"[Pixiv] {safeTitle} - {safeAuthor} ({pixivId})"
                : $"Pixiv_{safeTitle}_{pixivId}";

            string finalPath = isMultiple ? Path.Combine(targetFolder, folderOrFileName) : targetFolder;
            Directory.CreateDirectory(finalPath);

            long totalBytes = 0;
            int totalImages = imageUrls.Count;
            int downloadedCount = 0;
            string sampleFilePath = "";
            object lockObj = new object();

            using (var semaphore = new SemaphoreSlim(8, 8))
            {
                var downloadTasks = new List<Task>();

                for (int i = 0; i < totalImages; i++)
                {
                    int index = i;
                    string imgUrl = imageUrls[index];
                    int currentIndex = index + 1;

                    string ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
                    string fileName = isMultiple ? $"{currentIndex:D3}{ext}" : $"{folderOrFileName}{ext}";
                    string filePath = Path.Combine(finalPath, fileName);
                    string tempFilePath = filePath + ".part";

                    if (index == 0) sampleFilePath = filePath;

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
                            ctx.OnStatusChanged?.Invoke($"Downloading: {currentDone}/{totalImages} images");
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
                    try { if (isMultiple && Directory.Exists(finalPath)) Directory.Delete(finalPath, true); } catch { }
                    throw;
                }
            }

            string finalOutput = isMultiple ? finalPath : sampleFilePath;
            if (isMultiple)
            {
                var downloadedFiles = Directory.GetFiles(finalPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (downloadedFiles.Length < totalImages)
                {
                    throw new HttpRequestException($"Missing Pixiv images ({downloadedFiles.Length}/{totalImages}) due to connection errors, retrying remaining...");
                }

                ctx.OnStatusChanged?.Invoke("Packaging album into .zip...");
                finalOutput = await Task.Run(() => FormatUtils.PackFolderToZip(finalPath, deleteSourceFolder: true), ctx.Token);
                if (File.Exists(finalOutput)) totalBytes = new FileInfo(finalOutput).Length;
            }
            else if (string.IsNullOrEmpty(sampleFilePath) || !File.Exists(sampleFilePath))
            {
                throw new HttpRequestException("Failed to download single Pixiv image due to connection error.");
            }

            string displayTitle = isMultiple ? $"[Pixiv] {safeTitle} - {safeAuthor}" : safeTitle;
            ctx.OnStatusChanged?.Invoke("Completed");
            ctx.OnSuccess?.Invoke(displayTitle, FormatUtils.FormatFileSize(totalBytes), finalOutput);
        }

        private static string ExtractCleanPixivTitle(string rawFileOrDirName, string pixivId)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(rawFileOrDirName);
            string idSuffix = $" ({pixivId})";
            if (nameNoExt.EndsWith(idSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return nameNoExt.Substring(0, nameNoExt.Length - idSuffix.Length);
            }
            string singlePrefix = "Pixiv_";
            string singleSuffix = $"_{pixivId}";
            if (nameNoExt.StartsWith(singlePrefix, StringComparison.OrdinalIgnoreCase) &&
                nameNoExt.EndsWith(singleSuffix, StringComparison.OrdinalIgnoreCase) &&
                nameNoExt.Length > singlePrefix.Length + singleSuffix.Length)
            {
                return nameNoExt.Substring(singlePrefix.Length, nameNoExt.Length - singlePrefix.Length - singleSuffix.Length);
            }
            return nameNoExt;
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

                        if (domain.Contains("pixiv.net"))
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