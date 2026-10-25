using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Koxmix.Utils
{
    public static class UrlUtils
    {
        public static bool IsPlaylist(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return (url.Contains("youtube.com") && (url.Contains("playlist?list=") || url.Contains("&list="))) ||
                   (url.Contains("bilibili.com") && (url.Contains("p=") || url.Contains("/series/")));
        }

        public static bool IsFacebookAlbumOrPost(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.Contains("facebook.com") && !url.Contains("fb.me")) return false;
            if (url.Contains("/videos/") || url.Contains("/reel/") || url.Contains("/watch") || url.Contains("fb.watch")) return false;
            if (!url.Contains("/posts/") && !url.Contains("/photos/") && !url.Contains("pcb.") && !url.Contains("permalink.php") && !url.Contains("story.php"))
            {
                return url.Contains("/groups/");
            }
            return true;
        }

        public static async Task<string> NormalizeUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            if (url.Contains("tiktok.com") && url.Contains("?"))
                url = url.Substring(0, url.IndexOf("?"));

            if ((url.Contains("reddit.com") || url.Contains("redd.it")) && url.Contains("?"))
                url = url.Substring(0, url.IndexOf("?"));

            if (url.Contains("vm.tiktok.com") || url.Contains("vt.tiktok.com"))
            {
                string resolvedUrl = await ResolveRedirectAsync(url);
                if (!string.IsNullOrEmpty(resolvedUrl)) url = resolvedUrl;
                if (url.Contains("?")) url = url.Substring(0, url.IndexOf("?"));
            }

            if (url.Contains("youtube.com/watch") && url.Contains("v=") && (url.Contains("&list=") || url.Contains("?list=")))
            {
                string vidId = ExtractMediaId(url);
                if (!string.IsNullOrEmpty(vidId))
                {
                    return $"https://www.youtube.com/watch?v={vidId}";
                }
            }

            if (url.Contains("youtube.com/shorts/"))
            {
                try
                {
                    string[] parts = url.Split(new[] { "/shorts/" }, StringSplitOptions.None);
                    if (parts.Length > 1)
                    {
                        string restOfUrl = parts[1];
                        string shortsId = restOfUrl.Split('?')[0].Split('/')[0];
                        string queryParams = restOfUrl.Contains("?") ? restOfUrl.Substring(restOfUrl.IndexOf("?")) : "";

                        if (!string.IsNullOrEmpty(shortsId))
                        {
                            return "https://www.youtube.com/watch?v=" + shortsId + queryParams;
                        }
                    }
                }
                catch { }
            }

            if (url.Contains("bilibili.com") && url.Contains("?") && !url.Contains("?p=") && !url.Contains("&p=") && !url.Contains("bvid="))
            {
                return url.Substring(0, url.IndexOf("?"));
            }

            if (url.Contains("facebook.com/story.php"))
            {
                try
                {
                    string fbid = ExtractQueryParam(url, "story_fbid");
                    string id = ExtractQueryParam(url, "id");
                    if (!string.IsNullOrEmpty(fbid) && !string.IsNullOrEmpty(id))
                    {
                        string permalinkUrl = $"https://www.facebook.com/permalink.php?story_fbid={fbid}&id={id}";
                        string resolvedUrl = await ResolveRedirectAsync(permalinkUrl);
                        return !string.IsNullOrEmpty(resolvedUrl) ? resolvedUrl : permalinkUrl;
                    }
                }
                catch { }
            }

            return url;
        }

        public static async Task<string> ResolveRedirectAsync(string url)
        {
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                HttpResponseMessage response = await client.GetAsync(url);
                if (response.StatusCode >= HttpStatusCode.MultipleChoices && response.StatusCode <= (HttpStatusCode)399 && response.Headers.Location != null)
                {
                    string resolved = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.AbsoluteUri
                        : new Uri(new Uri(url), response.Headers.Location).AbsoluteUri;

                    if (resolved.Contains("login.php")) return url;
                    return resolved;
                }
            }
            catch { }
            return url;
        }

        public static string ExtractQueryParam(string url, string paramName)
        {
            Match match = Regex.Match(url, "[?&]" + Regex.Escape(paramName) + "=([^&#]*)");
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }

        public static string ExtractMediaId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var ytMatch = Regex.Match(url, @"(?:youtu\.be\/|v\/|u\/\w\/|embed\/|watch\?v=|&v=)([^#&?]{11})");
            if (ytMatch.Success) return ytMatch.Groups[1].Value;

            var biliMatch = Regex.Match(url, @"(?:bvid=|/video/)(BV\w+)");
            if (biliMatch.Success) return biliMatch.Groups[1].Value;

            var tiktokMatch = Regex.Match(url, @"(?:/video/|/v/|/photo/)(\d+)");
            if (tiktokMatch.Success) return tiktokMatch.Groups[1].Value;

            var redditMatch = Regex.Match(url, @"(?:comments/|redd\.it/|gallery/)(\w+)");
            if (redditMatch.Success) return redditMatch.Groups[1].Value;

            var xMatch = Regex.Match(url, @"status(?:es)?/(\d+)");
            if (xMatch.Success) return xMatch.Groups[1].Value;

            var fbMatch = Regex.Match(url, @"(?:/videos/|/reel/|/watch\?v=|/watch/\?v=|v=)([a-zA-Z0-9_]+)");
            if (fbMatch.Success && url.Contains("facebook.com")) return fbMatch.Groups[1].Value;

            var pixivMatch = Regex.Match(url, @"(?:illust_id=|artworks/)(\d+)");
            if (pixivMatch.Success) return pixivMatch.Groups[1].Value;

            var nHentaiMatch = Regex.Match(url, @"g/(\d+)");
            if (nHentaiMatch.Success) return nHentaiMatch.Groups[1].Value;

            var hitomiMatch = Regex.Match(url, @"(?:galleries/|cg/|manga/|doujinshi/)[^/]*?(\d+)(?:\.html|/|$)");
            if (hitomiMatch.Success && url.Contains("hitomi.la")) return hitomiMatch.Groups[1].Value;

            return null;
        }

        public static string ExtractPlaylistId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var match = Regex.Match(url, @"[?&]list=([^#&?]+)");
            if (match.Success) return match.Groups[1].Value;

            var biliMatch = Regex.Match(url, @"(?:/series/|/collection/|season_id=|/ep)(\d+)");
            if (biliMatch.Success) return biliMatch.Groups[1].Value;

            return null;
        }

        public static string ExtractFbPostId(string url)
        {
            try
            {
                var match = Regex.Match(url, @"(?:fbid=|story_fbid=|/posts/|/permalink/|/videos/|/groups/[^/]+/permalink/|set=(?:a\.|pcb\.)?)([a-zA-Z0-9_]+)");
                if (match.Success) return match.Groups[1].Value;

                match = Regex.Match(url, @"/([a-zA-Z0-9_]+)/?$");
                if (match.Success) return match.Groups[1].Value;
            }
            catch { }
            return DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
    }
}