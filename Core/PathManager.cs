using Koxmix.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace Koxmix.Core
{
    public class AppConfigData
    {
        public string YoutubePath { get; set; } = "";
        public string TikTokPath { get; set; } = "";
        public string BilibiliPath { get; set; } = "";
        public string XPath { get; set; } = "";
        public string PixivPath { get; set; } = "";
        public string NHentaiPath { get; set; } = "";
        public string HitomiPath { get; set; } = "";
        public string FacebookPath { get; set; } = "";
        public string SelectedBrowser { get; set; } = "chrome";
    }

    public static class PathManager
    {
        private static readonly string ConfigFilePath = Path.Combine(AppUtils.DataDir, "koxmix_config.json");
        private static readonly object _lock = new object();
        private static AppConfigData _config;

        public static readonly string[] CategoryNames = {
            "YouTube", "TikTok", "Bilibili", "X (Twitter)",
            "Pixiv", "nHentai", "Hitomi.la", "Facebook"
        };

        static PathManager()
        {
            LoadConfig();
        }

        public static AppConfigData Config
        {
            get
            {
                if (_config == null) LoadConfig();
                return _config;
            }
        }

        public static void LoadConfig()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(ConfigFilePath))
                    {
                        string json = File.ReadAllText(ConfigFilePath);
                        _config = JsonConvert.DeserializeObject<AppConfigData>(json) ?? new AppConfigData();
                        return;
                    }
                }
                catch { }
                _config = new AppConfigData();
            }
        }

        public static void SaveConfig()
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                    File.WriteAllText(ConfigFilePath, json);
                }
                catch { }
            }
        }

        public static string GetSelectedBrowser()
        {
            return string.IsNullOrWhiteSpace(Config.SelectedBrowser) ? "chrome" : Config.SelectedBrowser.Trim();
        }

        private static string GetValidPath(string configuredPath, string defaultSubFolder)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
            string portableBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            return Path.Combine(portableBase, defaultSubFolder);
        }

        public static string[] GetAllCategoryPaths()
        {
            return new string[]
            {
                GetValidPath(Config.YoutubePath, "Koxmix_YouTube"),
                GetValidPath(Config.TikTokPath, "Koxmix_TikTok"),
                GetValidPath(Config.BilibiliPath, "Koxmix_Bilibili"),
                GetValidPath(Config.XPath, "Koxmix_X"),
                GetValidPath(Config.PixivPath, "Koxmix_Pixiv"),
                GetValidPath(Config.NHentaiPath, "Koxmix_nHentai"),
                GetValidPath(Config.HitomiPath, "Koxmix_Hitomi"),
                GetValidPath(Config.FacebookPath, "Koxmix_Facebook")
            };
        }

        public static void SaveAllCategoryPaths(string[] paths)
        {
            if (paths == null || paths.Length < 8) return;

            Config.YoutubePath = paths[0]?.Trim() ?? "";
            Config.TikTokPath = paths[1]?.Trim() ?? "";
            Config.BilibiliPath = paths[2]?.Trim() ?? "";
            Config.XPath = paths[3]?.Trim() ?? "";
            Config.PixivPath = paths[4]?.Trim() ?? "";
            Config.NHentaiPath = paths[5]?.Trim() ?? "";
            Config.HitomiPath = paths[6]?.Trim() ?? "";
            Config.FacebookPath = paths[7]?.Trim() ?? "";

            SaveConfig();
        }

        public static HashSet<string> GetAllRootFolders()
        {
            return new HashSet<string>(GetAllCategoryPaths(), StringComparer.OrdinalIgnoreCase);
        }

        public static string GetTargetPathForUrl(string url, bool isMp3 = false)
        {
            var paths = GetAllCategoryPaths();

            if (string.IsNullOrEmpty(url)) return paths[0];
            if (url.Contains("youtube.com") || url.Contains("youtu.be")) return paths[0];
            if (url.Contains("tiktok.com")) return paths[1];
            if (url.Contains("bilibili.com") || url.Contains("bilibili.tv") || url.Contains("b23.tv")) return paths[2];
            if (url.Contains("x.com") || url.Contains("twitter.com")) return paths[3];
            if (url.Contains("pixiv.net") || url.Contains("pixiv.me")) return paths[4];
            if (url.Contains("nhentai.net")) return paths[5];
            if (url.Contains("hitomi.la")) return paths[6];
            if (url.Contains("facebook.com") || url.Contains("fb.me") || url.Contains("fb.watch")) return paths[7];

            return paths[0];
        }
    }
}