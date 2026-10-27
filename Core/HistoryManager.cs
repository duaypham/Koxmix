using Koxmix.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Koxmix.Core
{
    public class HistoryManager
    {
        private readonly string _historyFile = Path.Combine(AppUtils.DataDir, "history.txt");
        private static readonly object _fileLock = new object();

        public static bool IsMp3HistoryEntry(string title, string targetPath)
        {
            if (!string.IsNullOrEmpty(targetPath))
            {
                if (targetPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                    targetPath.EndsWith("_MP3].zip", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (!string.IsNullOrEmpty(title) &&
                (title.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                 title.StartsWith("[Playlist MP3]", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return false;
        }

        public void AddToHistory(string url, string title, string targetPath, string size)
        {
            try
            {
                lock (_fileLock)
                {
                    bool newIsMp3 = IsMp3HistoryEntry(title, targetPath);

                    var existingLines = new List<string>();
                    if (File.Exists(_historyFile))
                    {
                        existingLines = File.ReadAllLines(_historyFile, Encoding.UTF8)
                            .Where(l =>
                            {
                                if (string.IsNullOrWhiteSpace(l)) return false;
                                var parts = l.Split('|');
                                if (parts.Length < 4) return false;

                                bool sameUrl = parts[0].Equals(url ?? "", StringComparison.OrdinalIgnoreCase);
                                if (!sameUrl) return true; // Khác URL -> Giữ lại

                                string oldTitle = parts[1];
                                try { oldTitle = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); } catch { }
                                bool oldIsMp3 = IsMp3HistoryEntry(oldTitle, parts[2]);

                                // Nếu cùng URL nhưng khác chế độ (1 cái MP4, 1 cái MP3) -> Giữ lại cả hai!
                                return oldIsMp3 != newIsMp3;
                            })
                            .ToList();
                    }

                    string safeTitle = Convert.ToBase64String(Encoding.UTF8.GetBytes(title ?? ""));
                    string newLine = $"{url}|{safeTitle}|{targetPath}|{size}";
                    existingLines.Add(newLine);

                    File.WriteAllLines(_historyFile, existingLines, Encoding.UTF8);
                }
            }
            catch { }
        }

        public List<string[]> LoadHistory()
        {
            var results = new List<string[]>();
            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(_historyFile)) return results;

                    var lines = File.ReadAllLines(_historyFile, Encoding.UTF8);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var parts = line.Split('|');
                        if (parts.Length >= 4)
                        {
                            string decodedTitle = parts[1];
                            try
                            {
                                byte[] decodedBytes = Convert.FromBase64String(parts[1]);
                                decodedTitle = Encoding.UTF8.GetString(decodedBytes);
                            }
                            catch { }

                            results.Add(new[] { parts[0], decodedTitle, parts[2], parts[3] });
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        public void RemoveFromHistory(string targetUrl, string targetPath)
        {
            try
            {
                lock (_fileLock)
                {
                    var allHistory = LoadHistory().ToList();
                    ClearHistory();

                    bool removed = false;
                    foreach (var item in allHistory)
                    {
                        if (item.Length >= 4)
                        {
                            string itemUrl = item[0] ?? "";
                            string itemPath = item[2] ?? "";

                            if (!removed && itemUrl.Equals(targetUrl ?? "", StringComparison.OrdinalIgnoreCase))
                            {
                                bool isPathSame = true;
                                if (!string.IsNullOrEmpty(targetPath) && !string.IsNullOrEmpty(itemPath))
                                {
                                    isPathSame = itemPath.Replace("\\", "/").Equals(targetPath.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase);
                                }

                                if (isPathSame)
                                {
                                    removed = true;
                                    continue;
                                }
                            }

                            AddToHistory(item[0], item[1], item[2], item[3]);
                        }
                    }
                }
            }
            catch { }
        }

        public void ClearHistory()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_historyFile)) File.Delete(_historyFile);
                }
            }
            catch { }
        }
    }
}