using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;

namespace Koxmix.Downloaders
{
    public class FacebookDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();

        public bool CanHandle(string url) => UrlUtils.IsFacebookAlbumOrPost(url);

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            Directory.CreateDirectory(targetFolder);

            string extractId = UrlUtils.ExtractFbPostId(ctx.Url);

            if (!string.IsNullOrEmpty(extractId) && !System.Text.RegularExpressions.Regex.IsMatch(extractId, @"^\d{8}_\d{6}$"))
            {
                var existingZips = new DirectoryInfo(targetFolder).GetFiles($"*{extractId}*.zip", SearchOption.TopDirectoryOnly).ToList();
                var existingDirs = new DirectoryInfo(targetFolder).GetDirectories($"*{extractId}*", SearchOption.TopDirectoryOnly).ToList();
                var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*{extractId}_*.*", SearchOption.AllDirectories).ToList();

                if (existingZips.Count > 0 || (existingDirs.Count > 0 && existingDirs[0].GetFiles().Length > 0) || existingFiles.Count > 0)
                {
                    if (ctx.ForceRedownload)
                    {
                        foreach (var z in existingZips) FormatUtils.TryDeleteFile(z.FullName);
                        if (existingDirs.Count > 0) { try { existingDirs[0].Delete(true); } catch { } }
                        foreach (var f in existingFiles) FormatUtils.TryDeleteFile(f.FullName);
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        if (existingZips.Count > 0)
                        {
                            ctx.OnSuccess?.Invoke($"[FB] {Path.GetFileNameWithoutExtension(existingZips[0].Name)}", FormatUtils.FormatFileSize(existingZips[0].Length), existingZips[0].FullName);
                        }
                        else if (existingDirs.Count > 0)
                        {
                            ctx.OnSuccess?.Invoke($"[FB] {existingDirs[0].Name}", $"{existingDirs[0].GetFiles().Length} Files", existingDirs[0].FullName);
                        }
                        else
                        {
                            var latest = existingFiles.OrderByDescending(f => f.LastWriteTime).First();
                            ctx.OnSuccess?.Invoke(latest.Name, FormatUtils.FormatFileSize(existingFiles.Sum(f => f.Length)), latest.FullName);
                        }
                        return;
                    }
                }
            }

            ctx.OnStatusChanged?.Invoke("Initializing connection to FB...");
            string tempFolder = Path.Combine(targetFolder, $"Temp_FB_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 5)}");
            Directory.CreateDirectory(tempFolder);

            try
            {
                bool isDownloading = true;
                _ = Task.Run(async () =>
                {
                    int lastCount = 0;
                    while (isDownloading && !ctx.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000);
                        if (Directory.Exists(tempFolder))
                        {
                            try
                            {
                                int currentCount = Directory.GetFiles(tempFolder, "*.*", SearchOption.AllDirectories).Length;
                                if (currentCount > lastCount)
                                {
                                    ctx.OnStatusChanged?.Invoke($"Downloading: {currentCount} photos...");
                                    lastCount = currentCount;
                                }
                            }
                            catch { }
                        }
                    }
                });

                bool downloadSuccess = false;
                Exception originalError = null;

                try
                {
                    await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token, useCookies: true);
                    downloadSuccess = true;
                }
                catch (Exception ex) when (!ctx.Token.IsCancellationRequested)
                {
                    originalError = ex;
                    ctx.OnStatusChanged?.Invoke("Trying guest mode...");
                    try
                    {
                        await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token, useCookies: false);
                        downloadSuccess = true;
                    }
                    catch when (!ctx.Token.IsCancellationRequested) { downloadSuccess = false; }
                }
                finally
                {
                    isDownloading = false;
                }

                ctx.Token.ThrowIfCancellationRequested();

                List<FileInfo> newFiles = Directory.Exists(tempFolder)
                    ? new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories).ToList()
                    : new List<FileInfo>();

                if (!downloadSuccess || newFiles.Count == 0)
                {
                    throw new Exception(originalError != null ? originalError.ToString() : "Gallery-dl output empty.");
                }
                if (newFiles.Count == 1)
                {
                    FileInfo singleFile = newFiles[0];
                    string singleName = $"FB_{extractId}_1{singleFile.Extension}";
                    string singlePath = Path.Combine(targetFolder, singleName);
                    if (File.Exists(singlePath)) FormatUtils.TryDeleteFile(singlePath);
                    singleFile.MoveTo(singlePath);

                    FileInfo finalFi = new FileInfo(singlePath);
                    ctx.OnStatusChanged?.Invoke("Completed");
                    ctx.OnSuccess?.Invoke(finalFi.Name, FormatUtils.FormatFileSize(finalFi.Length), finalFi.FullName);
                    return;
                }
                ctx.OnStatusChanged?.Invoke("Organizing files...");
                string captionFolder = FormatUtils.SanitizeFileName(newFiles[0].Directory.Name, 80);

                if (string.IsNullOrWhiteSpace(captionFolder) || captionFolder.StartsWith("Temp_FB", StringComparison.OrdinalIgnoreCase))
                    captionFolder = $"FB_{extractId}";

                if (!string.IsNullOrEmpty(extractId) && !captionFolder.Contains(extractId))
                    captionFolder = $"[{extractId}] {captionFolder}";

                string finalFolder = Path.Combine(targetFolder, captionFolder);
                Directory.CreateDirectory(finalFolder);

                int imgIndex = 1;
                foreach (FileInfo file in newFiles.OrderBy(f => f.Name))
                {
                    try
                    {
                        string newName = $"{extractId}_{imgIndex++:D3}{file.Extension}";
                        string newPath = Path.Combine(finalFolder, newName);
                        if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                        file.MoveTo(newPath);
                    }
                    catch { }
                }

                ctx.OnStatusChanged?.Invoke("Packaging album into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(finalFolder, deleteSourceFolder: true), ctx.Token);
                long zipSize = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                ctx.OnStatusChanged?.Invoke("Completed");
                ctx.OnSuccess?.Invoke($"[FB] {captionFolder}", FormatUtils.FormatFileSize(zipSize), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
            }
        }
    }
}