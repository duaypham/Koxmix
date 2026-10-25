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

            string tweetIdMatch = Regex.Match(ctx.Url, @"status(?:es)?/(\d+)").Groups[1].Value;
            string usernameMatch = Regex.Match(ctx.Url, @"(?:x\.com|twitter\.com)/([^/]+)").Groups[1].Value;
            if (usernameMatch.Equals("i", StringComparison.OrdinalIgnoreCase) ||
                usernameMatch.Equals("explore", StringComparison.OrdinalIgnoreCase))
            {
                usernameMatch = "";
            }

            if (!string.IsNullOrEmpty(tweetIdMatch))
            {
                var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*{tweetIdMatch}*.*", SearchOption.TopDirectoryOnly).ToList();
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
                        string displayTitle = latest.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                            ? (!string.IsNullOrEmpty(usernameMatch) ? $"[X.com] @{usernameMatch} - Tweet {tweetIdMatch}" : Path.GetFileNameWithoutExtension(latest.Name))
                            : latest.Name;
                        ctx.OnSuccess?.Invoke(displayTitle, FormatUtils.FormatFileSize(latest.Length), latest.FullName);
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

                if (ctx.IsMp3)
                {
                    try
                    {
                        await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: true, ctx.Token, progress);
                    }
                    catch (Exception ex)
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

                        targetFolder = ctx.TargetFolder;
                        Directory.CreateDirectory(targetFolder);
                    }
                }

                if (!ctx.IsMp3 || fallbackToMedia)
                {
                    try
                    {
                        await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token);
                    }
                    catch
                    {
                        ctx.OnStatusChanged?.Invoke("Switching to Video/Gif Engine...");
                        try
                        {
                            await _cliRunner.DownloadMediaViaYtDlpCliAsync(ctx.Url, tempFolder, extractMp3: false, ctx.Token, progress);
                            finalStatus = fallbackToMedia ? "Completed (Missing Images)" : "Completed";
                        }
                        catch (Exception ex3)
                        {
                            throw new Exception("X.com đã chặn kết nối. Vui lòng bấm nút 'Cookies', đăng nhập vào X.com và lưu lại để phần mềm lấy giấy thông hành.\nChi tiết lỗi: " + ex3.Message);
                        }
                    }
                }

                ctx.Token.ThrowIfCancellationRequested();
                await Task.Delay(500, ctx.Token);

                List<FileInfo> newFiles = new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories).ToList();

                if (!string.IsNullOrEmpty(tweetIdMatch))
                {
                    List<FileInfo> matchedFiles = newFiles.Where(f => f.Name.Contains(tweetIdMatch)).ToList();
                    if (matchedFiles.Count > 0) newFiles = matchedFiles;
                }

                if (newFiles.Count == 0)
                {
                    throw new Exception("Không tải được dữ liệu. Link có thể chỉ chứa Text, hoặc bạn chưa làm mới Cookies đăng nhập X.com.");
                }

                string prefixName = !string.IsNullOrEmpty(usernameMatch) ? $"X_{usernameMatch}" : "X_Media";
                string idPart = !string.IsNullOrEmpty(tweetIdMatch) ? tweetIdMatch : DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // Nếu Tweet chỉ có 1 file -> Lưu trực tiếp ra thư mục đích
                if (newFiles.Count == 1)
                {
                    FileInfo singleFile = newFiles[0];
                    string cleanName = $"{prefixName}_{idPart}_1{singleFile.Extension}";
                    string newPath = Path.Combine(targetFolder, cleanName);
                    if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                    singleFile.MoveTo(newPath);

                    FileInfo finalFi = new FileInfo(newPath);
                    ctx.OnStatusChanged?.Invoke(finalStatus);
                    ctx.OnSuccess?.Invoke(finalFi.Name, FormatUtils.FormatFileSize(finalFi.Length), finalFi.FullName);
                    return;
                }

                // Nếu Tweet có từ 2 file trở lên -> Đóng gói thành .zip
                string albumFolderName = FormatUtils.SanitizeFileName($"[X] {prefixName}_{idPart}", 100);
                string albumFolder = Path.Combine(targetFolder, albumFolderName);
                Directory.CreateDirectory(albumFolder);

                int noneCounter = 1;
                foreach (FileInfo file in newFiles.OrderBy(f => f.Name))
                {
                    try
                    {
                        string cleanName = $"{prefixName}_{idPart}_{noneCounter++:D2}{file.Extension}";
                        string newPath = Path.Combine(albumFolder, cleanName);
                        if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                        file.MoveTo(newPath);
                    }
                    catch { }
                }

                ctx.OnStatusChanged?.Invoke("Packaging X album into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(albumFolder, deleteSourceFolder: true), ctx.Token);
                long zipSize = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                string xTitle = !string.IsNullOrEmpty(tweetIdMatch)
                    ? $"[X.com] @{usernameMatch} - Tweet {tweetIdMatch}"
                    : $"[X.com] @{usernameMatch} - {newFiles.Count} Media";

                ctx.OnStatusChanged?.Invoke(finalStatus);
                ctx.OnSuccess?.Invoke(xTitle, FormatUtils.FormatFileSize(zipSize), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
            }
        }
    }
}