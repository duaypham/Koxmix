using System.Threading.Tasks;
using Koxmix.Models;

namespace Koxmix.Core
{
    public interface IDownloader
    {
        bool CanHandle(string url);

        Task DownloadAsync(DownloadContext ctx);
    }
}