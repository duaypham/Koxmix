using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;

namespace Koxmix.Downloaders
{
    public class HitomiDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url) && url.Contains("hitomi.la");

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            string extractId = Regex.Match(ctx.Url, @"(?:galleries/|cg/|manga/|doujinshi/)[^/]*?(\d+)(?:\.html|/|$)").Groups[1].Value;
            if (string.IsNullOrEmpty(extractId))
                throw new ArgumentException("Không tìm thấy ID của Hitomi trong URL.");

            Directory.CreateDirectory(targetFolder);
            var existingZips = new DirectoryInfo(targetFolder).GetFiles($"*[Hitomi_{extractId}]*.zip").ToList();
            var existingDirs = new DirectoryInfo(targetFolder).GetDirectories($"*[Hitomi_{extractId}]*").ToList();

            if (existingZips.Count > 0 || (existingDirs.Count > 0 && existingDirs[0].GetFiles().Length > 0))
            {
                if (ctx.ForceRedownload)
                {
                    foreach (var z in existingZips) FormatUtils.TryDeleteFile(z.FullName);
                    if (existingDirs.Count > 0) { try { existingDirs[0].Delete(true); } catch { } }
                }
                else
                {
                    ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                    if (existingZips.Count > 0)
                        ctx.OnSuccess?.Invoke(Path.GetFileNameWithoutExtension(existingZips[0].Name), FormatUtils.FormatFileSize(existingZips[0].Length), existingZips[0].FullName);
                    else
                        ctx.OnSuccess?.Invoke(existingDirs[0].Name, $"{existingDirs[0].GetFiles().Length} Files", existingDirs[0].FullName);
                    return;
                }
            }

            ctx.OnStatusChanged?.Invoke("Fetching Hitomi metadata...");
            string title = "Comic";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.Add("Referer", "https://hitomi.la/");

                string jsData = await client.GetStringAsync($"https://ltn.hitomi.la/galleries/{extractId}.js", ctx.Token);
                var titleMatch = Regex.Match(jsData, @"\""title\""\s*:\s*\""(.*?)\""");
                if (titleMatch.Success)
                {
                    title = Regex.Unescape(titleMatch.Groups[1].Value);
                }
            }
            catch { }

            string safeTitle = FormatUtils.SanitizeFileName(title, 120);
            string folderName = $"[Hitomi_{extractId}] {safeTitle}";
            string finalPath = Path.Combine(targetFolder, folderName);
            Directory.CreateDirectory(finalPath);

            ctx.OnStatusChanged?.Invoke("Downloading pages via gallery-dl...");
            bool isDownloading = true;

            _ = Task.Run(async () =>
            {
                int lastCount = 0;
                while (isDownloading && !ctx.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    if (Directory.Exists(finalPath))
                    {
                        try
                        {
                            int currentCount = Directory.GetFiles(finalPath, "*.*", SearchOption.TopDirectoryOnly).Length;
                            if (currentCount > lastCount)
                            {
                                ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount} pages...");
                                lastCount = currentCount;
                            }
                        }
                        catch { }
                    }
                }
            });

            try
            {
                await _cliRunner.DownloadGalleryAsync(ctx.Url, finalPath, ctx.Token, useCookies: true);
            }
            catch
            {
                ctx.OnStatusChanged?.Invoke("Trying guest mode...");
                await _cliRunner.DownloadGalleryAsync(ctx.Url, finalPath, ctx.Token, useCookies: false);
            }
            finally
            {
                isDownloading = false;
            }

            ctx.Token.ThrowIfCancellationRequested();

            var downloadedFiles = new DirectoryInfo(finalPath).GetFiles("*.*", SearchOption.TopDirectoryOnly);
            if (downloadedFiles.Length == 0)
            {
                try { Directory.Delete(finalPath, true); } catch { }
                throw new Exception("Không tải được ảnh nào. Có thể Hitomi đã thay đổi thuật toán, hãy thử khởi động lại ứng dụng để tự cập nhật gallery-dl.");
            }

            ctx.OnStatusChanged?.Invoke("Packaging manga into .zip...");
            string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(finalPath, deleteSourceFolder: true), ctx.Token);
            long totalBytes = File.Exists(zipPath) ? new FileInfo(zipPath).Length : downloadedFiles.Sum(f => f.Length);

            ctx.OnStatusChanged?.Invoke("Completed");
            ctx.OnSuccess?.Invoke(folderName, FormatUtils.FormatFileSize(totalBytes), zipPath);
        }
    }
}