using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Koxmix.Utils
{
    public static class UpdateChecker
    {
        private const string GithubUser = "duaypham";
        private const string GithubRepo = "koxmix_media_downloader";

        public static async Task CheckForUpdatesAsync(Form mainForm)
        {
            try
            {
                string apiUrl = $"https://api.github.com/repos/{GithubUser}/{GithubRepo}/releases/latest";

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "C# WinForms Koxmix App");

                var response = await client.GetAsync(apiUrl);
                if (!response.IsSuccessStatusCode) return;

                string jsonResponse = await response.Content.ReadAsStringAsync();
                JObject release = JObject.Parse(jsonResponse);

                string latestTag = release["tag_name"]?.ToString() ?? "";
                string releaseNotes = release["body"]?.ToString() ?? "No release notes.";

                string downloadUrl = "";
                if (release["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        string url = asset["browser_download_url"]?.ToString();
                        if (!string.IsNullOrEmpty(url) && url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = url;
                            break;
                        }
                    }
                }

                Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                string cleanTag = latestTag.TrimStart('v', 'V');

                if (Version.TryParse(cleanTag, out Version latestVersion) && latestVersion > currentVersion)
                {
                    mainForm.Invoke(new Action(async () =>
                    {
                        DialogResult result = MessageBox.Show(
                            mainForm,
                            $"Phát hiện phiên bản mới ({latestTag})!\n\nChi tiết cập nhật:\n{releaseNotes}\n\nBạn có muốn cập nhật tự động ngay bây giờ không?",
                            "Cập nhật phần mềm",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information
                        );

                        if (result == DialogResult.Yes)
                        {
                            if (string.IsNullOrEmpty(downloadUrl))
                            {
                                MessageBox.Show(mainForm, "Không tìm thấy file cài đặt trong bản phát hành.", "Lỗi Cập Nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                return;
                            }

                            MessageBox.Show(mainForm, "Bản cập nhật đang được tải ngầm.\nPhần mềm sẽ tự động khởi động lại sau vài giây, vui lòng không tắt máy!", "Đang tải...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            await DownloadAndInstallUpdateAsync(downloadUrl);
                        }
                    }));
                }
            }
            catch { }
        }

        private static async Task DownloadAndInstallUpdateAsync(string url)
        {
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "Koxmix_Update.exe");

                using (var client = new HttpClient())
                {
                    var data = await client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempFile, data);
                }

                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string processName = Path.GetFileNameWithoutExtension(currentExe);
                string batchFile = Path.Combine(Path.GetTempPath(), "update_koxmix.bat");

                string batchContent = $@"@echo off
title Updating Koxmix...
echo Vui long cho, phan mem dang duoc cap nhat...
timeout /t 2 /nobreak > nul
taskkill /f /im ""{processName}.exe"" > nul 2>&1
:retry
copy /y ""{tempFile}"" ""{currentExe}"" > nul
if %errorlevel% neq 0 (
    timeout /t 1 /nobreak > nul
    goto retry
)
start """" ""{currentExe}""
del ""%~f0""";

                await File.WriteAllTextAsync(batchFile, batchContent);

                Process.Start(new ProcessStartInfo
                {
                    FileName = batchFile,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal,
                    UseShellExecute = true
                });

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cập nhật tự động thất bại: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}