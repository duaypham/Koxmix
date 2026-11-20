using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;
using Newtonsoft.Json.Linq;

namespace Koxmix.Downloaders
{
    public class HitomiDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();
        private static readonly HttpClient _metaClient = CreateMetaClient();

        private static HttpClient CreateMetaClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://hitomi.la/");
            return client;
        }

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && url.Contains("hitomi.la");

        private static string FixHitomiUrl(string url, out string extractId)
        {
            if (url.Contains("/lofi/")) url = url.Replace("/lofi/", "/");
            int qIdx = url.IndexOf('?');
            if (qIdx > 0) url = url.Substring(0, qIdx);
            int hashIdx = url.IndexOf('#');
            if (hashIdx > 0) url = url.Substring(0, hashIdx);

            extractId = Regex.Match(url, @"(\d{5,})(?:\.html|/|$)").Groups[1].Value;
            if (string.IsNullOrEmpty(extractId))
            {
                extractId = Regex.Match(url, @"(\d+)").Groups[1].Value;
            }

            return !string.IsNullOrEmpty(extractId)
                ? $"https://hitomi.la/galleries/{extractId}.html"
                : url;
        }

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            string cleanUrl = FixHitomiUrl(ctx.Url, out string extractId);
            if (string.IsNullOrEmpty(extractId))
                throw new ArgumentException("Could not find Hitomi ID in the URL.");

            Directory.CreateDirectory(targetFolder);
            var existingZips = new DirectoryInfo(targetFolder).GetFiles($"*[{extractId}]*.zip").ToList();

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

            ctx.OnStatusChanged?.Invoke("Fetching Hitomi metadata...");
            string title = null;
            string artist = null;
            string infoSummary = null;
            int totalExpectedPages = 0;

            string[] jsDomains = { "https://ltn.gold-usergeneratedcontent.net", "https://ltn.hitomi.la" };
            foreach (string domain in jsDomains)
            {
                try
                {
                    string jsData = await _metaClient.GetStringAsync($"{domain}/galleries/{extractId}.js", ctx.Token);
                    int eqIdx = jsData.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        string jsonPart = jsData.Substring(eqIdx + 1).Trim().TrimEnd(';');
                        JObject galObj = JObject.Parse(jsonPart);

                        title = galObj["title"]?.ToString();
                        string language = galObj["language_localname"]?.ToString() ?? galObj["language"]?.ToString() ?? "N/A";
                        string galType = galObj["type"]?.ToString() ?? "manga";

                        if (galObj["files"] is JArray filesArr)
                        {
                            totalExpectedPages = filesArr.Count;
                        }

                        var artists = galObj["artists"] is JArray artArr
                            ? artArr.Select(a => a["artist"]?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList()
                            : new List<string>();

                        var groups = galObj["groups"] is JArray grpArr
                            ? grpArr.Select(g => g["group"]?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList()
                            : new List<string>();

                        artist = artists.Count > 0
                            ? string.Join(", ", artists.Take(2))
                            : (groups.Count > 0 ? string.Join(", ", groups.Take(2)) : null);

                        var sbInfo = new StringBuilder();
                        sbInfo.AppendLine($"Gallery ID: {extractId}");
                        sbInfo.AppendLine($"Title: {title}");
                        sbInfo.AppendLine($"Artist: {(artists.Count > 0 ? string.Join(", ", artists) : "N/A")}");
                        sbInfo.AppendLine($"Group: {(groups.Count > 0 ? string.Join(", ", groups) : "N/A")}");
                        sbInfo.AppendLine($"Type: {galType}");
                        sbInfo.AppendLine($"Language: {language}");
                        infoSummary = sbInfo.ToString();
                        break;
                    }
                }
                catch { }
            }

            string tempPath = Path.Combine(targetFolder, $"Temp_Hitomi_{extractId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);

            try
            {
                ctx.OnStatusChanged?.Invoke("Downloading pages via gallery-dl...");
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
                                    if (totalExpectedPages > 0)
                                    {
                                        ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount}/{totalExpectedPages} pages...");
                                        ctx.OnProgressChanged?.Invoke($"{Math.Min(99, Math.Round((double)currentCount / totalExpectedPages * 100))}%", null);
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
                    await _cliRunner.DownloadGalleryAsync(cleanUrl, tempPath, ctx.Token, useCookies: true);
                }
                catch when (!ctx.Token.IsCancellationRequested)
                {
                    ctx.OnStatusChanged?.Invoke("Trying guest mode...");
                    await _cliRunner.DownloadGalleryAsync(cleanUrl, tempPath, ctx.Token, useCookies: false);
                }
                finally
                {
                    isDownloading = false;
                }

                ctx.Token.ThrowIfCancellationRequested();

                var (metaArtist, metaTitle) = FormatUtils.ExtractAndCleanupGalleryDlMetadata(tempPath);
                if (string.IsNullOrWhiteSpace(artist)) artist = metaArtist;
                if (string.IsNullOrWhiteSpace(title)) title = metaTitle;

                var downloadedFiles = new DirectoryInfo(tempPath).GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => !f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                                !f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name)
                    .ToArray();

                if (downloadedFiles.Length == 0)
                {
                    throw new Exception("No images could be downloaded. Hitomi may have changed its algorithm; please restart the application to auto-update gallery-dl.");
                }

                string folderName = FormatUtils.BuildStandardName("Hitomi", artist, title ?? $"Gallery {extractId}", extractId);
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

                if (!string.IsNullOrEmpty(infoSummary))
                {
                    try { File.WriteAllText(Path.Combine(finalPath, "info.txt"), infoSummary, Encoding.UTF8); } catch { }
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