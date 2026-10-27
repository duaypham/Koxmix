using Koxmix.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Koxmix.Engines
{
    public static class ThumbnailExtractor
    {
        public static Bitmap LoadBitmapSafely(string imgPath)
        {
            try
            {
                if (!File.Exists(imgPath)) return null;
                using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var tempBmp = new Bitmap(fs);
                return new Bitmap(tempBmp);
            }
            catch
            {
                return null;
            }
        }

        public static Bitmap ExtractVideoFrame(string videoPath)
        {
            string tempJpg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
            try
            {
                string ext = Path.GetExtension(videoPath).ToLowerInvariant();
                bool isStillOrGif = ext == ".webp" || ext == ".avif" || ext == ".gif";
                string seekArg = isStillOrGif ? "" : "-ss 00:00:01 ";

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppUtils.DataDir, "ffmpeg.exe"),
                    Arguments = $"{seekArg}-i \"{videoPath}\" -vframes 1 -q:v 2 -y \"{tempJpg}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    if (proc.WaitForExit(4000))
                    {
                        if (File.Exists(tempJpg)) return LoadBitmapSafely(tempJpg);
                    }
                    else
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempJpg)) File.Delete(tempJpg); } catch { }
            }
            return null;
        }

        public static Bitmap ExtractAudioCover(string audioPath)
        {
            string tempJpg = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppUtils.DataDir, "ffmpeg.exe"),
                    Arguments = $"-i \"{audioPath}\" -an -vframes 1 -q:v 2 -y \"{tempJpg}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    if (proc.WaitForExit(3000))
                    {
                        if (File.Exists(tempJpg)) return LoadBitmapSafely(tempJpg);
                    }
                    else
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempJpg)) File.Delete(tempJpg); } catch { }
            }
            return null;
        }

        public static void PrepareFolderThumbnailBeforeZip(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;

                string thumbPath = Path.Combine(folderPath, ".thumb.jpg");
                if (File.Exists(thumbPath)) return;

                var dir = new DirectoryInfo(folderPath);
                var nativeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
                if (dir.GetFiles("*.*", SearchOption.TopDirectoryOnly).Any(f => nativeExts.Contains(f.Extension)))
                {
                    return;
                }

                var ffmpegConvertExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".avif", ".gif", ".mp4", ".mkv", ".webm", ".mov" };
                var targetMedia = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => ffmpegConvertExts.Contains(f.Extension))
                    .OrderBy(f => f.Name)
                    .FirstOrDefault();

                if (targetMedia == null) return;

                string ffmpegExe = Path.Combine(AppUtils.DataDir, "ffmpeg.exe");
                if (!File.Exists(ffmpegExe)) return;

                bool isVideo = targetMedia.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                               targetMedia.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                               targetMedia.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                               targetMedia.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);

                string args = isVideo
                    ? $"-ss 00:00:01 -i \"{targetMedia.FullName}\" -vframes 1 -q:v 2 -y \"{thumbPath}\""
                    : $"-i \"{targetMedia.FullName}\" -vframes 1 -q:v 2 -y \"{thumbPath}\"";

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(4000);
            }
            catch { }
        }

        public static Bitmap ExtractThumbnailFromZip(string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return null;

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                var nativeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

                var imgEntry = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) && nativeExts.Contains(Path.GetExtension(e.Name)))
                    .OrderBy(e => e.Name.Equals(".thumb.jpg", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(e => e.FullName)
                    .FirstOrDefault();

                if (imgEntry != null)
                {
                    using var stream = imgEntry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    using var tempBmp = new Bitmap(ms);
                    return new Bitmap(tempBmp);
                }
            }
            catch { }
            return null;
        }

        public static async Task<string> GetOrGenerateGalleryThumbnailAsync(string folderPath)
        {
            if (File.Exists(folderPath) && Path.GetExtension(folderPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return folderPath;

            if (!Directory.Exists(folderPath)) return folderPath;

            await Task.Run(() => PrepareFolderThumbnailBeforeZip(folderPath));

            string thumbPath = Path.Combine(folderPath, ".thumb.jpg");
            if (File.Exists(thumbPath)) return thumbPath;

            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
            var firstImg = new DirectoryInfo(folderPath).GetFiles("*.*")
                .Where(f => allowedExts.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Name)
                .FirstOrDefault();

            return firstImg != null ? firstImg.FullName : folderPath;
        }
    }
}