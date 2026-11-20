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
    public class XDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();

        public bool CanHandle(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains("x.com") || url.Contains("twitter.com");
        }

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            ctx.OnStatusChanged?.Invoke(ctx.IsMp3 ? "Processing X Audio..." : "Fetching X media...");
            Directory.CreateDirectory(targetFolder);

            bool isSpace = ctx.Url.Contains("/spaces/");
            string tweetIdMatch = Regex.Match(ctx.Url, @"(?:status(?:es)?|spaces)/([0-9a-zA-Z]+)").Groups[1].Value;
            string usernameMatch = Regex.Match(ctx.Url, @"(?:x\.com|twitter\.com)/([^/?#]+)").Groups[1].Value;
            if (usernameMatch.Equals("i", StringComparison.OrdinalIgnoreCase) ||
                usernameMatch.Equals("explore", StringComparison.OrdinalIgnoreCase))
            {
                usernameMatch = "";
            }

            if (!string.IsNullOrEmpty(tweetIdMatch))
            {
                var allMatchedFiles = new DirectoryInfo(targetFolder).GetFiles($"*[{tweetIdMatch}]*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                FileInfo existingFile = ctx.IsMp3
                    ? allMatchedFiles.FirstOrDefault(f => f.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
                    : allMatchedFiles.FirstOrDefault(f => !f.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) && !f.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase));

                if (existingFile != null)
                {
                    if (ctx.ForceRedownload)
                    {
                        FormatUtils.TryDeleteFile(existingFile.FullName);
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        string cardTitle = existingFile.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(existingFile.Name)
                            : existingFile.Name;
                        ctx.OnSuccess?.Invoke(cardTitle, FormatUtils.FormatFileSize(existingFile.Length), existingFile.FullName);
                        return;
                    }
                }
            }

            string tempFolder = Path.Combine(targetFolder, $"Temp_X_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
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

                if (ctx.IsMp3 || isSpace)
                {
                    try
                    {
                        await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: ctx.IsMp3, ctx.Token, progress);
                    }
                    catch (Exception ex) when (!ctx.Token.IsCancellationRequested && !isSpace)
                    {
                        if (!ex.Message.Contains("unable to obtain file audio codec") &&
                            !ex.Message.Contains("Audio extraction failed") &&
                            !ex.Message.Contains("No video could be found") &&
                            !ex.Message.Contains("Requested format is not available"))
                        {
                            throw;
                        }
                        fallbackToMedia = true;
                        try
                        {
                            foreach (FileInfo s in new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories))
                                FormatUtils.TryDeleteFile(s.FullName);
                        }
                        catch { }
                    }
                }

                string metaUploader = null;
                string metaTitle = null;

                if ((!ctx.IsMp3 && !isSpace) || fallbackToMedia)
                {
                    bool needYtDlpFallback = false;
                    bool isDownloadingGallery = true;

                    _ = Task.Run(async () =>
                    {
                        int lastCount = 0;
                        while (isDownloadingGallery && !ctx.Token.IsCancellationRequested)
                        {
                            await Task.Delay(700);
                            if (Directory.Exists(tempFolder))
                            {
                                try
                                {
                                    int currentCount = Directory.GetFiles(tempFolder, "*.*", SearchOption.AllDirectories)
                                        .Count(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
                                    if (currentCount > lastCount)
                                    {
                                        ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount} media files...");
                                        lastCount = currentCount;
                                    }
                                }
                                catch { }
                            }
                        }
                    });

                    try
                    {
                        await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token);
                        (metaUploader, metaTitle) = FormatUtils.ExtractAndCleanupGalleryDlMetadata(tempFolder);

                        if (Directory.GetFiles(tempFolder, "*.*", SearchOption.AllDirectories).Length == 0)
                        {
                            needYtDlpFallback = true;
                        }
                    }
                    catch when (!ctx.Token.IsCancellationRequested)
                    {
                        FormatUtils.ExtractAndCleanupGalleryDlMetadata(tempFolder);
                        needYtDlpFallback = true;
                    }
                    finally
                    {
                        isDownloadingGallery = false;
                    }

                    if (needYtDlpFallback && !ctx.Token.IsCancellationRequested)
                    {
                        ctx.OnStatusChanged?.Invoke("Switching to Video/Gif Engine...");
                        try
                        {
                            await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: false, ctx.Token, progress);
                            finalStatus = fallbackToMedia ? "Completed (Missing Audio)" : "Completed";
                        }
                        catch (Exception ex3) when (!ctx.Token.IsCancellationRequested)
                        {
                            throw new Exception("X.com blocked the connection. Please click the 'Cookies' button, log in to X.com, and save to refresh your session.\nError details: " + ex3.Message);
                        }
                    }
                }

                ctx.Token.ThrowIfCancellationRequested();
                await Task.Delay(300, ctx.Token);

                List<FileInfo> newFiles = new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => !f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                                !f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (newFiles.Count == 0)
                {
                    throw new Exception("Could not download media. The link might be a text-only post or you haven't refreshed the X.com login cookies.");
                }

                string firstNoExt = Path.GetFileNameWithoutExtension(newFiles[0].Name);
                if (firstNoExt.Contains("__KX__"))
                {
                    string[] parts = firstNoExt.Split(new[] { "__KX__" }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        if (string.IsNullOrEmpty(metaUploader)) metaUploader = parts[0].Trim();
                        if (string.IsNullOrEmpty(metaTitle)) metaTitle = parts[1].Trim();
                    }
                }

                string finalUser = !string.IsNullOrWhiteSpace(metaUploader) ? metaUploader : usernameMatch;
                if (!string.IsNullOrWhiteSpace(finalUser) && !finalUser.StartsWith("@"))
                    finalUser = "@" + finalUser;

                string finalTitle = !string.IsNullOrWhiteSpace(metaTitle) ? metaTitle : "Tweet Media";
                string idPart = !string.IsNullOrEmpty(tweetIdMatch) ? tweetIdMatch : DateTime.Now.ToString("yyyyMMdd_HHmmss");

                if (newFiles.Count == 1)
                {
                    FileInfo singleFile = newFiles[0];
                    string cleanName = FormatUtils.BuildStandardName("X.com", finalUser, finalTitle, idPart, singleFile.Extension);
                    string newPath = Path.Combine(targetFolder, cleanName);
                    if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                    singleFile.MoveTo(newPath);

                    FileInfo finalFi = new FileInfo(newPath);
                    ctx.OnStatusChanged?.Invoke(finalStatus);
                    ctx.OnSuccess?.Invoke(finalFi.Name, FormatUtils.FormatFileSize(finalFi.Length), finalFi.FullName);
                    return;
                }

                string albumBaseName = FormatUtils.BuildStandardName("X.com", finalUser, finalTitle, idPart);
                string albumFolder = Path.Combine(targetFolder, albumBaseName);
                Directory.CreateDirectory(albumFolder);

                int noneCounter = 1;
                foreach (FileInfo file in newFiles.OrderBy(f => f.Name))
                {
                    try
                    {
                        string cleanName = $"{idPart}_{noneCounter++:D3}{file.Extension}";
                        string newPath = Path.Combine(albumFolder, cleanName);
                        if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                        file.MoveTo(newPath);
                    }
                    catch { }
                }

                ctx.OnStatusChanged?.Invoke("Packaging X album into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(albumFolder, deleteSourceFolder: true), ctx.Token);
                long zipSize = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                ctx.OnStatusChanged?.Invoke(finalStatus);
                ctx.OnSuccess?.Invoke(albumBaseName, FormatUtils.FormatFileSize(zipSize), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
            }
        }
    }
}