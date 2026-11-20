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
            if (url.Contains("youtube.com") && url.Contains("playlist?list=")) return true;

            if (url.Contains("bilibili.com"))
            {
                if (Regex.IsMatch(url, @"(?:/video/|[?&]bvid=)(BV[a-zA-Z0-9]+)", RegexOptions.IgnoreCase))
                    return false;

                return url.Contains("/series") ||
                       url.Contains("/collection") ||
                       url.Contains("/lists/") ||
                       url.Contains("/medialist/");
            }

            return false;
        }

        public static bool IsBilibiliImagePost(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.Contains("t.bilibili.com/") ||
                   url.Contains("bilibili.com/opus/") ||
                   url.Contains("bilibili.com/read/cv");
        }

        public static bool IsFacebookAlbumOrPost(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.Contains("facebook.com") && !url.Contains("fb.me")) return false;
            if (url.Contains("/videos/") || url.Contains("/reel/") || url.Contains("/watch") || url.Contains("fb.watch") || url.Contains("/share/v/") || url.Contains("/share/r/") || url.Contains("video.php")) return false;
            if (!url.Contains("/posts/") && !url.Contains("/photos/") && !url.Contains("/photo") && !url.Contains("fbid=") && !url.Contains("/share/p/") && !url.Contains("pcb.") && !url.Contains("permalink.php") && !url.Contains("story.php"))
            {
                return url.Contains("/groups/");
            }
            return true;
        }

        public static async Task<string> NormalizeUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();

            url = url.Replace("yewtu.be/", "www.youtube.com/")
                     .Replace("fxtwitter.com/", "x.com/")
                     .Replace("vxtwitter.com/", "x.com/")
                     .Replace("fixupx.com/", "x.com/")
                     .Replace("mobile.twitter.com/", "x.com/")
                     .Replace("mobile.x.com/", "x.com/")
                     .Replace("m.bilibili.com/", "www.bilibili.com/");

            url = Regex.Replace(url, @"https?://(?:m|mbasic|web)\.facebook\.com/", "https://www.facebook.com/", RegexOptions.IgnoreCase);

            if (url.Contains("vm.tiktok.com") || url.Contains("vt.tiktok.com") ||
                url.Contains("b23.tv/") || url.Contains("bili.im/") ||
                url.Contains("fb.watch/") || url.Contains("fb.me/") || url.Contains("facebook.com/share/"))
            {
                string resolvedUrl = await ResolveRedirectAsync(url);
                if (!string.IsNullOrEmpty(resolvedUrl)) url = resolvedUrl;
            }

            if (url.Contains("tiktok.com") && url.Contains("?"))
                url = url.Substring(0, url.IndexOf("?"));

            if ((url.Contains("x.com/") || url.Contains("twitter.com/")) && url.Contains("?"))
                url = url.Substring(0, url.IndexOf("?"));

            if (((url.Contains("youtube.com/watch") && url.Contains("v=")) || url.Contains("youtu.be/")) &&
                (url.Contains("&list=") || url.Contains("?list=")))
            {
                string vidId = ExtractMediaId(url);
                if (!string.IsNullOrEmpty(vidId))
                {
                    return $"https://www.youtube.com/watch?v={vidId}";
                }
            }

            if (url.Contains("youtube.com/shorts/") || url.Contains("youtube.com/live/"))
            {
                string vidId = ExtractMediaId(url);
                if (!string.IsNullOrEmpty(vidId))
                {
                    return $"https://www.youtube.com/watch?v={vidId}";
                }
            }

            if (url.Contains("bilibili.com"))
            {
                var bvMatch = Regex.Match(url, @"(?:/video/|[?&]bvid=)(BV[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                if (bvMatch.Success)
                {
                    string bvid = bvMatch.Groups[1].Value;
                    string pageParam = ExtractQueryParam(url, "p");
                    return !string.IsNullOrEmpty(pageParam) && int.TryParse(pageParam, out int pNum) && pNum > 1
                        ? $"https://www.bilibili.com/video/{bvid}?p={pNum}"
                        : $"https://www.bilibili.com/video/{bvid}";
                }

                var avMatch = Regex.Match(url, @"(?:/video/av|[?&]aid=)(\d+)", RegexOptions.IgnoreCase);
                if (avMatch.Success)
                {
                    string aid = avMatch.Groups[1].Value;
                    string pageParam = ExtractQueryParam(url, "p");
                    return !string.IsNullOrEmpty(pageParam) && int.TryParse(pageParam, out int pNum) && pNum > 1
                        ? $"https://www.bilibili.com/video/av{aid}?p={pNum}"
                        : $"https://www.bilibili.com/video/av{aid}";
                }

                if ((IsBilibiliImagePost(url) || url.Contains("/bangumi/play/")) && url.Contains("?"))
                {
                    return url.Substring(0, url.IndexOf("?"));
                }
            }

            if (url.Contains("bilibili.tv") && url.Contains("?"))
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
            string currentUrl = url;
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                for (int hop = 0; hop < 4; hop++)
                {
                    using HttpResponseMessage response = await client.GetAsync(currentUrl);
                    if (response.StatusCode >= HttpStatusCode.MultipleChoices && response.StatusCode <= (HttpStatusCode)399 && response.Headers.Location != null)
                    {
                        string nextUrl = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location.AbsoluteUri
                            : new Uri(new Uri(currentUrl), response.Headers.Location).AbsoluteUri;

                        if (nextUrl.Contains("login.php")) break;
                        currentUrl = nextUrl;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch { }
            return currentUrl;
        }

        public static string ExtractQueryParam(string url, string paramName)
        {
            Match match = Regex.Match(url, "[?&]" + Regex.Escape(paramName) + "=([^&#]*)");
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }

        public static string ExtractMediaId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var ytMatch = Regex.Match(url, @"(?:youtu\.be\/|\/(?:v|embed|shorts|live)\/|watch\?v=|&v=)([0-9A-Za-z_-]{11})");
            if (ytMatch.Success) return ytMatch.Groups[1].Value;

            var biliBvMatch = Regex.Match(url, @"(?:bvid=|/video/)(BV[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            if (biliBvMatch.Success)
            {
                string p = ExtractQueryParam(url, "p");
                return !string.IsNullOrEmpty(p) && p != "1" ? $"{biliBvMatch.Groups[1].Value}_p{p}" : biliBvMatch.Groups[1].Value;
            }

            var biliOtherMatch = Regex.Match(url, @"(?:/video/(av\d+)|/bangumi/play/(ep\d+|ss\d+)|/(?:opus|read/cv)(\d+)|t\.bilibili\.com/(\d+)|bilibili\.tv/(?:[a-zA-Z]{2}/)?(?:video|play)/(\d+))", RegexOptions.IgnoreCase);
            if (biliOtherMatch.Success)
            {
                for (int i = 1; i < biliOtherMatch.Groups.Count; i++)
                {
                    if (biliOtherMatch.Groups[i].Success) return biliOtherMatch.Groups[i].Value;
                }
            }

            var tiktokMatch = Regex.Match(url, @"(?:/video/|/v/|/photo/)(\d+)");
            if (tiktokMatch.Success) return tiktokMatch.Groups[1].Value;

            var xMatch = Regex.Match(url, @"(?:status(?:es)?|spaces)/([0-9a-zA-Z]+)");
            if (xMatch.Success) return xMatch.Groups[1].Value;

            var fbMatch = Regex.Match(url, @"(?:/videos/|/reel/|/watch\?v=|/watch/\?v=|v=|/share/v/|/share/r/)([a-zA-Z0-9_]+)");
            if (fbMatch.Success && (url.Contains("facebook.com") || url.Contains("fb.watch") || url.Contains("fb.me"))) return fbMatch.Groups[1].Value;

            var pixivMatch = Regex.Match(url, @"(?:illust_id=|artworks/)(\d+)");
            if (pixivMatch.Success) return pixivMatch.Groups[1].Value;

            var nHentaiMatch = Regex.Match(url, @"g/(\d+)");
            if (nHentaiMatch.Success) return nHentaiMatch.Groups[1].Value;

            var hitomiMatch = Regex.Match(url, @"(\d{5,})(?:\.html|/|$)");
            if (hitomiMatch.Success && url.Contains("hitomi.la")) return hitomiMatch.Groups[1].Value;

            return null;
        }

        public static string ExtractPlaylistId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            var match = Regex.Match(url, @"[?&]list=([^#&?]+)");
            if (match.Success) return match.Groups[1].Value;

            var biliMatch = Regex.Match(url, @"(?:/series/|/collection/|/lists/|/medialist/play/|season_id=|sid=|business_id=)(\d+)");
            if (biliMatch.Success) return biliMatch.Groups[1].Value;

            return null;
        }

        public static string ExtractFbPostId(string url)
        {
            try
            {
                var match = Regex.Match(url, @"(?:fbid=|story_fbid=|/posts/|/permalink/|/videos/|/share/p/|/groups/[^/]+/permalink/|set=(?:a\.|pcb\.)?)([a-zA-Z0-9_]+)");
                if (match.Success) return match.Groups[1].Value;

                match = Regex.Match(url, @"/([a-zA-Z0-9_]+)/?$");
                if (match.Success) return match.Groups[1].Value;
            }
            catch { }
            return DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
    }
}