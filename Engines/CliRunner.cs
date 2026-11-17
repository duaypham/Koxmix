using Koxmix.Core;
using Koxmix.Models;
using Koxmix.Utils;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Koxmix.Engines
{
    public class CliRunner
    {
        private readonly string _galleryDlPath = Path.Combine(AppUtils.DataDir, "gallery-dl.exe");
        private readonly string _ytDlpPath = Path.Combine(AppUtils.DataDir, "yt-dlp.exe");
        private readonly string _ffmpegPath = Path.Combine(AppUtils.DataDir, "ffmpeg.exe");

        public event Action<string> OnProgressChanged;

        private string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public async Task<bool> DownloadGalleryAsync(string url, string outputFolder, CancellationToken ct, bool useCookies = true)
        {
            if (!File.Exists(_galleryDlPath))
                throw new FileNotFoundException("gallery-dl.exe not found.", _galleryDlPath);

            Directory.CreateDirectory(outputFolder);

            string cookieArg = "";
            if (useCookies)
            {
                string cookieFile = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
                if (File.Exists(cookieFile))
                {
                    string safeCookiePath = cookieFile.Replace("\\", "/");
                    cookieArg = $"-o \"http.cookies={safeCookiePath}\" ";
                }
                else
                {
                    string browser = PathManager.GetSelectedBrowser();
                    cookieArg = $"--cookies-from-browser {browser} ";
                }
            }

            string uaPath = Path.Combine(AppUtils.DataDir, "app_useragent.txt");
            string realUa = File.Exists(uaPath)
                ? File.ReadAllText(uaPath).Trim()
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

            string arguments =
                $"{cookieArg}" +
                $"-o \"http.headers.User-Agent={realUa}\" " +
                $"-o \"http.headers.Accept-Language=en-US,en;q=0.9\" " +
                $"--write-metadata " +
                $"-D {QuoteArgument(outputFolder)} " +
                $"-o \"filename={{num:>04}}_{{filename}}.{{extension}}\" " +
                $"--no-mtime " +
                $"{QuoteArgument(url)}";

            ProcessResult result = await RunProcessAsync(_galleryDlPath, arguments, ct);

            if (!result.Success)
            {
                throw new Exception(result.ErrorOutput);
            }
            return true;
        }

        public async Task<bool> DownloadMediaViaYtDlpCliAsync(
            string url, string outputFolder, bool extractMp3, CancellationToken ct, IProgress<string> progress = null)
        {
            if (!File.Exists(_ytDlpPath))
                throw new FileNotFoundException("yt-dlp.exe not found.", _ytDlpPath);
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", _ffmpegPath);

            Directory.CreateDirectory(outputFolder);

            string cookieFile = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
            string cookieArg = File.Exists(cookieFile)
                ? $"--cookies {QuoteArgument(cookieFile)} "
                : $"--cookies-from-browser {PathManager.GetSelectedBrowser()} ";

            string outputTemplate = Path.Combine(outputFolder, "%(uploader_id,uploader,creator|Unknown).35s__KX__%(title,description|Media).75s__KX__%(id)s.%(ext)s");
            string arguments;

            if (extractMp3)
            {
                arguments =
                    $"{cookieArg}" +
                    $"--no-playlist --no-mtime --retries 3 --fragment-retries 3 --socket-timeout 30 " +
                    $"--ffmpeg-location {QuoteArgument(_ffmpegPath)} " +
                    $"--windows-filenames " +
                    $"--format \"ba/b\" --extract-audio --audio-format mp3 --audio-quality 0 " +
                    $"--output {QuoteArgument(outputTemplate)} --newline --progress {QuoteArgument(url)}";
            }
            else
            {
                arguments =
                    $"{cookieArg}" +
                    $"--no-playlist --no-mtime --retries 3 --fragment-retries 3 --socket-timeout 30 " +
                    $"--ffmpeg-location {QuoteArgument(_ffmpegPath)} " +
                    $"--windows-filenames " +
                    $"--format \"b/bv*+ba/b\" " +
                    $"--output {QuoteArgument(outputTemplate)} --newline --progress {QuoteArgument(url)}";
            }

            ProcessResult result = await RunProcessAsync(_ytDlpPath, arguments, ct, progress);

            if (!result.Success)
            {
                string details = string.IsNullOrWhiteSpace(result.ErrorOutput) ? result.Output : result.ErrorOutput;
                throw new Exception(details);
            }
            return true;
        }

        public static void ParseYtDlpProgress(string line, DownloadContext ctx)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                int downloadIndex = line.IndexOf("[download]", StringComparison.OrdinalIgnoreCase);
                if (downloadIndex < 0) return;

                string text = line.Substring(downloadIndex);
                int percentIndex = text.IndexOf('%');
                if (percentIndex > 0)
                {
                    string beforePercent = text.Substring(0, percentIndex).Trim();
                    int start = beforePercent.LastIndexOf(' ');
                    if (start >= 0 && double.TryParse(beforePercent.Substring(start + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                    {
                        ctx.OnProgressChanged?.Invoke($"{Math.Round(percent, 1)}%", null);
                    }
                }

                int atIndex = text.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
                if (atIndex >= 0)
                {
                    int start2 = atIndex + 4;
                    int etaIndex = text.IndexOf(" ETA ", start2, StringComparison.OrdinalIgnoreCase);
                    string speed = etaIndex > start2 ? text.Substring(start2, etaIndex - start2).Trim() : text.Substring(start2).Trim();
                    ctx.OnProgressChanged?.Invoke(null, speed);
                    ctx.OnStatusChanged?.Invoke("Downloading");
                }
            }
            catch { }
        }

        private async Task<ProcessResult> RunProcessAsync(
            string exePath, string arguments, CancellationToken ct, IProgress<string> progress = null)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = AppUtils.DataDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                stdout.AppendLine(e.Data);
                OnProgressChanged?.Invoke(e.Data);
                progress?.Report(e.Data);
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                stderr.AppendLine(e.Data);
                OnProgressChanged?.Invoke(e.Data);
                progress?.Report(e.Data);
            };

            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var processTask = process.WaitForExitAsync(ct);
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(15), ct);

                if (await Task.WhenAny(processTask, timeoutTask) == timeoutTask)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    throw new TimeoutException("Server did not respond within 15 minutes. Process was forcefully terminated to prevent application hang.");
                }

                await processTask;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }

            await Task.Delay(100);

            return new ProcessResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = stdout.ToString(),
                ErrorOutput = stderr.ToString()
            };
        }

        private sealed class ProcessResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string ErrorOutput { get; set; }
        }
    }
}