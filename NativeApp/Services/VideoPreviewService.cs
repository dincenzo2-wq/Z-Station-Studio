using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZStationStudio.Models;

namespace ZStationStudio.Services
{
    public class VideoPreviewService
    {
        private string _cacheDir;
        private readonly string? _ffmpegPath;
        private readonly string? _ffprobePath;

        private readonly SemaphoreSlim _thumbGate = new(3, 3);
        private readonly SemaphoreSlim _stripGate = new(2, 2);

        private readonly ConcurrentDictionary<string, byte> _inFlightThumbs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inFlightStrips = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ImageSource> _memoryThumbs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ImageSource[]> _memoryStrips = new(StringComparer.OrdinalIgnoreCase);

        private const int StripFrameCount = 16;
        private const int StripFrameWidth = 280;
        private const int StripFrameHeight = 158;

        public string CacheDirectory => _cacheDir;

        public VideoPreviewService()
        {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Z-station",
                "VideoCache");

            EnsureCacheDirExists();

            _ffmpegPath = FindExecutable("ffmpeg.exe");
            _ffprobePath = FindExecutable("ffprobe.exe");
        }

        public void SetCacheDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            _cacheDir = dir;
            EnsureCacheDirExists();
        }

        public void ClearMemoryCache()
        {
            _memoryThumbs.Clear();
            _memoryStrips.Clear();
        }

        private void EnsureCacheDirExists()
        {
            try
            {
                if (!Directory.Exists(_cacheDir))
                {
                    Directory.CreateDirectory(_cacheDir);
                }
            }
            catch { }
        }

        private static string? FindExecutable(string exeName)
        {
            try
            {
                string winGetLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links", exeName);
                if (File.Exists(winGetLink)) return winGetLink;

                string appLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
                if (File.Exists(appLocal)) return appLocal;

                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            string candidate = Path.Combine(dir.Trim(), exeName);
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool IsCloudPlaceholder(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var attr = File.GetAttributes(filePath);
                // FILE_ATTRIBUTE_OFFLINE = 0x1000, FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000,
                // FILE_ATTRIBUTE_UNPINNED = 0x00100000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000
                const int CloudMask = 0x1000 | 0x00040000 | 0x00100000 | 0x00400000;
                return (((int)attr) & CloudMask) != 0;
            }
            catch
            {
                return false;
            }
        }

        private string GetCacheKey(string filePath)
        {
            string norm = DataManager.NormalizePath(filePath);
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
            return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
        }

        public void QueueVisibleItems(IEnumerable<SfxItem> items)
        {
            var visualList = new List<SfxItem>();
            foreach (var item in items)
            {
                if (item.IsVideo || item.IsImage || item.IsDesign) visualList.Add(item);
            }
            if (visualList.Count == 0) return;

            _ = Task.Run(async () =>
            {
                // Pass 1: Immediately populate any items that already have cached disk/memory thumbnails & strips
                var remaining = new List<SfxItem>();
                foreach (var item in visualList)
                {
                    string key = GetCacheKey(item.FilePath);
                    string thumbPath = Path.Combine(_cacheDir, $"{key}_thumb.jpg");
                    string stripPath = Path.Combine(_cacheDir, $"{key}_strip.jpg");
                    string metaPath = Path.Combine(_cacheDir, $"{key}_meta.txt");

                    if (File.Exists(metaPath) && ((item.IsVideo && item.Duration <= 0) || string.IsNullOrEmpty(item.ResolutionBadge)))
                    {
                        TryApplyCachedMeta(item, metaPath);
                    }

                    if (item.ThumbnailSource == null && File.Exists(thumbPath))
                    {
                        var diskImg = LoadBitmapFromDisk(thumbPath);
                        if (diskImg != null)
                        {
                            _memoryThumbs[key] = diskImg;
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = diskImg);
                        }
                    }

                    if (item.IsVideo && item.ScrubFrames == null && File.Exists(stripPath))
                    {
                        var diskFrames = LoadStripFramesFromDisk(stripPath);
                        if (diskFrames != null && diskFrames.Length > 0)
                        {
                            _memoryStrips[key] = diskFrames;
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ScrubFrames = diskFrames);
                        }
                    }

                    if (item.ThumbnailSource == null || (item.IsVideo && item.ScrubFrames == null))
                    {
                        remaining.Add(item);
                    }
                }

                // Pass 2: Process local (non-cloud) files first, then cloud placeholders
                var localFirst = new List<SfxItem>();
                var cloudLater = new List<SfxItem>();
                foreach (var item in remaining)
                {
                    bool isCloud = IsCloudPlaceholder(item.FilePath);
                    Application.Current?.Dispatcher.InvokeAsync(() => item.IsCloudOnly = isCloud);
                    if (isCloud) cloudLater.Add(item);
                    else localFirst.Add(item);
                }

                foreach (var item in localFirst)
                {
                    await EnsureThumbnailAsync(item, allowCloudHydrate: false).ConfigureAwait(false);
                    if (item.IsVideo)
                    {
                        await EnsureScrubStripAsync(item, allowCloudHydrate: false).ConfigureAwait(false);
                    }
                }

                foreach (var item in cloudLater)
                {
                    await EnsureThumbnailAsync(item, allowCloudHydrate: false).ConfigureAwait(false);
                }
            });
        }

        public async Task EnsureVideoPreviewAsync(SfxItem item, bool allowCloudHydrate = false)
        {
            await EnsureThumbnailAsync(item, allowCloudHydrate).ConfigureAwait(false);
            if (item.IsVideo)
            {
                await EnsureScrubStripAsync(item, allowCloudHydrate).ConfigureAwait(false);
            }
        }

        public async Task EnsureThumbnailAsync(SfxItem item, bool allowCloudHydrate = false)
        {
            if ((!item.IsVideo && !item.IsImage && !item.IsDesign) || string.IsNullOrEmpty(item.FilePath)) return;

            string key = GetCacheKey(item.FilePath);
            if (_memoryThumbs.TryGetValue(key, out var cachedImg))
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    item.ThumbnailSource = cachedImg;
                });
            }

            string thumbPath = Path.Combine(_cacheDir, $"{key}_thumb.jpg");
            string metaPath = Path.Combine(_cacheDir, $"{key}_meta.txt");

            // Load cached metadata if present
            if (File.Exists(metaPath) && ((item.IsVideo && item.Duration <= 0) || string.IsNullOrEmpty(item.ResolutionBadge)))
            {
                TryApplyCachedMeta(item, metaPath);
            }

            // Load cached disk thumbnail if present
            if (item.ThumbnailSource == null && File.Exists(thumbPath))
            {
                var diskImg = LoadBitmapFromDisk(thumbPath);
                if (diskImg != null)
                {
                    _memoryThumbs[key] = diskImg;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        item.ThumbnailSource = diskImg;
                    });
                    return;
                }
            }

            if (item.ThumbnailSource != null && (item.IsVideo ? item.Duration > 0 : !string.IsNullOrEmpty(item.ResolutionBadge))) return;

            if (!_inFlightThumbs.TryAdd(key, 1)) return;

            try
            {
                await _thumbGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool isCloud = IsCloudPlaceholder(item.FilePath);
                    Application.Current?.Dispatcher.InvokeAsync(() => item.IsCloudOnly = isCloud);

                    if (isCloud && !allowCloudHydrate)
                    {
                        // Try Windows Shell cached thumbnail (does not hydrate Dropbox file when INCACHEONLY is used)
                        var shellBmp = TryGetShellThumbnail(item.FilePath, inCacheOnly: true);
                        if (shellBmp != null)
                        {
                            _memoryThumbs[key] = shellBmp;
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = shellBmp);
                        }
                        return;
                    }

                    if (isCloud && allowCloudHydrate)
                    {
                        await HydrateCloudFileAsync(item.FilePath).ConfigureAwait(false);
                        isCloud = IsCloudPlaceholder(item.FilePath);
                        Application.Current?.Dispatcher.InvokeAsync(() => item.IsCloudOnly = isCloud);
                    }

                    // Handle IMAGE files
                    if (item.IsImage)
                    {
                        var (directBmp, imgW, imgH) = TryLoadDirectImage(item.FilePath);
                        if (directBmp != null)
                        {
                            _memoryThumbs[key] = directBmp;
                            if (directBmp is BitmapSource bs)
                            {
                                SaveBitmapSourceAsJpeg(bs, thumbPath);
                            }
                            string res = imgW > 0 && imgH > 0 ? $"{imgW}x{imgH}" : string.Empty;
                            if (!string.IsNullOrEmpty(res))
                            {
                                try { await File.WriteAllTextAsync(metaPath, $"0|{imgW}|{imgH}").ConfigureAwait(false); } catch { }
                            }
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                item.ThumbnailSource = directBmp;
                                if (!string.IsNullOrEmpty(res)) item.ResolutionBadge = res;
                            });
                            return;
                        }

                        // Fallback to Shell thumbnail
                        var shellImg = TryGetShellThumbnail(item.FilePath, inCacheOnly: false);
                        if (shellImg != null)
                        {
                            _memoryThumbs[key] = shellImg;
                            if (shellImg is BitmapSource sbs)
                            {
                                SaveBitmapSourceAsJpeg(sbs, thumbPath);
                            }
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = shellImg);
                            return;
                        }
                    }

                    // Handle DESIGN files (PSD, AI, PDF, etc.)
                    if (item.IsDesign)
                    {
                        // 1. Try Windows Shell thumbnail (works great if Adobe/PDF shell handlers exist)
                        var shellDsg = TryGetShellThumbnail(item.FilePath, inCacheOnly: false);
                        if (shellDsg != null)
                        {
                            _memoryThumbs[key] = shellDsg;
                            if (shellDsg is BitmapSource dsbs)
                            {
                                SaveBitmapSourceAsJpeg(dsbs, thumbPath);
                            }
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = shellDsg);
                            return;
                        }

                        // 2. Fallback to ffmpeg (ffmpeg can decode PSD, PDF if installed)
                        if (!File.Exists(thumbPath) && !string.IsNullOrEmpty(_ffmpegPath))
                        {
                            await ExtractSingleThumbWithFfmpegAsync(item.FilePath, thumbPath, 0).ConfigureAwait(false);
                            if (File.Exists(thumbPath))
                            {
                                var dsgBmp = LoadBitmapFromDisk(thumbPath);
                                if (dsgBmp != null)
                                {
                                    _memoryThumbs[key] = dsgBmp;
                                    Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = dsgBmp);
                                    return;
                                }
                            }
                        }

                        // 3. Try Direct Image load (e.g. some PSDs/TIFFs can be opened by WIC)
                        var (directDsg, dW, dH) = TryLoadDirectImage(item.FilePath);
                        if (directDsg != null)
                        {
                            _memoryThumbs[key] = directDsg;
                            if (directDsg is BitmapSource bs)
                            {
                                SaveBitmapSourceAsJpeg(bs, thumbPath);
                            }
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = directDsg);
                            return;
                        }
                        return;
                    }

                    // Video handling
                    if (item.IsVideo)
                    {
                        // 1. Probe duration & resolution
                        var (dur, width, height) = await ProbeVideoAsync(item.FilePath).ConfigureAwait(false);
                        if (dur > 0 || width > 0)
                        {
                            try
                            {
                                await File.WriteAllTextAsync(metaPath, $"{dur.ToString(CultureInfo.InvariantCulture)}|{width}|{height}").ConfigureAwait(false);
                            }
                            catch { }

                            string resBadge = GetResolutionLabel(width, height);
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                if (dur > 0) item.Duration = dur;
                                if (!string.IsNullOrEmpty(resBadge)) item.ResolutionBadge = resBadge;
                            });
                        }

                        // 2. Extract poster thumbnail using ffmpeg
                        if (!File.Exists(thumbPath) && !string.IsNullOrEmpty(_ffmpegPath))
                        {
                            double seekSec = dur > 1.2 ? Math.Min(1.5, dur * 0.2) : 0.0;
                            await ExtractSingleThumbWithFfmpegAsync(item.FilePath, thumbPath, seekSec).ConfigureAwait(false);
                        }

                        if (File.Exists(thumbPath))
                        {
                            var bmp = LoadBitmapFromDisk(thumbPath);
                            if (bmp != null)
                            {
                                _memoryThumbs[key] = bmp;
                                Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = bmp);
                                return;
                            }
                        }

                        // Fallback to Windows Shell thumbnail
                        var fallbackBmp = TryGetShellThumbnail(item.FilePath, inCacheOnly: false);
                        if (fallbackBmp != null)
                        {
                            _memoryThumbs[key] = fallbackBmp;
                            Application.Current?.Dispatcher.InvokeAsync(() => item.ThumbnailSource = fallbackBmp);
                        }
                    }
                }
                finally
                {
                    _thumbGate.Release();
                }
            }
            finally
            {
                _inFlightThumbs.TryRemove(key, out _);
            }
        }

        public async Task EnsureScrubStripAsync(SfxItem item, bool allowCloudHydrate = false)
        {
            if (!item.IsVideo || string.IsNullOrEmpty(item.FilePath)) return;
            if (item.ScrubFrames != null && item.ScrubFrames.Length > 0) return;

            string key = GetCacheKey(item.FilePath);
            if (_memoryStrips.TryGetValue(key, out var memFrames))
            {
                Application.Current?.Dispatcher.InvokeAsync(() => item.ScrubFrames = memFrames);
                return;
            }

            string stripPath = Path.Combine(_cacheDir, $"{key}_strip.jpg");
            if (File.Exists(stripPath))
            {
                var diskFrames = LoadStripFramesFromDisk(stripPath);
                if (diskFrames != null && diskFrames.Length > 0)
                {
                    _memoryStrips[key] = diskFrames;
                    Application.Current?.Dispatcher.InvokeAsync(() => item.ScrubFrames = diskFrames);
                    return;
                }
            }

            if (string.IsNullOrEmpty(_ffmpegPath)) return;

            bool isCloud = IsCloudPlaceholder(item.FilePath);
            if (isCloud && !allowCloudHydrate) return;

            if (!_inFlightStrips.TryAdd(key, 1)) return;

            try
            {
                await _stripGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (isCloud && allowCloudHydrate)
                    {
                        await HydrateCloudFileAsync(item.FilePath).ConfigureAwait(false);
                        Application.Current?.Dispatcher.InvokeAsync(() => item.IsCloudOnly = IsCloudPlaceholder(item.FilePath));
                    }

                    double dur = item.Duration;
                    if (dur <= 0)
                    {
                        var probed = await ProbeVideoAsync(item.FilePath).ConfigureAwait(false);
                        dur = probed.Duration;
                        if (dur > 0)
                        {
                            string resBadge = GetResolutionLabel(probed.Width, probed.Height);
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                item.Duration = dur;
                                if (!string.IsNullOrEmpty(resBadge)) item.ResolutionBadge = resBadge;
                            });
                        }
                    }

                    if (dur <= 0) dur = 4.0;

                    await ExtractStripWithFfmpegAsync(item.FilePath, stripPath, dur).ConfigureAwait(false);

                    if (File.Exists(stripPath))
                    {
                        var frames = LoadStripFramesFromDisk(stripPath);
                        if (frames != null && frames.Length > 0)
                        {
                            _memoryStrips[key] = frames;
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                item.ScrubFrames = frames;
                                if (item.ThumbnailSource == null)
                                {
                                    item.ThumbnailSource = frames[Math.Min(3, frames.Length - 1)];
                                }
                            });
                        }
                    }
                }
                finally
                {
                    _stripGate.Release();
                }
            }
            finally
            {
                _inFlightStrips.TryRemove(key, out _);
            }
        }

        private static async Task HydrateCloudFileAsync(string filePath)
        {
            await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    byte[] buffer = new byte[4096];
                    _ = fs.Read(buffer, 0, buffer.Length);
                }
                catch { }
            }).ConfigureAwait(false);
        }

        private void TryApplyCachedMeta(SfxItem item, string metaPath)
        {
            try
            {
                string txt = File.ReadAllText(metaPath).Trim();
                string[] parts = txt.Split('|');
                if (parts.Length >= 3)
                {
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double dur);
                    int.TryParse(parts[1], out int w);
                    int.TryParse(parts[2], out int h);
                    string badge = GetResolutionLabel(w, h);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (dur > 0 && item.Duration <= 0) item.Duration = dur;
                        if (!string.IsNullOrEmpty(badge)) item.ResolutionBadge = badge;
                    });
                }
            }
            catch { }
        }

        private static string GetResolutionLabel(int width, int height)
        {
            int maxDim = Math.Max(width, height);
            if (maxDim >= 3800) return "4K";
            if (maxDim >= 2500) return "2K";
            if (maxDim >= 1900) return "1080p";
            if (maxDim >= 1200) return "720p";
            if (maxDim > 0) return $"{width}x{height}";
            return string.Empty;
        }

        private async Task<(double Duration, int Width, int Height)> ProbeVideoAsync(string filePath)
        {
            if (string.IsNullOrEmpty(_ffprobePath) || !File.Exists(filePath)) return (0, 0, 0);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-select_streams");
                psi.ArgumentList.Add("v:0");
                psi.ArgumentList.Add("-show_entries");
                psi.ArgumentList.Add("stream=width,height,duration:format=duration");
                psi.ArgumentList.Add("-of");
                psi.ArgumentList.Add("default=noprint_wrappers=1");
                psi.ArgumentList.Add(filePath);

                using var proc = Process.Start(psi);
                if (proc == null) return (0, 0, 0);

                string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                double dur = 0;
                int w = 0, h = 0;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("width=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.AsSpan(6), out w);
                    else if (line.StartsWith("height=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.AsSpan(7), out h);
                    else if (line.StartsWith("duration=", StringComparison.OrdinalIgnoreCase) && dur <= 0)
                        double.TryParse(line.AsSpan(9), NumberStyles.Any, CultureInfo.InvariantCulture, out dur);
                }

                return (dur, w, h);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        private async Task ExtractSingleThumbWithFfmpegAsync(string videoPath, string outThumbPath, double seekSec)
        {
            if (string.IsNullOrEmpty(_ffmpegPath)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                if (seekSec > 0.05)
                {
                    psi.ArgumentList.Add("-ss");
                    psi.ArgumentList.Add(seekSec.ToString("0.00", CultureInfo.InvariantCulture));
                }
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2:black");
                psi.ArgumentList.Add("-q:v");
                psi.ArgumentList.Add("4");
                psi.ArgumentList.Add(outThumbPath);

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        private async Task ExtractStripWithFfmpegAsync(string videoPath, string outStripPath, double duration)
        {
            if (string.IsNullOrEmpty(_ffmpegPath)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");

                var filterSb = new StringBuilder();
                for (int i = 0; i < StripFrameCount; i++)
                {
                    double t = Math.Max(0, (duration * (i + 0.35)) / StripFrameCount);
                    psi.ArgumentList.Add("-ss");
                    psi.ArgumentList.Add(t.ToString("0.00", CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(videoPath);
                    filterSb.Append($"[{i}:v]scale={StripFrameWidth}:{StripFrameHeight}:force_original_aspect_ratio=decrease,pad={StripFrameWidth}:{StripFrameHeight}:(ow-iw)/2:(oh-ih)/2:black[v{i}];");
                }

                for (int i = 0; i < StripFrameCount; i++)
                {
                    filterSb.Append($"[v{i}]");
                }
                filterSb.Append($"hstack=inputs={StripFrameCount}[out]");

                psi.ArgumentList.Add("-filter_complex");
                psi.ArgumentList.Add(filterSb.ToString());
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("[out]");
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-q:v");
                psi.ArgumentList.Add("5");
                psi.ArgumentList.Add(outStripPath);

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        private static ImageSource? LoadBitmapFromDisk(string filePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length == 0) return null;

                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource[]? LoadStripFramesFromDisk(string stripPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(stripPath);
                if (bytes.Length == 0) return null;

                using var ms = new MemoryStream(bytes);
                var stripBmp = new BitmapImage();
                stripBmp.BeginInit();
                stripBmp.CacheOption = BitmapCacheOption.OnLoad;
                stripBmp.StreamSource = ms;
                stripBmp.EndInit();
                stripBmp.Freeze();

                int totalW = stripBmp.PixelWidth;
                int totalH = stripBmp.PixelHeight;
                if (totalW < StripFrameCount || totalH <= 0) return null;

                int frameW = totalW / StripFrameCount;
                var frames = new ImageSource[StripFrameCount];
                for (int i = 0; i < StripFrameCount; i++)
                {
                    var rect = new Int32Rect(i * frameW, 0, frameW, totalH);
                    var cropped = new CroppedBitmap(stripBmp, rect);
                    cropped.Freeze();
                    frames[i] = cropped;
                }
                return frames;
            }
            catch
            {
                return null;
            }
        }

        private static (ImageSource? Image, int Width, int Height) TryLoadDirectImage(string filePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length == 0) return (null, 0, 0);

                int origW = 0, origH = 0;
                try
                {
                    using var msOrig = new MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(msOrig, BitmapCreateOptions.None, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        origW = frame.PixelWidth;
                        origH = frame.PixelHeight;
                    }
                }
                catch { }

                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 320;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return (bmp, origW, origH);
            }
            catch
            {
                return (null, 0, 0);
            }
        }

        private static void SaveBitmapSourceAsJpeg(BitmapSource bmpSource, string outPath)
        {
            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
            }
            catch { }
        }

        #region Windows Shell Thumbnail Interop (Cloud-safe thumbnail fallback)

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface, IidParameterIndex = 2)] out IShellItemImageFactory? ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private static ImageSource? TryGetShellThumbnail(string filePath, bool inCacheOnly)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, iid, out var factory);
                if (hr != 0 || factory == null) return null;

                // SIIGBF_THUMBNAILONLY = 0x08, SIIGBF_INCACHEONLY = 0x10
                int flags = inCacheOnly ? (0x08 | 0x10) : 0x08;
                hr = factory.GetImage(new SIZE { cx = 320, cy = 180 }, flags, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;

                var bmpSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmpSource.Freeze();
                return bmpSource;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        #endregion
    }
}
