using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using Koxmix.Engines;

namespace Koxmix.Utils
{
    public static class FormatUtils
    {
        public static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;

            while (number >= 1024m && counter < suffixes.Length - 1)
            {
                number /= 1024m;
                counter++;
            }

            return counter <= 1
                ? $"{Math.Round(number)} {suffixes[counter]}"
                : $"{Math.Round(number, 2)} {suffixes[counter]}";
        }

        public static string SanitizeFileName(string name, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Media_" + DateTime.Now.Ticks;

            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string clean = Regex.Replace(name, "[" + invalidChars + "]", "_");

            if (clean.Length > maxLength) clean = clean.Substring(0, maxLength);

            clean = clean.TrimEnd(' ', '.').TrimStart(' ');

            string[] reserved = {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

            if (Array.Exists(reserved, r => r.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                clean += "_renamed";
            }

            return string.IsNullOrWhiteSpace(clean) ? "Media_" + DateTime.Now.Ticks : clean;
        }

        public static void EnsureDiskSpace(string targetPath, long requiredBytes = 1073741824L)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(targetPath));
                DriveInfo drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new IOException($"[Lỗi Ổ Cứng] Không đủ dung lượng trống trên phân vùng ({root}). Cần ít nhất {FormatFileSize(requiredBytes)} để thực hiện tải xuống.");
                }
            }
            catch (IOException) { throw; }
            catch { }
        }

        public static void TryDeleteFile(string filePath, int maxRetries = 10, int delayMs = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    break;
                }
                catch
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        /// <summary>
        /// Tạo ảnh bìa nhúng sẵn, đóng gói toàn bộ thư mục thành .zip và xóa thư mục gốc.
        /// </summary>
        public static string PackFolderToZip(string folderPath, bool deleteSourceFolder = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return folderPath;

            // Tạo sẵn .thumb.jpg bên trong thư mục trước khi nén để đọc ảnh bìa từ .zip chỉ mất 1ms
            ThumbnailExtractor.PrepareFolderThumbnailBeforeZip(folderPath);

            string zipPath = folderPath.TrimEnd('\\', '/') + ".zip";
            if (File.Exists(zipPath))
            {
                TryDeleteFile(zipPath);
            }

            ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            if (deleteSourceFolder && File.Exists(zipPath))
            {
                try { Directory.Delete(folderPath, true); } catch { }
            }

            return zipPath;
        }
    }
}