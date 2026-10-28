using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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

        private static void SafeStatus(Action<string> onStatus, string message)
        {
            try { onStatus?.Invoke(message); } catch { }
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
                SafeStatus(onStatus, "Extracting deno.exe...");

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
                    SafeStatus(onStatus, "Downloading yt-dlp.exe...");
                    await DownloadFileWithProgressAsync(downloadUrl, ytdlpPath, "yt-dlp.exe", onStatus);
                }
                else
                {
                    SafeStatus(onStatus, "Checking updates for yt-dlp...");
                    await RunSelfUpdateProcessAsync(ytdlpPath, "-U");
                }
            }
            catch
            {
                if (!File.Exists(ytdlpPath))
                {
                    try { await YoutubeDLSharp.Utils.DownloadYtDlp(BaseDir); } catch { }
                }
            }
        }

        public static async Task EnsureAndUpdateGalleryDlAsync(Action<string> onStatus)
        {
            string galleryDlPath = Path.Combine(BaseDir, "gallery-dl.exe");

            try
            {
                if (!File.Exists(galleryDlPath))
                {
                    SafeStatus(onStatus, "Extracting gallery-dl.exe...");
                    if (TryExtractEmbeddedTool("gallery-dl.exe", galleryDlPath))
                    {
                        SafeStatus(onStatus, "Checking updates for gallery-dl...");
                        await RunSelfUpdateProcessAsync(galleryDlPath, "-U");
                        return;
                    }

                    SafeStatus(onStatus, "Downloading gallery-dl.exe...");
                    const string downloadUrl = "https://github.com/mikf/gallery-dl/releases/latest/download/gallery-dl.exe";
                    try
                    {
                        await DownloadFileWithProgressAsync(downloadUrl, galleryDlPath, "gallery-dl.exe", onStatus);
                    }
                    catch
                    {
                        string apiAssetUrl = await GetLatestGithubExeUrlAsync("mikf", "gallery-dl");
                        if (!string.IsNullOrEmpty(apiAssetUrl))
                        {
                            await DownloadFileWithProgressAsync(apiAssetUrl, galleryDlPath, "gallery-dl.exe", onStatus);
                        }
                    }
                }
                else
                {
                    SafeStatus(onStatus, "Checking updates for gallery-dl...");
                    await RunSelfUpdateProcessAsync(galleryDlPath, "-U");
                }
            }
            catch { }
        }

        private static bool TryExtractEmbeddedTool(string fileName, string destinationPath)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(resName)) return false;

                using Stream stream = asm.GetManifestResourceStream(resName);
                if (stream == null) return false;

                using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(fs);
                }

                return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> GetLatestGithubExeUrlAsync(string owner, string repo)
        {
            try
            {
                string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                string json = await _httpClient.GetStringAsync(apiUrl);
                JObject release = JObject.Parse(json);
                if (release["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        string name = asset["name"]?.ToString() ?? "";
                        string url = asset["browser_download_url"]?.ToString() ?? "";
                        if (name.Equals("gallery-dl.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(url))
                        {
                            return url;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static async Task EnsureAndUpdateFFmpegAsync(Action<string> onStatus)
        {
            string ffmpegPath = Path.Combine(BaseDir, "ffmpeg.exe");
            string ffprobePath = Path.Combine(BaseDir, "ffprobe.exe");

            bool isMissing = !File.Exists(ffmpegPath) || !File.Exists(ffprobePath);
            bool isOutdated = !isMissing && (DateTime.Now - File.GetLastWriteTime(ffmpegPath)).TotalDays > 30;

            if (!isMissing && !isOutdated) return;

            const string ffmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), $"koxmix_ffmpeg_{Guid.NewGuid():N}.zip");

            try
            {
                string actionText = isMissing ? "Downloading FFmpeg & FFprobe" : "Updating FFmpeg & FFprobe";
                await DownloadFileWithProgressAsync(ffmpegZipUrl, tempZipPath, actionText, onStatus);

                SafeStatus(onStatus, "Extracting FFmpeg & FFprobe...");
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
                            SafeStatus(onStatus, $"{label} ({percent}%)...");
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