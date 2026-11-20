# Koxmix Media Downloader

A fast, lightweight, dark-themed Windows desktop media and gallery downloader built with **C# (.NET WinForms)**. Koxmix integrates **yt-dlp**, **gallery-dl**, **FFmpeg**, **Deno**, and an embedded **Microsoft Edge WebView2** engine to download videos, audio, illustrations, and manga albums with unified file naming.

---

## Key Features

- **Unified File & Archive Naming:** Every downloaded file or `.zip` archive follows a clean, consistent naming convention:
  ```text
  [Platform] Uploader - Title [ID].ext
  ```
- **Built-in Chromium Cookie Extractor:** Integrated WebView2 browser (`Cookies` button) lets you log into restricted accounts and automatically exports clean session cookies for age-restricted (R-18) or Cloudflare-protected sites.
- **Self-Updating Core Engines:** Automatically downloads and updates `yt-dlp`, `gallery-dl`, `FFmpeg`, and `Deno` on startup inside a hidden `Koxmix_Data` workspace.
- **High-Speed Multi-Threaded Downloads:** Concurrent chunk and image downloading (up to 16 threads for manga galleries) with automatic retry on network glitches.
- **Pixiv Ugoira Animation Support:** Automatically fetches Ugoira frame metadata and converts animations into smooth `.gif` files via FFmpeg.
- **Smart Subtitle Embedding:** Downloads and embeds priority subtitles (`.srt`) directly into `.mp4` containers while organizing external subtitle tracks cleanly.
- **Modern Dark UI & Quick Workflow:** Supports batch URL pasting, `Ctrl + V` instant download trigger, thumbnail previews, persistent download history, and custom save folders per platform.

---

## Supported Platforms

| Platform | Supported Content | Output Format |
| :--- | :--- | :--- |
| **YouTube** | Videos, Shorts, Livestreams, Playlists, Audio-only | `.mp4`, `.mp3`, `.zip` (Playlists) |
| **TikTok** | Videos (watermark-free), Photo slideshows, Short links (`vt.tiktok.com`) | `.mp4`, `.mp3`, `.zip` |
| **Bilibili** | Videos (`BV`/`av`), Multi-part (`?p=`), Short links (`b23.tv`), Opus/Image posts, Series | `.mp4`, `.mp3`, `.zip` |
| **X (Twitter)** | Multi-image posts (up to 20 images), Videos, GIFs, Twitter Spaces | `.jpg`/`.png`, `.mp4`, `.mp3`, `.zip` |
| **Facebook** | Public/Group videos, Reels, Watch, Photo posts & Albums | `.mp4`, `.jpg`, `.zip` |
| **Pixiv** | Single artworks, Multi-page manga, R-18 works, Animated Ugoira | `.jpg`/`.png`, `.gif`, `.zip` |
| **nHentai** | Full galleries with automatic Chromium Cloudflare bypass | `.zip` (`0001.ext`...) |
| **Hitomi.la** | Full galleries with metadata extraction & `info.txt` generation | `.zip` (`0001.ext`...) |

---

## Prerequisites & Building from Source

### System Requirements
- **OS:** Windows 10 or Windows 11 (x64)
- **Runtime:** [.NET Desktop Runtime](https://dotnet.microsoft.com/download) & [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on Windows 10/11)

### Build Instructions
1. Clone the repository:
   ```bash
   git clone [https://github.com/duaypham/koxmix_media_downloader.git](https://github.com/duaypham/koxmix_media_downloader.git)
   ```
2. Open the solution file (`.sln`) in **Visual Studio 2022**.
3. Restore NuGet packages (`Newtonsoft.Json`, `YoutubeDLSharp`, `Microsoft.Web.WebView2`).
4. Select **Release | x64** and build the project (`Ctrl + Shift + B`).

---

## Usage

1. **Launch Koxmix:** On first run, wait a few seconds for the status bar to finish downloading `yt-dlp`, `gallery-dl`, `FFmpeg`, and `Deno`.
2. **Paste Links:** Paste one or multiple URLs (one per line) into the input box and click **Download** (or press `Ctrl + V` anywhere in the window to paste and start immediately).
3. **Choose Format:** Toggle between **Download Videos** and **Download MP3** for supported video platforms.
4. **Unlock Restricted Content:** Click **Cookies**, select a platform from the dropdown, log in, and click **SAVE COOKIES & CLOSE**.
5. **Custom Folders:** Click **Settings** to configure custom save directories for each of the 8 platforms.
6. **Manage Tasks:** Right-click any download card to open the file, open its folder, copy the path, stop, redownload, or delete from disk.

---

## Disclaimer

This tool is intended for personal archival and educational purposes only. Please respect the Terms of Service of each platform and the copyrights of content creators.
