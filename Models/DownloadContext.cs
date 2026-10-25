using System;
using System.Threading;

namespace Koxmix.Models
{
    public class DownloadContext
    {
        public string Url { get; set; }
        public bool IsMp3 { get; set; }
        public bool ForceRedownload { get; set; }
        public string TargetFolder { get; set; }

        public DateTime StartTime { get; set; }
        public CancellationToken Token { get; set; }

        public Action<string> OnStatusChanged { get; set; }
        public Action<string, string> OnProgressChanged { get; set; }
        public Action<string, string, string> OnSuccess { get; set; }
        public Action<Exception> OnError { get; set; }
    }
}