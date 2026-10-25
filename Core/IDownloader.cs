using System.Threading.Tasks;
using Koxmix.Models;

namespace Koxmix.Core
{
    public interface IDownloader
    {
        /// <summary>
        /// Kiểm tra xem bộ tải này có hỗ trợ URL được truyền vào hay không.
        /// </summary>
        bool CanHandle(string url);

        /// <summary>
        /// Thực thi toàn bộ quy trình: Kiểm tra trùng lặp -> Tải xuống -> Trả kết quả.
        /// </summary>
        Task DownloadAsync(DownloadContext ctx);
    }
}