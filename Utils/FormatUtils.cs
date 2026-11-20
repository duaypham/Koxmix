using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Koxmix.Engines;
using Newtonsoft.Json.Linq;

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

            name = Regex.Replace(name, @"[\r\n\t]+", " ").Trim();
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string clean = Regex.Replace(name, "[" + invalidChars + "]", "_");
            clean = Regex.Replace(clean, @"\s{2,}", " ");

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

        /// <summary>
        /// Builds the unified format: [Platform] Uploader - Title [ID] + optional extension
        /// </summary>
        public static string BuildStandardName(string platform, string uploader, string title, string id, string extension = "")
        {
            string cleanUploader = (uploader ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cleanUploader) ||
                cleanUploader.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                cleanUploader.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                cleanUploader = "Unknown";
            }

            string cleanTitle = (title ?? "").Trim();
            cleanTitle = Regex.Replace(cleanTitle, @"[\r\n\t]+", " ");

            // Xóa phần thống kê "828K views · 5.1K reactions | " của Facebook
            cleanTitle = Regex.Replace(cleanTitle, @"^[\d\.,KMBTkmbt]+\s*views\s*·\s*[\d\.,KMBTkmbt]+\s*reactions\s*[\|_－\-]\s*", "", RegexOptions.IgnoreCase).Trim();

            // Nếu tiêu đề truyện có dạng "[Artist] Title" và chưa có uploader thì tách Artist ra
            var leadingBracket = Regex.Match(cleanTitle, @"^\[([^\]]+)\]\s*(.+)$");
            if (leadingBracket.Success)
            {
                string bracketContent = leadingBracket.Groups[1].Value.Trim();
                string remainder = leadingBracket.Groups[2].Value.Trim();
                if (cleanUploader == "Unknown" && !string.IsNullOrEmpty(bracketContent))
                {
                    cleanUploader = bracketContent;
                }
                if (!string.IsNullOrEmpty(remainder))
                {
                    cleanTitle = remainder;
                }
            }

            // Xóa tên người đăng bị lặp ở cuối tiêu đề Facebook (ví dụ: "... | Native Ty Food")
            if (!string.IsNullOrEmpty(cleanUploader) && cleanUploader != "Unknown")
            {
                string trailingAuthorPattern = @"\s*[\|_]\s*" + Regex.Escape(cleanUploader) + @"$";
                cleanTitle = Regex.Replace(cleanTitle, trailingAuthorPattern, "", RegexOptions.IgnoreCase).Trim();
            }

            if (string.IsNullOrWhiteSpace(cleanTitle) ||
                cleanTitle.Equals("NA", StringComparison.OrdinalIgnoreCase))
            {
                cleanTitle = "Media";
            }

            string safeUploader = SanitizeFileName(cleanUploader, 35);
            string safeTitle = SanitizeFileName(cleanTitle, 75);
            string safeId = SanitizeFileName(id ?? "0", 35);

            string result = $"[{platform}] {safeUploader} - {safeTitle} [{safeId}]";
            if (!string.IsNullOrEmpty(extension))
            {
                if (!extension.StartsWith(".")) extension = "." + extension;
                result += extension;
            }
            return result;
        }

        /// <summary>
        /// Cleans up a file created by yt-dlp so its filename strictly follows [Platform] Uploader - Title [ID].ext
        /// </summary>
        public static string NormalizeYtDlpFile(string filePath, string platform, string fallbackId)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return filePath;

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                string ext = Path.GetExtension(filePath);
                string nameNoExt = Path.GetFileNameWithoutExtension(filePath);

                string[] parts = nameNoExt.Split(new[] { "__KX__" }, StringSplitOptions.None); if (parts.Length >= 3)
                {
                    string uploader = parts[0].Trim();
                    string title = parts[1].Trim();
                    string id = !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : fallbackId;

                    string newFileName = BuildStandardName(platform, uploader, title, id, ext);
                    string newPath = Path.Combine(dir, newFileName);

                    if (!string.Equals(filePath, newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(newPath)) TryDeleteFile(newPath);
                        File.Move(filePath, newPath);
                    }
                    return newPath;
                }
            }
            catch { }

            return filePath;
        }

        /// <summary>
        /// Reads metadata from gallery-dl's --write-metadata JSON files and deletes the JSON files afterwards.
        /// </summary>
        public static (string Uploader, string Title) ExtractAndCleanupGalleryDlMetadata(string folderPath)
        {
            string uploader = null;
            string title = null;

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return (null, null);

            try
            {
                var jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);
                foreach (string jf in jsonFiles)
                {
                    if (uploader == null || title == null)
                    {
                        try
                        {
                            string raw = File.ReadAllText(jf);
                            JObject obj = JObject.Parse(raw);

                            if (string.IsNullOrEmpty(uploader))
                            {
                                if (obj["artist"] is JArray artArr && artArr.Count > 0)
                                    uploader = string.Join(", ", artArr.Select(a => a.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(2));
                                else if (obj["group"] is JArray grpArr && grpArr.Count > 0)
                                    uploader = string.Join(", ", grpArr.Select(g => g.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(2));
                                else if (obj["author"] is JObject authObj)
                                    uploader = authObj["name"]?.ToString() ?? authObj["nick"]?.ToString();
                                else if (obj["user"] is JObject userObj)
                                    uploader = userObj["name"]?.ToString() ?? userObj["nick"]?.ToString();
                                else
                                    uploader = obj["uploader"]?.ToString() ?? obj["username"]?.ToString() ?? obj["author"]?.ToString() ?? obj["user"]?.ToString();
                            }

                            if (string.IsNullOrEmpty(title))
                            {
                                if (obj["title"] is JObject titleObj)
                                    title = titleObj["english"]?.ToString() ?? titleObj["japanese"]?.ToString() ?? titleObj["pretty"]?.ToString();
                                else
                                    title = obj["title"]?.ToString() ?? obj["title_en"]?.ToString() ?? obj["content"]?.ToString() ?? obj["description"]?.ToString();
                            }
                        }
                        catch { }
                    }

                    TryDeleteFile(jf);
                }
            }
            catch { }

            return (uploader, title);
        }

        public static void EnsureDiskSpace(string targetPath, long requiredBytes = 1073741824L)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(targetPath));
                DriveInfo drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new IOException($"[Disk Error] Not enough free space on drive ({root}). At least {FormatFileSize(requiredBytes)} is required to download.");
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

        public static string PackFolderToZip(string folderPath, bool deleteSourceFolder = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return folderPath;

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