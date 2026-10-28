using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;

namespace Koxmix.Downloaders
{
    public class RedditDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();

        public bool CanHandle(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains("reddit.com") || url.Contains("redd.it");
        }

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            ctx.OnStatusChanged?.Invoke(ctx.IsMp3 ? "Processing Reddit Audio..." : "Fetching Reddit media...");
            Directory.CreateDirectory(targetFolder);

            string redditId = Regex.Match(ctx.Url, @"(?:comments/|redd\.it/|gallery/)(\w+)").Groups[1].Value;
            string subreddit = Regex.Match(ctx.Url, @"/r/(\w+)").Groups[1].Value;

            if (!string.IsNullOrEmpty(redditId))
            {
                var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*{redditId}*.*", SearchOption.TopDirectoryOnly).ToList();
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
                        string subPart = !string.IsNullOrEmpty(subreddit) ? $"Reddit_r-{subreddit}" : "Reddit";
                        string displayTitle = latest.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                            ? $"[{subPart}] Post {redditId}"
                            : latest.Name;
                        ctx.OnSuccess?.Invoke(displayTitle, FormatUtils.FormatFileSize(latest.Length), latest.FullName);
                        return;
                    }
                }
            }

            string tempFolder = Path.Combine(targetFolder, $"Temp_Reddit_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
            Directory.CreateDirectory(tempFolder);

            try
            {
                var progress = new Progress<string>(line =>
                {
                    if (!ctx.Token.IsCancellationRequested)
                        CliRunner.ParseYtDlpProgress(line, ctx);
                });

                string finalStatus = "Completed";
                bool fallbackToMedia = false;

                if (ctx.IsMp3)
                {
                    try
                    {
                        await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: true, ctx.Token, progress);
                    }
                    catch when (!ctx.Token.IsCancellationRequested)
                    {
                        fallbackToMedia = true;
                        try
                        {
                            foreach (FileInfo s in new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories))
                                FormatUtils.TryDeleteFile(s.FullName);
                        }
                        catch { }
                    }
                }

                if (!ctx.IsMp3 || fallbackToMedia)
                {
                    try
                    {
                        await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token);
                    }
                    catch when (!ctx.Token.IsCancellationRequested)
                    {
                        ctx.OnStatusChanged?.Invoke("Switching to Video Engine...");
                        await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: false, ctx.Token, progress);
                    }
                }

                ctx.Token.ThrowIfCancellationRequested();
                await Task.Delay(500, ctx.Token);

                List<FileInfo> newFiles = new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories).ToList();

                if (!string.IsNullOrEmpty(redditId))
                {
                    var matchedFiles = newFiles.Where(f => f.Name.Contains(redditId)).ToList();
                    if (matchedFiles.Count > 0) newFiles = matchedFiles;
                }

                if (newFiles.Count == 0)
                {
                    throw new Exception("Could not download media. The link might be a text-only post or marked 18+ (Cookies update required).");
                }

                string subPart = !string.IsNullOrEmpty(subreddit) ? $"Reddit_r-{subreddit}" : "Reddit";
                string idPart = !string.IsNullOrEmpty(redditId) ? redditId : DateTime.Now.ToString("yyyyMMdd_HHmmss");

                if (newFiles.Count == 1)
                {
                    FileInfo singleFile = newFiles[0];
                    string cleanName = $"{subPart}_{idPart}_1{singleFile.Extension}";
                    string newPath = Path.Combine(targetFolder, cleanName);
                    if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                    singleFile.MoveTo(newPath);

                    FileInfo finalFi = new FileInfo(newPath);
                    ctx.OnStatusChanged?.Invoke(finalStatus);
                    ctx.OnSuccess?.Invoke(finalFi.Name, FormatUtils.FormatFileSize(finalFi.Length), finalFi.FullName);
                    return;
                }

                string albumFolderName = FormatUtils.SanitizeFileName($"[{subPart}] Post_{idPart}", 100);
                string albumFolder = Path.Combine(targetFolder, albumFolderName);
                Directory.CreateDirectory(albumFolder);

                int noneCounter = 1;
                foreach (FileInfo file in newFiles.OrderBy(f => f.Name))
                {
                    try
                    {
                        string cleanName = $"{subPart}_{idPart}_{noneCounter++:D2}{file.Extension}";
                        string newPath = Path.Combine(albumFolder, cleanName);
                        if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                        file.MoveTo(newPath);
                    }
                    catch { }
                }

                ctx.OnStatusChanged?.Invoke("Packaging Reddit gallery into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(albumFolder, deleteSourceFolder: true), ctx.Token);
                long zipSize = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                string rTitle = !string.IsNullOrEmpty(redditId) ? $"[{subPart}] Post {redditId}" : $"[Reddit] {newFiles.Count} Media";
                ctx.OnStatusChanged?.Invoke(finalStatus);
                ctx.OnSuccess?.Invoke(rTitle, FormatUtils.FormatFileSize(zipSize), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
            }
        }
    }
}