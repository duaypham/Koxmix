using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        private static CookieContainer _cookieContainer = new CookieContainer();
        private static HttpClientHandler _handler;
        private static HttpClient _client;
        private static readonly object _initLock = new object();

        public PixivDownloader()
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
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ja;q=0.8");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.pixiv.net/");
                _client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                try
                {
                    string cookiePath = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                    if (!File.Exists(cookiePath)) return;

                    string extractedUserId = null;
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
                                string cleanDomain = domain.StartsWith(".") ? domain.Substring(1) : domain;
                                try { _cookieContainer.Add(new Cookie(name, value, "/", cleanDomain)); } catch { }

                                if (name == "PHPSESSID" && string.IsNullOrEmpty(extractedUserId))
                                {
                                    var uidMatch = Regex.Match(value, @"^(\d+)");
                                    if (uidMatch.Success) extractedUserId = uidMatch.Groups[1].Value;
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(extractedUserId))
                    {
                        _client.DefaultRequestHeaders.Remove("X-User-Id");
                        _client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", extractedUserId);
                    }
                }
                catch { }
            }
        }

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && (url.Contains("pixiv.net") || url.Contains("pixiv.me"));

        public async Task DownloadAsync(DownloadContext ctx)
        {
            ReloadClientAndCookies();
            string targetFolder = ctx.TargetFolder;
            Directory.CreateDirectory(targetFolder);

            string pixivId = Regex.Match(ctx.Url, @"(?:illust_id=|artworks/)(\d+)").Groups[1].Value;
            if (string.IsNullOrEmpty(pixivId))
                throw new ArgumentException("Could not find Pixiv Artwork ID in the URL.");

            var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*[{pixivId}]*.*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (existingFiles.Count > 0)
            {
                if (ctx.ForceRedownload)
                {
                    foreach (var f in existingFiles) FormatUtils.TryDeleteFile(f.FullName);
                }
                else
                {
                    ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                    var latest = existingFiles.OrderByDescending(f => f.LastWriteTime).First();
                    string cardTitle = latest.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(latest.Name)
                        : latest.Name;
                    ctx.OnSuccess?.Invoke(cardTitle, FormatUtils.FormatFileSize(latest.Length), latest.FullName);
                    return;
                }
            }

            ctx.OnStatusChanged?.Invoke("Fetching Pixiv metadata...");

            string metaUrl = $"https://www.pixiv.net/ajax/illust/{pixivId}";
            using HttpResponseMessage metaRes = await _client.GetAsync(metaUrl, ctx.Token);
            string metaJson = await metaRes.Content.ReadAsStringAsync(ctx.Token);

            if (!metaRes.IsSuccessStatusCode || metaJson.Contains("\"error\":true"))
            {
                throw new Exception($"[Pixiv] Could not access ID {pixivId}.\nReason: Artwork deleted, or R-18/Login restriction. Please click 'Cookies', log in to Pixiv, and click Save!");
            }

            JObject metaObj = JObject.Parse(metaJson);
            JToken bodyToken = metaObj["body"];
            int xRestrict = bodyToken?["xRestrict"]?.Value<int>() ?? 0;
            bool isNotLoggedIn = bodyToken?["noLoginData"] != null;

            if (xRestrict > 0 && isNotLoggedIn)
            {
                throw new Exception($"[Pixiv R-18] Artwork {pixivId} requires an active Pixiv login session. Please click 'Cookies', log in to Pixiv, and save!");
            }

            string rawTitle = bodyToken?["illustTitle"]?.ToString() ?? "Artwork";
            string rawAuthor = bodyToken?["userName"]?.ToString() ?? "Unknown";
            int illustType = bodyToken?["illustType"]?.Value<int>() ?? 0;

            if (illustType == 2)
            {
                await DownloadAndConvertUgoiraAsync(ctx, pixivId, rawTitle, rawAuthor, targetFolder);
                return;
            }

            ctx.OnStatusChanged?.Invoke("Parsing pages...");
            string pagesUrl = $"https://www.pixiv.net/ajax/illust/{pixivId}/pages";
            using HttpResponseMessage pagesRes = await _client.GetAsync(pagesUrl, ctx.Token);
            string pagesJson = await pagesRes.Content.ReadAsStringAsync(ctx.Token);

            JObject data = JObject.Parse(pagesJson);
            JArray body = data["body"] as JArray;

            if (body == null || body.Count == 0)
                throw new Exception("No image data found.");

            List<string> imageUrls = body.Select(item => item["urls"]?["original"]?.ToString())
                                         .Where(u => !string.IsNullOrEmpty(u))
                                         .ToList();

            bool isMultiple = imageUrls.Count > 1;
            string standardBaseName = FormatUtils.BuildStandardName("Pixiv", rawAuthor, rawTitle, pixivId);
            string finalPath = isMultiple ? Path.Combine(targetFolder, standardBaseName) : targetFolder;
            Directory.CreateDirectory(finalPath);

            long totalBytes = 0;
            int totalImages = imageUrls.Count;
            int downloadedCount = 0;
            string sampleFilePath = "";
            object lockObj = new object();
            string artworkReferer = $"https://www.pixiv.net/en/artworks/{pixivId}";

            using (var semaphore = new SemaphoreSlim(8, 8))
            {
                var downloadTasks = new List<Task>();

                for (int i = 0; i < totalImages; i++)
                {
                    int index = i;
                    string imgUrl = imageUrls[index];
                    int currentIndex = index + 1;

                    string ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
                    string fileName = isMultiple ? $"{currentIndex:D3}{ext}" : $"{standardBaseName}{ext}";
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
                                for (int retry = 0; retry < 3; retry++)
                                {
                                    try
                                    {
                                        using var req = new HttpRequestMessage(HttpMethod.Get, imgUrl);
                                        req.Headers.Referrer = new Uri(artworkReferer);

                                        using var imgResponse = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.Token);
                                        imgResponse.EnsureSuccessStatusCode();

                                        using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                                        {
                                            await imgResponse.Content.CopyToAsync(fs, ctx.Token);
                                        }

                                        File.Move(tempFilePath, filePath, true);
                                        lock (lockObj) totalBytes += imgResponse.Content.Headers.ContentLength ?? new FileInfo(filePath).Length;
                                        break;
                                    }
                                    catch (OperationCanceledException) { throw; }
                                    catch
                                    {
                                        if (retry == 2) throw;
                                        await Task.Delay(800 * (retry + 1), ctx.Token);
                                    }
                                }
                            }
                            else
                            {
                                lock (lockObj) totalBytes += new FileInfo(filePath).Length;
                            }

                            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                            {
                                int currentDone = Interlocked.Increment(ref downloadedCount);
                                ctx.OnStatusChanged?.Invoke($"Downloading: {currentDone}/{totalImages} images");
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
                    try { if (isMultiple && Directory.Exists(finalPath)) Directory.Delete(finalPath, true); } catch { }
                    throw;
                }
            }

            string finalOutput;
            string displayTitle;

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
                displayTitle = standardBaseName;
            }
            else
            {
                if (string.IsNullOrEmpty(sampleFilePath) || !File.Exists(sampleFilePath))
                {
                    throw new HttpRequestException("Failed to download single Pixiv image due to connection error.");
                }
                finalOutput = sampleFilePath;
                displayTitle = Path.GetFileName(sampleFilePath);
            }

            ctx.OnStatusChanged?.Invoke("Completed");
            ctx.OnSuccess?.Invoke(displayTitle, FormatUtils.FormatFileSize(totalBytes), finalOutput);
        }

        private async Task DownloadAndConvertUgoiraAsync(DownloadContext ctx, string pixivId, string rawTitle, string rawAuthor, string targetFolder)
        {
            ctx.OnStatusChanged?.Invoke("Fetching Ugoira animation metadata...");
            string ugoiraMetaUrl = $"https://www.pixiv.net/ajax/illust/{pixivId}/ugoira_meta";
            using HttpResponseMessage metaRes = await _client.GetAsync(ugoiraMetaUrl, ctx.Token);
            string metaJson = await metaRes.Content.ReadAsStringAsync(ctx.Token);

            JObject ugoiraObj = JObject.Parse(metaJson);
            string originalSrc = ugoiraObj["body"]?["originalSrc"]?.ToString();
            JArray frames = ugoiraObj["body"]?["frames"] as JArray;

            if (string.IsNullOrEmpty(originalSrc) || frames == null || frames.Count == 0)
                throw new Exception("[Pixiv Ugoira] Could not retrieve animation frames. Please refresh Pixiv cookies.");

            string tempUgoiraDir = Path.Combine(targetFolder, $"Temp_Ugoira_{pixivId}_{Guid.NewGuid():N}");
            string tempZipPath = Path.Combine(tempUgoiraDir, "ugoira.zip");
            Directory.CreateDirectory(tempUgoiraDir);

            try
            {
                ctx.OnStatusChanged?.Invoke("Downloading Ugoira frames...");
                using var req = new HttpRequestMessage(HttpMethod.Get, originalSrc);
                req.Headers.Referrer = new Uri($"https://www.pixiv.net/en/artworks/{pixivId}");

                using var zipRes = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.Token);
                zipRes.EnsureSuccessStatusCode();

                using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await zipRes.Content.CopyToAsync(fs, ctx.Token);
                }

                ZipFile.ExtractToDirectory(tempZipPath, tempUgoiraDir, overwriteFiles: true);
                File.Delete(tempZipPath);

                string ffmpegExe = Path.Combine(AppUtils.DataDir, "ffmpeg.exe");
                string standardGifName = FormatUtils.BuildStandardName("Pixiv", rawAuthor, rawTitle, pixivId, ".gif");
                string outputGifPath = Path.Combine(targetFolder, standardGifName);

                if (File.Exists(ffmpegExe))
                {
                    ctx.OnStatusChanged?.Invoke("Converting Ugoira to animated GIF via FFmpeg...");
                    var sb = new StringBuilder();
                    string lastFile = null;

                    foreach (var frame in frames)
                    {
                        string file = frame["file"]?.ToString();
                        int delayMs = frame["delay"]?.Value<int>() ?? 100;
                        if (string.IsNullOrEmpty(file)) continue;

                        double durationSec = Math.Max(0.02, delayMs / 1000.0);
                        sb.AppendLine($"file '{file}'");
                        sb.AppendLine($"duration {durationSec.ToString("0.###", CultureInfo.InvariantCulture)}");
                        lastFile = file;
                    }

                    if (!string.IsNullOrEmpty(lastFile))
                    {
                        sb.AppendLine($"file '{lastFile}'");
                    }

                    string concatFile = Path.Combine(tempUgoiraDir, "ffconcat.txt");
                    File.WriteAllText(concatFile, sb.ToString(), new UTF8Encoding(false));

                    string args = $"-y -f concat -safe 0 -i \"{concatFile}\" -vf \"split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse=dither=bayer\" \"{outputGifPath}\"";
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = ffmpegExe,
                        Arguments = args,
                        WorkingDirectory = tempUgoiraDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (proc != null)
                    {
                        await proc.WaitForExitAsync(ctx.Token);
                    }
                }

                if (File.Exists(outputGifPath) && new FileInfo(outputGifPath).Length > 0)
                {
                    long gifBytes = new FileInfo(outputGifPath).Length;
                    ctx.OnStatusChanged?.Invoke("Completed");
                    ctx.OnSuccess?.Invoke(standardGifName, FormatUtils.FormatFileSize(gifBytes), outputGifPath);
                    return;
                }

                string animJsonPath = Path.Combine(tempUgoiraDir, "animation.json");
                File.WriteAllText(animJsonPath, frames.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8);

                string standardZipName = FormatUtils.BuildStandardName("Pixiv", rawAuthor, rawTitle, pixivId, ".zip");
                string fallbackZip = Path.Combine(targetFolder, standardZipName);
                if (File.Exists(fallbackZip)) FormatUtils.TryDeleteFile(fallbackZip);
                ZipFile.CreateFromDirectory(tempUgoiraDir, fallbackZip, CompressionLevel.Fastest, false);

                long zipBytes = new FileInfo(fallbackZip).Length;
                ctx.OnStatusChanged?.Invoke("Completed");
                ctx.OnSuccess?.Invoke(Path.GetFileNameWithoutExtension(standardZipName), FormatUtils.FormatFileSize(zipBytes), fallbackZip);
            }
            finally
            {
                try { if (Directory.Exists(tempUgoiraDir)) Directory.Delete(tempUgoiraDir, true); } catch { }
            }
        }
    }
}