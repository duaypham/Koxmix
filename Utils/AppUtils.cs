using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace Koxmix.Utils
{
    public static class AppUtils
    {
        public static readonly string DataDir = InitDataDir();
        private static readonly string BaseDir = DataDir;

        private static string InitDataDir()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Koxmix_Data");
            var di = Directory.CreateDirectory(dir);
            try { di.Attributes |= FileAttributes.Hidden; } catch { }
            return dir;
        }
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        static AppUtils()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Koxmix-Downloader-Updater/1.0");
        }

        public static async Task EnsureAndUpdateAllToolsAsync(Action<string> onStatus)
        {
            await EnsureAndUpdateYtDlpAsync(onStatus);
            await EnsureAndUpdateGalleryDlAsync(onStatus);
            await EnsureAndUpdateFFmpegAsync(onStatus);
            await EnsureAndUpdateDenoAsync(onStatus);
        }

        public static async Task EnsureAndUpdateDenoAsync(Action<string> onStatus)
        {
            string denoPath = Path.Combine(BaseDir, "deno.exe");
            if (File.Exists(denoPath)) return;

            const string denoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), $"koxmix_deno_{Guid.NewGuid():N}.zip");

            try
            {
                await DownloadFileWithProgressAsync(denoZipUrl, tempZipPath, "Downloading Deno JS Runtime", onStatus);
                onStatus?.Invoke("Extracting deno.exe...");

                await Task.Run(() =>
                {
                    using ZipArchive archive = ZipFile.OpenRead(tempZipPath);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(denoPath, overwrite: true);
                            break;
                        }
                    }
                });
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            }
        }

        public static async Task EnsureAndUpdateYtDlpAsync(Action<string> onStatus)
        {
            string ytdlpPath = Path.Combine(BaseDir, "yt-dlp.exe");
            const string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

            try
            {
                if (!File.Exists(ytdlpPath))
                {
                    onStatus?.Invoke("Downloading yt-dlp.exe...");
                    await DownloadFileWithProgressAsync(downloadUrl, ytdlpPath, "yt-dlp.exe", onStatus);
                }
                else
                {
                    onStatus?.Invoke("Checking updates for yt-dlp...");
                    await RunSelfUpdateProcessAsync(ytdlpPath, "-U");
                }
            }
            catch
            {
                // Fallback sang thư viện YoutubeDLSharp nếu GitHub bị nghẽn
                if (!File.Exists(ytdlpPath))
                {
                    try { await YoutubeDLSharp.Utils.DownloadYtDlp(BaseDir); } catch { }
                }
            }
        }

        public static async Task EnsureAndUpdateGalleryDlAsync(Action<string> onStatus)
        {
            string galleryDlPath = Path.Combine(BaseDir, "gallery-dl.exe");
            const string downloadUrl = "https://github.com/mikf/gallery-dl/releases/latest/download/gallery-dl.exe";

            try
            {
                if (!File.Exists(galleryDlPath))
                {
                    onStatus?.Invoke("Downloading gallery-dl.exe...");
                    await DownloadFileWithProgressAsync(downloadUrl, galleryDlPath, "gallery-dl.exe", onStatus);
                }
                else
                {
                    onStatus?.Invoke("Checking updates for gallery-dl...");
                    await RunSelfUpdateProcessAsync(galleryDlPath, "-U");
                }
            }
            catch { }
        }

        public static async Task EnsureAndUpdateFFmpegAsync(Action<string> onStatus)
        {
            string ffmpegPath = Path.Combine(BaseDir, "ffmpeg.exe");
            string ffprobePath = Path.Combine(BaseDir, "ffprobe.exe");

            bool isMissing = !File.Exists(ffmpegPath) || !File.Exists(ffprobePath);
            bool isOutdated = !isMissing && (DateTime.Now - File.GetLastWriteTime(ffmpegPath)).TotalDays > 30;

            if (!isMissing && !isOutdated) return;

            // Kho FFmpeg chính thức do đội ngũ yt-dlp biên dịch, tối ưu riêng cho việc nhúng Subtitles & Chapters
            const string ffmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), $"koxmix_ffmpeg_{Guid.NewGuid():N}.zip");

            try
            {
                string actionText = isMissing ? "Downloading FFmpeg & FFprobe" : "Updating FFmpeg & FFprobe";
                await DownloadFileWithProgressAsync(ffmpegZipUrl, tempZipPath, actionText, onStatus);

                onStatus?.Invoke("Extracting FFmpeg & FFprobe...");
                await Task.Run(() =>
                {
                    using ZipArchive archive = ZipFile.OpenRead(tempZipPath);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(ffmpegPath, overwrite: true);
                            File.SetLastWriteTime(ffmpegPath, DateTime.Now);
                        }
                        else if (entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(ffprobePath, overwrite: true);
                            File.SetLastWriteTime(ffprobePath, DateTime.Now);
                        }
                    }
                });
            }
            catch
            {
                if (!File.Exists(ffmpegPath))
                {
                    try { await YoutubeDLSharp.Utils.DownloadFFmpeg(BaseDir); } catch { }
                }
            }
            finally
            {
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            }
        }

        private static async Task DownloadFileWithProgressAsync(string url, string destinationPath, string label, Action<string> onStatus)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            string tempPath = destinationPath + ".download";

            using (var contentStream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                int lastReportedPercent = -1;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)(totalRead * 100 / totalBytes.Value);
                        if (percent != lastReportedPercent && percent % 5 == 0)
                        {
                            lastReportedPercent = percent;
                            onStatus?.Invoke($"{label} ({percent}%)...");
                        }
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                FormatUtils.TryDeleteFile(destinationPath);
            }
            File.Move(tempPath, destinationPath);
        }

        private static async Task RunSelfUpdateProcessAsync(string exePath, string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = arguments,
                        WorkingDirectory = BaseDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                await Task.Run(() => process.WaitForExit(20000));
            }
            catch { }
        }
    }
}