using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Core;
using Koxmix.Engines;
using Koxmix.Models;
using Koxmix.Utils;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Koxmix.Downloaders
{
    public class YtDlpDownloader : IDownloader
    {
        private readonly CliRunner _cliRunner = new CliRunner();

        public bool CanHandle(string url) => !string.IsNullOrEmpty(url);

        private static string GetPlatformLabel(string url)
        {
            if (string.IsNullOrEmpty(url)) return "Web";
            if (url.Contains("youtube.com") || url.Contains("youtu.be")) return "YouTube";
            if (url.Contains("tiktok.com")) return "TikTok";
            if (url.Contains("bilibili.com") || url.Contains("bilibili.tv") || url.Contains("b23.tv")) return "Bilibili";
            if (url.Contains("facebook.com") || url.Contains("fb.watch") || url.Contains("fb.me")) return "Facebook";
            if (url.Contains("x.com") || url.Contains("twitter.com")) return "X.com";
            return "Web";
        }

        public async Task DownloadAsync(DownloadContext ctx)
        {
            string targetFolder = ctx.TargetFolder;
            bool isPlaylist = UrlUtils.IsPlaylist(ctx.Url);
            string platform = GetPlatformLabel(ctx.Url);
            Directory.CreateDirectory(targetFolder);

            string videoId = UrlUtils.ExtractMediaId(ctx.Url);
            string playlistId = UrlUtils.ExtractPlaylistId(ctx.Url);

            if (UrlUtils.IsBilibiliImagePost(ctx.Url))
            {
                await DownloadGalleryFallbackAsync(ctx, platform, videoId ?? DateTime.Now.ToString("yyyyMMdd_HHmmss"), targetFolder);
                return;
            }

            if (isPlaylist && !string.IsNullOrEmpty(playlistId))
            {
                string plTag = ctx.IsMp3 ? $"[{playlistId}_MP3]" : $"[{playlistId}]";
                var existingZip = new DirectoryInfo(targetFolder).GetFiles($"*{plTag}*.zip").FirstOrDefault();
                var existingDir = new DirectoryInfo(targetFolder).GetDirectories($"*{plTag}*").FirstOrDefault();

                if (existingZip != null)
                {
                    if (ctx.ForceRedownload)
                    {
                        FormatUtils.TryDeleteFile(existingZip.FullName);
                        if (existingDir != null) { try { existingDir.Delete(true); } catch { } }
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        ctx.OnSuccess?.Invoke(Path.GetFileNameWithoutExtension(existingZip.Name), FormatUtils.FormatFileSize(existingZip.Length), existingZip.FullName);
                        return;
                    }
                }
                else if (existingDir != null)
                {
                    foreach (var tempFile in existingDir.GetFiles("*.*", SearchOption.AllDirectories))
                    {
                        if (tempFile.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                            tempFile.Extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase))
                        {
                            FormatUtils.TryDeleteFile(tempFile.FullName);
                        }
                    }
                }
            }
            else if (!isPlaylist && !string.IsNullOrEmpty(videoId))
            {
                var existingFiles = new DirectoryInfo(targetFolder).GetFiles($"*[{videoId}]*.*")
                    .Where(f => !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl")).ToList();

                FileInfo existingFile = ctx.IsMp3
                    ? existingFiles.FirstOrDefault(f => f.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
                    : existingFiles.FirstOrDefault(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase));

                if (existingFile != null)
                {
                    if (ctx.ForceRedownload)
                    {
                        FormatUtils.TryDeleteFile(existingFile.FullName);
                    }
                    else
                    {
                        ctx.OnStatusChanged?.Invoke("Completed (Exists)");
                        string displayExisting = existingFile.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(existingFile.Name)
                            : existingFile.Name;
                        ctx.OnSuccess?.Invoke(displayExisting, FormatUtils.FormatFileSize(existingFile.Length), existingFile.FullName);
                        return;
                    }
                }
            }

            HashSet<string> snapshotBefore = GetDirectorySnapshot(targetFolder);

            string ytdlpExe = Path.Combine(AppUtils.DataDir, "yt-dlp.exe");
            string ffmpegExe = Path.Combine(AppUtils.DataDir, "ffmpeg.exe");

            var localYtdl = new YoutubeDL(4)
            {
                YoutubeDLPath = ytdlpExe,
                FFmpegPath = ffmpegExe,
                OutputFolder = targetFolder
            };

            string uaPath = Path.Combine(AppUtils.DataDir, "app_useragent.txt");
            string realUa = File.Exists(uaPath)
                ? File.ReadAllText(uaPath).Trim()
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

            string refererHeader = (ctx.Url.Contains("bilibili.com") || ctx.Url.Contains("bilibili.tv"))
                ? "https://www.bilibili.com/"
                : (ctx.Url.Contains("x.com") ? "https://x.com/" : null);

            var options = new OptionSet
            {
                IgnoreErrors = isPlaylist,
                NoPlaylist = !isPlaylist,
                EmbedChapters = true,
                WindowsFilenames = true,
                ConcurrentFragments = 4,
                SleepInterval = 1,
                MaxSleepInterval = 3
            };

            options.AddCustomOption<bool>("--rm-cache-dir", true);
            options.AddCustomOption<string>("--user-agent", realUa);
            if (!string.IsNullOrEmpty(refererHeader))
            {
                options.AddCustomOption<string>("--add-header", $"Referer:{refererHeader}");
            }

            string cookieFile = Path.Combine(AppUtils.DataDir, "app_cookies.txt");
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
                options.EmbedThumbnail = true;
                options.Format = "bestaudio[format_note*=original]/bestaudio/best";
            }
            else
            {
                options.WriteSubs = true;
                options.WriteAutoSubs = false;
                options.SubLangs = "vi.*,en.*,ja.*,zh.*,ko.*,id.*,es.*,fr.*,ru.*,th.*,-live_chat";
                options.EmbedSubs = false;
                options.ConvertSubs = "srt";

                options.MergeOutputFormat = DownloadMergeFormat.Mp4;
                options.Format = (ctx.Url.Contains("bilibili.com") || ctx.Url.Contains("bilibili.tv"))
                    ? "bestvideo[vcodec^=avc1]+bestaudio[ext=m4a]/best[vcodec^=avc1]/bv*+ba/b"
                    : "bestvideo+bestaudio[format_note*=original]/bestvideo+bestaudio/best";
            }

            if (isPlaylist)
            {
                string plIdTemplate = ctx.IsMp3 ? "%(playlist_id,id)s_MP3" : "%(playlist_id,id)s";
                string plPlatform = ctx.IsMp3 ? $"{platform} MP3" : $"{platform} Playlist";
                options.Output = Path.Combine(
                    targetFolder,
                    $"[{plPlatform}] %(playlist_uploader,uploader,channel|Unknown).30s - %(playlist_title,series,title).55s [{plIdTemplate}]",
                    $"%(playlist_index,autonumber)02d - %(title).65s [%(id)s].%(ext)s");
            }
            else
            {
                options.Output = Path.Combine(targetFolder, "%(uploader,channel,creator,uploader_id|Unknown).35s__KX__%(title).75s__KX__%(id)s.%(ext)s");
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

                string firstValidFile = playlistRes.Data?.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(d));
                string playlistFolder = !string.IsNullOrEmpty(firstValidFile) ? Path.GetDirectoryName(firstValidFile) : null;

                if (string.IsNullOrEmpty(playlistFolder) && !string.IsNullOrEmpty(playlistId))
                {
                    string plTag = ctx.IsMp3 ? $"[{playlistId}_MP3]" : $"[{playlistId}]";
                    var matchedDir = new DirectoryInfo(targetFolder).GetDirectories($"*{plTag}*").FirstOrDefault();
                    if (matchedDir != null) playlistFolder = matchedDir.FullName;
                }

                if (string.IsNullOrEmpty(playlistFolder) || !Directory.Exists(playlistFolder) ||
                    Directory.GetFiles(playlistFolder, "*.*", SearchOption.TopDirectoryOnly).Length == 0)
                {
                    throw new Exception("No video/audio could be downloaded from this playlist (The playlist may be private or require updated Cookies).");
                }

                string rawFolderName = new DirectoryInfo(playlistFolder).Name;

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
                ctx.OnSuccess?.Invoke(rawFolderName, sizeText, finalPlaylistOutput);
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

                    if (!ctx.IsMp3 && (errorMsg.Contains("extension ('html') is unusual") || errorMsg.Contains("Falling back on generic")))
                    {
                        await DownloadGalleryFallbackAsync(ctx, platform, videoId ?? DateTime.Now.ToString("yyyyMMdd_HHmmss"), targetFolder);
                        return;
                    }

                    throw new Exception(errorMsg);
                }
                finalPath = newCreated.OrderByDescending(f => f.LastWriteTime).First().FullName;
            }

            string rawMediaPrefix = Path.GetFileNameWithoutExtension(finalPath);

            finalPath = FormatUtils.NormalizeYtDlpFile(finalPath, platform, videoId);

            if (!ctx.IsMp3)
            {
                ctx.OnStatusChanged?.Invoke("Embedding subtitles...");
                string directoryName = Path.GetDirectoryName(finalPath);
                string subDirSingle = Path.Combine(directoryName, "Subtitles");
                await Task.Run(() => FilterAndEmbedSubtitlesForVideo(finalPath, subDirSingle, ffmpegExe, rawMediaPrefix), ctx.Token);
            }

            ctx.OnStatusChanged?.Invoke("Completed");
            FileInfo fi = new FileInfo(finalPath);
            ctx.OnSuccess?.Invoke(fi.Name, FormatUtils.FormatFileSize(fi.Length), fi.FullName);
        }

        private async Task DownloadGalleryFallbackAsync(DownloadContext ctx, string platform, string mediaId, string targetFolder)
        {
            ctx.OnStatusChanged?.Invoke($"Fetching {platform} post via gallery-dl...");
            string tempFolder = Path.Combine(targetFolder, $"Temp_{platform}_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempFolder);

            try
            {
                try
                {
                    await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token, useCookies: true);
                }
                catch when (!ctx.Token.IsCancellationRequested)
                {
                    await _cliRunner.DownloadGalleryAsync(ctx.Url, tempFolder, ctx.Token, useCookies: false);
                }

                ctx.Token.ThrowIfCancellationRequested();

                var (metaUploader, metaTitle) = FormatUtils.ExtractAndCleanupGalleryDlMetadata(tempFolder);
                var files = new DirectoryInfo(tempFolder).GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => !f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                                !f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name)
                    .ToList();

                if (files.Count == 0)
                {
                    throw new Exception($"Could not extract media from this {platform} link. Please verify the URL or refresh your {platform} Cookies.");
                }

                if (files.Count == 1)
                {
                    string singleName = FormatUtils.BuildStandardName(platform, metaUploader, metaTitle ?? "Post", mediaId, files[0].Extension);
                    string singlePath = Path.Combine(targetFolder, singleName);
                    if (File.Exists(singlePath)) FormatUtils.TryDeleteFile(singlePath);
                    files[0].MoveTo(singlePath);

                    var finalFi = new FileInfo(singlePath);
                    ctx.OnStatusChanged?.Invoke("Completed");
                    ctx.OnSuccess?.Invoke(finalFi.Name, FormatUtils.FormatFileSize(finalFi.Length), finalFi.FullName);
                    return;
                }

                string albumBaseName = FormatUtils.BuildStandardName(platform, metaUploader, metaTitle ?? "Album", mediaId);
                string albumFolder = Path.Combine(targetFolder, albumBaseName);
                Directory.CreateDirectory(albumFolder);

                int idx = 1;
                foreach (var file in files)
                {
                    string newPath = Path.Combine(albumFolder, $"{idx++:D4}{file.Extension}");
                    if (File.Exists(newPath)) FormatUtils.TryDeleteFile(newPath);
                    file.MoveTo(newPath);
                }

                ctx.OnStatusChanged?.Invoke("Packaging album into .zip...");
                string zipPath = await Task.Run(() => FormatUtils.PackFolderToZip(albumFolder, deleteSourceFolder: true), ctx.Token);
                long zipBytes = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                ctx.OnStatusChanged?.Invoke("Completed");
                ctx.OnSuccess?.Invoke(albumBaseName, FormatUtils.FormatFileSize(zipBytes), zipPath);
            }
            finally
            {
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
            }
        }

        private static void FilterAndEmbedSubtitlesForVideo(string videoPath, string subDir, string ffmpegExe, string rawSearchPrefix = null)
        {
            try
            {
                if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return;

                string directoryName = Path.GetDirectoryName(videoPath);
                string cleanMediaName = Path.GetFileNameWithoutExtension(videoPath);
                string searchPrefix = !string.IsNullOrEmpty(rawSearchPrefix) ? rawSearchPrefix : cleanMediaName;

                var allSubFiles = new DirectoryInfo(directoryName).GetFiles(searchPrefix + ".*")
                    .Where(f => IsSubtitleExtension(f.Extension) && f.Length > 0)
                    .ToList();

                if (allSubFiles.Count == 0) return;

                var parsedSubs = allSubFiles.Select(f =>
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(f.Name);
                    string lang = nameNoExt.Length > searchPrefix.Length
                        ? nameNoExt.Substring(searchPrefix.Length).TrimStart('.')
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
                    if (viSub != null) selectedSubs.Add((viSub.File, viSub.Lang));
                    if (enSub != null) selectedSubs.Add((enSub.File, enSub.Lang));
                }
                else
                {
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

                var keptPaths = new HashSet<string>(selectedSubs.Select(s => s.File.FullName), StringComparer.OrdinalIgnoreCase);
                foreach (var sub in allSubFiles)
                {
                    if (!keptPaths.Contains(sub.FullName))
                    {
                        FormatUtils.TryDeleteFile(sub.FullName);
                    }
                }

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

                Directory.CreateDirectory(subDir);
                foreach (var (subFile, lang) in selectedSubs)
                {
                    if (File.Exists(subFile.FullName))
                    {
                        string cleanSubName = $"{cleanMediaName}.{lang}{subFile.Extension}";
                        string newSubPath = Path.Combine(subDir, cleanSubName);
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

            if (lower.StartsWith("vi")) return ("vie", "Vietnamese");
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