using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Models;
using Koxmix.Utils;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Koxmix.Downloaders
{
    public class YtDlpDownloader : IDownloader
    {
        public bool CanHandle(string url) => !string.IsNullOrEmpty(url);

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            bool isPlaylist = UrlUtils.IsPlaylist(ctx.Url);
            Directory.CreateDirectory(targetFolder);

            string videoId = UrlUtils.ExtractMediaId(ctx.Url);
            string playlistId = UrlUtils.ExtractPlaylistId(ctx.Url);

            if (isPlaylist && !string.IsNullOrEmpty(playlistId))
            {
                var existingZip = new DirectoryInfo(targetFolder).GetFiles($"*[{playlistId}]*.zip").FirstOrDefault();
                var existingDir = new DirectoryInfo(targetFolder).GetDirectories($"*[{playlistId}]*").FirstOrDefault();

                if (existingZip != null || (existingDir != null && existingDir.GetFiles().Length > 0))
                {
                    if (ctx.ForceRedownload)
                    {
                        if (existingZip != null) FormatUtils.TryDeleteFile(existingZip.FullName);
                        if (existingDir != null) { try { existingDir.Delete(true); } catch { } }
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        if (existingZip != null)
                        {
                            ctx.OnSuccess?.Invoke($"[Playlist] {Path.GetFileNameWithoutExtension(existingZip.Name)}", FormatUtils.FormatFileSize(existingZip.Length), existingZip.FullName);
                        }
                        else
                        {
                            ctx.OnSuccess?.Invoke($"[Playlist] {existingDir.Name}", "Multiple files", existingDir.FullName);
                        }
                        return;
                    }
                }
            }
            else if (!isPlaylist && !string.IsNullOrEmpty(videoId))
            {
                var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*[{videoId}].*")
                    .Where(f => !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl")).ToList();

                FileInfo existingFile = ctx.IsMp3
                    ? existingFiles.FirstOrDefault(f => f.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
                    : existingFiles.FirstOrDefault(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase));

                if (existingFile != null)
                {
                    if (ctx.ForceRedownload)
                    {
                        FormatUtils.TryDeleteFile(existingFile.FullName);
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        ctx.OnSuccess?.Invoke(existingFile.Name, FormatUtils.FormatFileSize(existingFile.Length), existingFile.FullName);
                        return;
                    }
                }
            }

            HashSet<string> snapshotBefore = GetDirectorySnapshot(targetFolder);

            string ytdlpExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
            string ffmpegExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

            var localYtdl = new YoutubeDL(4)
            {
                YoutubeDLPath = ytdlpExe,
                FFmpegPath = ffmpegExe,
                OutputFolder = targetFolder
            };

            string refererHeader = ctx.Url.Contains("bilibili.com")
                ? "https://www.bilibili.com/"
                : (ctx.Url.Contains("x.com") ? "https://x.com/" : null);

            var options = new OptionSet
            {
                IgnoreErrors = true,
                EmbedChapters = true,
                Referer = refererHeader,
                WindowsFilenames = true,
                ConcurrentFragments = 4,
                SleepInterval = 1,
                MaxSleepInterval = 3
            };

            options.AddCustomOption<bool>("--rm-cache-dir", true);

            string cookieFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_cookies.txt");
            if (File.Exists(cookieFile))
            {
                options.Cookies = cookieFile;
                options.CookiesFromBrowser = null;
            }
            else
            {
                options.CookiesFromBrowser = PathManager.GetSelectedBrowser();
            }

            if (ctx.IsMp3)
            {
                options.ExtractAudio = true;
                options.AudioFormat = AudioConversionFormat.Mp3;
                options.Format = "bestaudio/best";
            }
            else
            {
                options.WriteSubs = true;
                options.WriteAutoSubs = false;
                options.SubLangs = "vi.*,en.*,ja.*,zh.*,ko.*,id.*,es.*,fr.*,ru.*,th.*,-live_chat";
                options.EmbedSubs = false;
                options.ConvertSubs = "srt";

                options.MergeOutputFormat = DownloadMergeFormat.Mp4;
                options.Format = ctx.Url.Contains("bilibili.com")
                    ? "bestvideo[vcodec^=avc1]+bestaudio[ext=m4a]/best[vcodec^=avc1]/bv*+ba/b"
                    : "bestvideo+bestaudio/best";
            }

            if (isPlaylist)
            {
                options.Output = Path.Combine(targetFolder, "%(playlist_title,series,title).70s [%(playlist_id,id)s]", "%(playlist_index,autonumber)02d - %(title).70s [%(id)s].%(ext)s");
            }
            else if (ctx.Url.Contains("tiktok.com"))
            {
                options.Output = Path.Combine(targetFolder, "TikTok_@%(uploader,creator)s_[%(id)s].%(ext)s");
            }
            else
            {
                options.Output = Path.Combine(targetFolder, "%(title).100s [%(id)s].%(ext)s");
            }

            RunResult<string[]> playlistRes = null;
            RunResult<string> singleRes = null;

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (!ctx.Token.IsCancellationRequested)
                {
                    ctx.OnProgressChanged?.Invoke($"{Math.Round(p.Progress * 100f, 1)}%", p.DownloadSpeed);
                }
            });

            if (isPlaylist)
            {
                playlistRes = await localYtdl.RunVideoPlaylistDownload(ctx.Url, overrideOptions: options, progress: progress, ct: ctx.Token, start: 1);
            }
            else
            {
                var mergeFmt = ctx.IsMp3 ? DownloadMergeFormat.Unspecified : DownloadMergeFormat.Mp4;
                singleRes = await localYtdl.RunVideoDownload(ctx.Url, options.Format, mergeFmt, VideoRecodeFormat.None, ctx.Token, progress, null, options);
            }

            ctx.Token.ThrowIfCancellationRequested();

            if (isPlaylist)
            {
                if (playlistRes == null || (!playlistRes.Success && playlistRes.Data == null))
                {
                    throw new Exception(playlistRes?.ErrorOutput != null ? string.Join("\n", playlistRes.ErrorOutput) : "Failed to download playlist.");
                }

                string playlistFolder = (playlistRes.Data != null && playlistRes.Data.Length != 0 && !string.IsNullOrEmpty(playlistRes.Data[0]))
                    ? Path.GetDirectoryName(playlistRes.Data[0])
                    : targetFolder;

                string rawFolderName = new DirectoryInfo(playlistFolder).Name;
                int lastBracketIndex = rawFolderName.LastIndexOf(" [");
                string playlistName = lastBracketIndex > 0 ? rawFolderName.Substring(0, lastBracketIndex) : rawFolderName;

                if (!ctx.IsMp3 && Directory.Exists(playlistFolder))
                {
                    ctx.OnStatusChanged?.Invoke("Embedding subtitles into playlist videos...");
                    string subDir = Path.Combine(playlistFolder, "Subtitles");
                    var videoFiles = new DirectoryInfo(playlistFolder).GetFiles("*.mp4", SearchOption.TopDirectoryOnly);
                    foreach (var vid in videoFiles)
                    {
                        await Task.Run(() => FilterAndEmbedSubtitlesForVideo(vid.FullName, subDir, ffmpegExe), ctx.Token);
                    }
                }

                string finalPlaylistOutput = playlistFolder;
                string sizeText = "Multiple files";

                if (!string.Equals(Path.GetFullPath(playlistFolder).TrimEnd('\\'), Path.GetFullPath(targetFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    ctx.OnStatusChanged?.Invoke("Packaging playlist into .zip...");
                    finalPlaylistOutput = await Task.Run(() => FormatUtils.PackFolderToZip(playlistFolder, deleteSourceFolder: true), ctx.Token);
                    if (File.Exists(finalPlaylistOutput))
                    {
                        sizeText = FormatUtils.FormatFileSize(new FileInfo(finalPlaylistOutput).Length);
                    }
                }

                ctx.OnStatusChanged?.Invoke("Completed");
                ctx.OnSuccess?.Invoke("[Playlist] " + playlistName, sizeText, finalPlaylistOutput);
                return;
            }

            string finalPath = singleRes?.Data;

            if (singleRes == null || !singleRes.Success || string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
            {
                List<FileInfo> newCreated = GetNewFilesCreated(targetFolder, snapshotBefore);

                if (!string.IsNullOrEmpty(videoId))
                {
                    var exactMatches = newCreated.Where(f => f.Name.Contains(videoId)).ToList();
                    if (exactMatches.Count > 0) newCreated = exactMatches;
                }

                if (newCreated.Count <= 0)
                {
                    string errorMsg = singleRes?.ErrorOutput != null ? string.Join("\n", singleRes.ErrorOutput) : "Unknown yt-dlp extraction error.";
                    throw new Exception(errorMsg);
                }
                finalPath = newCreated.OrderByDescending(f => f.LastWriteTime).First().FullName;
            }

            if (!ctx.IsMp3)
            {
                ctx.OnStatusChanged?.Invoke("Embedding subtitles...");
                string directoryName = Path.GetDirectoryName(finalPath);
                string subDirSingle = Path.Combine(directoryName, "Subtitles");
                await Task.Run(() => FilterAndEmbedSubtitlesForVideo(finalPath, subDirSingle, ffmpegExe), ctx.Token);
            }

            ctx.OnStatusChanged?.Invoke("Completed");
            FileInfo fi = new FileInfo(finalPath);
            ctx.OnSuccess?.Invoke(fi.Name, FormatUtils.FormatFileSize(fi.Length), finalPath);
        }

        /// <summary>
        /// Lọc phụ đề theo đúng logic:
        /// - Nếu có Tiếng Việt và/hoặc Tiếng Anh -> Giữ cả Việt và Anh, xóa toàn bộ phụ đề ngôn ngữ khác.
        /// - Nếu KHÔNG có cả Việt lẫn Anh -> Chọn đúng 1 phụ đề thay thế theo thứ tự ưu tiên (ja -> zh -> ko -> id -> khác).
        /// Sau đó dùng FFmpeg nhúng thẳng vào file .mp4 và di chuyển file .srt vào thư mục Subtitles.
        /// </summary>
        private static void FilterAndEmbedSubtitlesForVideo(string videoPath, string subDir, string ffmpegExe)
        {
            try
            {
                if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return;

                string directoryName = Path.GetDirectoryName(videoPath);
                string mediaName = Path.GetFileNameWithoutExtension(videoPath);

                var allSubFiles = new DirectoryInfo(directoryName).GetFiles(mediaName + ".*")
                    .Where(f => IsSubtitleExtension(f.Extension) && f.Length > 0)
                    .ToList();

                if (allSubFiles.Count == 0) return;

                var parsedSubs = allSubFiles.Select(f =>
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(f.Name);
                    string lang = nameNoExt.Length > mediaName.Length
                        ? nameNoExt.Substring(mediaName.Length).TrimStart('.')
                        : "und";
                    return new { File = f, Lang = lang };
                }).ToList();

                var viSub = parsedSubs
                    .Where(x => x.Lang.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Lang.Length)
                    .FirstOrDefault();

                var enSub = parsedSubs
                    .Where(x => x.Lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Lang.Length)
                    .FirstOrDefault();

                var selectedSubs = new List<(FileInfo File, string Lang)>();

                if (viSub != null || enSub != null)
                {
                    // Có Việt hoặc Anh -> Lấy cả Việt và Anh (nếu có)
                    if (viSub != null) selectedSubs.Add((viSub.File, viSub.Lang));
                    if (enSub != null) selectedSubs.Add((enSub.File, enSub.Lang));
                }
                else
                {
                    // Không có cả Việt lẫn Anh -> Lấy đúng 1 phụ đề ưu tiên nhất
                    string[] priorityPrefixes = { "ja", "zh", "ko", "id", "es", "fr", "ru", "th" };
                    var fallbackSub = parsedSubs
                        .OrderBy(x =>
                        {
                            for (int i = 0; i < priorityPrefixes.Length; i++)
                            {
                                if (x.Lang.StartsWith(priorityPrefixes[i], StringComparison.OrdinalIgnoreCase))
                                    return i;
                            }
                            return 999;
                        })
                        .ThenBy(x => x.Lang.Length)
                        .First();

                    selectedSubs.Add((fallbackSub.File, fallbackSub.Lang));
                }

                // Xóa ngay các file phụ đề thừa không nằm trong danh sách được chọn
                var keptPaths = new HashSet<string>(selectedSubs.Select(s => s.File.FullName), StringComparer.OrdinalIgnoreCase);
                foreach (var sub in allSubFiles)
                {
                    if (!keptPaths.Contains(sub.FullName))
                    {
                        FormatUtils.TryDeleteFile(sub.FullName);
                    }
                }

                // Nhúng các phụ đề đã chọn vào file MP4 bằng FFmpeg
                if (File.Exists(ffmpegExe) && selectedSubs.Count > 0)
                {
                    string tempVideoPath = Path.Combine(directoryName, $"{Guid.NewGuid():N}_temp.mp4");
                    try
                    {
                        string inputArgs = $"-y -i \"{videoPath}\" ";
                        string mapArgs = "-map 0:v -map 0:a? ";
                        string metaArgs = "";

                        for (int i = 0; i < selectedSubs.Count; i++)
                        {
                            inputArgs += $"-sub_charenc UTF-8 -i \"{selectedSubs[i].File.FullName}\" ";
                            mapArgs += $"-map {i + 1}:0 ";

                            var (isoCode, displayTitle) = GetIsoLangInfo(selectedSubs[i].Lang);
                            metaArgs += $"-metadata:s:s:{i} language={isoCode} -metadata:s:s:{i} handler_name=\"{displayTitle}\" -metadata:s:s:{i} title=\"{displayTitle}\" ";

                            // Đặt cờ default cho phụ đề đầu tiên để trình phát tự động hiển thị ngay khi mở video
                            if (i == 0)
                            {
                                metaArgs += "-disposition:s:0 default ";
                            }
                        }

                        string fullArgs = $"{inputArgs}{mapArgs}-map_metadata 0 -map_chapters 0 -c:v copy -c:a copy -c:s mov_text {metaArgs}\"{tempVideoPath}\"";

                        using var proc = Process.Start(new ProcessStartInfo
                        {
                            FileName = ffmpegExe,
                            Arguments = fullArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });

                        if (proc != null && proc.WaitForExit(60000) && proc.ExitCode == 0 && File.Exists(tempVideoPath) && new FileInfo(tempVideoPath).Length > 0)
                        {
                            FormatUtils.TryDeleteFile(videoPath);
                            File.Move(tempVideoPath, videoPath);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (File.Exists(tempVideoPath)) FormatUtils.TryDeleteFile(tempVideoPath);
                    }
                }

                // Di chuyển các file .srt đã chọn vào thư mục Subtitles
                Directory.CreateDirectory(subDir);
                foreach (var (subFile, _) in selectedSubs)
                {
                    if (File.Exists(subFile.FullName))
                    {
                        string newSubPath = Path.Combine(subDir, subFile.Name);
                        if (File.Exists(newSubPath)) FormatUtils.TryDeleteFile(newSubPath);
                        try { File.Move(subFile.FullName, newSubPath); } catch { }
                    }
                }
            }
            catch { }
        }

        private static (string IsoCode, string Title) GetIsoLangInfo(string langTag)
        {
            if (string.IsNullOrWhiteSpace(langTag)) return ("und", "Subtitle");
            string lower = langTag.ToLowerInvariant();

            if (lower.StartsWith("vi")) return ("vie", "Tiếng Việt");
            if (lower.StartsWith("en")) return ("eng", "English");
            if (lower.StartsWith("ja")) return ("jpn", "Japanese");
            if (lower.StartsWith("zh")) return ("zho", "Chinese");
            if (lower.StartsWith("ko")) return ("kor", "Korean");
            if (lower.StartsWith("id")) return ("ind", "Indonesian");
            if (lower.StartsWith("es")) return ("spa", "Spanish");
            if (lower.StartsWith("fr")) return ("fra", "French");
            if (lower.StartsWith("ru")) return ("rus", "Russian");
            if (lower.StartsWith("th")) return ("tha", "Thai");

            return ("und", langTag);
        }

        private static bool IsSubtitleExtension(string ext)
        {
            return ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".lrc", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetDirectorySnapshot(string folder)
        {
            if (Directory.Exists(folder))
            {
                return new HashSet<string>(Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static List<FileInfo> GetNewFilesCreated(string folder, HashSet<string> snapshotBefore)
        {
            if (!Directory.Exists(folder)) return new List<FileInfo>();

            return new DirectoryInfo(folder).GetFiles("*.*", SearchOption.AllDirectories)
                .Where(f => !snapshotBefore.Contains(f.FullName)
                            && !f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase)
                            && !f.Extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase)
                            && !IsSubtitleExtension(f.Extension)
                            && !f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                            && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                            && !Regex.IsMatch(f.Name, @"\.f\d+\.", RegexOptions.IgnoreCase))
                .ToList();
        }
    }
}