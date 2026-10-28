using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Koxmix.Downloaders;
using Koxmix.Models;
using Koxmix.Utils;

namespace Koxmix.Core
{
    public class DownloadRouter
    {
        private readonly List<IDownloader> _downloaders;

        public DownloadRouter()
        {
            _downloaders = new List<IDownloader>
            {
                new FacebookDownloader(),
                new XDownloader(),
                new RedditDownloader(),
                new PixivDownloader(),
                new NHentaiDownloader(),
                new HitomiDownloader(),
                new YtDlpDownloader()
            };
        }

        public async Task ProcessDownloadAsync(DownloadContext ctx)
        {
            ctx.OnStatusChanged?.Invoke("Normalizing URL...");
            ctx.Url = await UrlUtils.NormalizeUrlAsync(ctx.Url);

            string activeTargetFolder = ctx.TargetFolder;

            try
            {
                FormatUtils.EnsureDiskSpace(activeTargetFolder, 1073741824L);

                IDownloader matchedDownloader = _downloaders.First(d => d.CanHandle(ctx.Url));

                await ExecuteWithRetryAsync(async () =>
                {
                    ctx.OnStatusChanged?.Invoke("Initializing...");
                    await matchedDownloader.DownloadAsync(ctx);
                }, ctx);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ctx.Token.IsCancellationRequested)
            {
                await CleanupUnfinishedFilesAsync(activeTargetFolder, ctx.Url);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupUnfinishedFilesAsync(activeTargetFolder, ctx.Url);
                ctx.OnError?.Invoke(ex);
            }
        }

        private async Task ExecuteWithRetryAsync(Func<Task> action, DownloadContext ctx, int maxRetries = 3)
        {
            int currentAttempt = 0;
            while (true)
            {
                currentAttempt++;
                try
                {
                    await action();
                    break;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException || ctx.Token.IsCancellationRequested) throw;

                    bool isNetworkGlitch = ex is HttpRequestException ||
                                           ex.InnerException is SocketException ||
                                           ex.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           ex.Message.IndexOf("Connection reset", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (currentAttempt < maxRetries && isNetworkGlitch)
                    {
                        int delaySeconds = (int)Math.Pow(2, currentAttempt) + Random.Shared.Next(1, 4);
                        for (int sec = delaySeconds; sec > 0; sec--)
                        {
                            ctx.OnStatusChanged?.Invoke($"Network glitch, retrying {currentAttempt}/{maxRetries} in {sec}s...");
                            await Task.Delay(1000, ctx.Token);
                        }
                        continue;
                    }
                    throw;
                }
            }
        }

        private async Task CleanupUnfinishedFilesAsync(string targetFolder, string url)
        {
            await Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500);

                    if (!Directory.Exists(targetFolder)) return;

                    string mediaId = UrlUtils.ExtractMediaId(url);
                    if (string.IsNullOrEmpty(mediaId)) return;

                    var files = new DirectoryInfo(targetFolder).GetFiles("*.*", SearchOption.TopDirectoryOnly);
                    foreach (FileInfo f in files)
                    {
                        bool isTempExt = f.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                                         f.Extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                                         f.Extension.Equals(".temp", StringComparison.OrdinalIgnoreCase) ||
                                         f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                                         f.Extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase);

                        bool isYtDlpFrag = Regex.IsMatch(f.Name, @"\.f\d+\.") || f.Name.Contains("-Frag");

                        if (f.Name.Contains(mediaId) && (isTempExt || isYtDlpFrag))
                        {
                            FormatUtils.TryDeleteFile(f.FullName);
                        }
                    }
                }
                catch { }
            });
        }
    }
}