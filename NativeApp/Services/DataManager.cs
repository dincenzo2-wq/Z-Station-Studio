using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZStationStudio.Models;

namespace ZStationStudio.Services
{
    public class DataManager
    {
        public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".flac", ".aiff", ".aif", ".m4a", ".aac", ".ogg", ".wma", ".mid", ".midi"
        };

        public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".gif"
        };

        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif", ".ico", ".svg"
        };

        public static readonly HashSet<string> DesignExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".psd", ".psb", ".ai", ".eps", ".pdf",
            ".aep", ".prproj", ".mogrt",
            ".drp", ".dra", ".setting", ".comp",
            ".blend", ".c4d", ".obj", ".fbx",
            ".otf", ".ttf"
        };

        public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Audio
            ".wav", ".mp3", ".flac", ".aiff", ".aif", ".m4a", ".aac", ".ogg", ".wma", ".mid", ".midi",
            // Video
            ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".gif",
            // Image / Photo / Texture
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif", ".ico", ".svg",
            // Design & Project files
            ".psd", ".psb", ".ai", ".eps", ".pdf",
            ".aep", ".prproj", ".mogrt",
            ".drp", ".dra", ".setting", ".comp",
            ".blend", ".c4d", ".obj", ".fbx",
            ".otf", ".ttf"
        };

        public static bool IsVideoFile(string? pathOrExt)
        {
            if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
            string ext = pathOrExt.StartsWith('.') ? pathOrExt : Path.GetExtension(pathOrExt);
            if (string.IsNullOrEmpty(ext)) ext = "." + pathOrExt.TrimStart('.');
            return VideoExtensions.Contains(ext);
        }

        public static bool IsImageFile(string? pathOrExt)
        {
            if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
            string ext = pathOrExt.StartsWith('.') ? pathOrExt : Path.GetExtension(pathOrExt);
            if (string.IsNullOrEmpty(ext)) ext = "." + pathOrExt.TrimStart('.');
            return ImageExtensions.Contains(ext);
        }

        public static bool IsDesignFile(string? pathOrExt)
        {
            if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
            string ext = pathOrExt.StartsWith('.') ? pathOrExt : Path.GetExtension(pathOrExt);
            if (string.IsNullOrEmpty(ext)) ext = "." + pathOrExt.TrimStart('.');
            return DesignExtensions.Contains(ext);
        }

        public static bool IsAudioFile(string? pathOrExt)
        {
            if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
            string ext = pathOrExt.StartsWith('.') ? pathOrExt : Path.GetExtension(pathOrExt);
            if (string.IsNullOrEmpty(ext)) ext = "." + pathOrExt.TrimStart('.');
            return AudioExtensions.Contains(ext);
        }

        public string DataDir { get; }
        public string SettingsFile { get; }
        public AppSettings Settings { get; private set; } = new();

        private static string GlobalPointerDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Z-station");

        private static string TagSyncPointerFile =>
            Path.Combine(GlobalPointerDir, "tag-sync-path.txt");

        private const string OwnerMasterSyncFile =
            @"F:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\sfx-settings-sync.json";

        public static bool IsStandaloneInstall =>
            File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "standalone-install.flag")) &&
            !File.Exists(OwnerMasterSyncFile) &&
            !File.Exists(TagSyncPointerFile);

        public static string GetConfiguredTagSyncFilePath()
        {
            try
            {
                if (File.Exists(TagSyncPointerFile))
                {
                    string customPath = File.ReadAllText(TagSyncPointerFile).Trim();
                    if (!string.IsNullOrWhiteSpace(customPath))
                    {
                        return customPath;
                    }
                }
            }
            catch { }

            if (File.Exists(OwnerMasterSyncFile))
            {
                return OwnerMasterSyncFile;
            }

            if (IsStandaloneInstall)
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "sfx-settings-sync.json");
            }

            string localSync = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sfx-settings-sync.json");
            if (File.Exists(localSync)) return localSync;

            string[] candidatePaths = new[]
            {
                OwnerMasterSyncFile,
                @"F:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-station\sfx-settings-sync.json"
            };

            foreach (var p in candidatePaths)
            {
                if (File.Exists(p)) return p;
            }

            foreach (var p in candidatePaths)
            {
                string? dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return p;
            }

            return candidatePaths[0];
        }

        public static void SaveConfiguredTagSyncFilePath(string filePath)
        {
            try
            {
                Directory.CreateDirectory(GlobalPointerDir);
                File.WriteAllText(TagSyncPointerFile, filePath.Trim());
            }
            catch { }
        }

        public static string DropboxSyncFile => GetConfiguredTagSyncFilePath();

        private readonly Dictionary<string, List<string>> _fastTagsByNormPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _fastTagsByFileName = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _fastDeletedTagsNorm = new(StringComparer.Ordinal);
        private bool _fastTagIndexBuilt = false;

        public void RebuildFastTagIndex()
        {
            _fastTagsByNormPath.Clear();
            _fastTagsByFileName.Clear();
            _fastDeletedTagsNorm = new HashSet<string>(
                (Settings.DeletedTags ?? new List<string>()).Select(SearchNormalizer.Normalize),
                StringComparer.Ordinal);

            var customTagNorms = new HashSet<string>(
                (Settings.CustomTags ?? new List<string>()).Select(SearchNormalizer.Normalize),
                StringComparer.Ordinal);

            if (Settings.Tags != null)
            {
                foreach (var kvp in Settings.Tags)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                        continue;

                    var validTags = new List<string>(kvp.Value.Count);
                    foreach (var t in kvp.Value)
                    {
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        string trimmed = t.Trim();
                        string normTag = SearchNormalizer.Normalize(trimmed);
                        if (_fastDeletedTagsNorm.Contains(normTag)) continue;

                        if (!validTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            validTags.Add(trimmed);
                        }

                        // Ensure every active tag used on files is also registered in CustomTags so it never disappears
                        if (!customTagNorms.Contains(normTag))
                        {
                            Settings.CustomTags!.Add(trimmed);
                            customTagNorms.Add(normTag);
                        }
                    }

                    if (validTags.Count == 0) continue;

                    string key = kvp.Key.Trim();
                    if (key.Contains('\\') || key.Contains('/'))
                    {
                        string normP = NormalizePath(key);
                        if (!string.IsNullOrEmpty(normP))
                        {
                            if (_fastTagsByNormPath.TryGetValue(normP, out var existing))
                            {
                                foreach (var vt in validTags)
                                {
                                    if (!existing.Contains(vt, StringComparer.OrdinalIgnoreCase))
                                        existing.Add(vt);
                                }
                            }
                            else
                            {
                                _fastTagsByNormPath[normP] = validTags;
                            }
                        }

                        string fn = Path.GetFileName(key);
                        if (!string.IsNullOrEmpty(fn) && !_fastTagsByFileName.ContainsKey(fn))
                        {
                            _fastTagsByFileName[fn] = new List<string>(validTags);
                        }
                    }
                    else
                    {
                        if (_fastTagsByFileName.TryGetValue(key, out var existingFn))
                        {
                            foreach (var vt in validTags)
                            {
                                if (!existingFn.Contains(vt, StringComparer.OrdinalIgnoreCase))
                                    existingFn.Add(vt);
                            }
                        }
                        else
                        {
                            _fastTagsByFileName[key] = validTags;
                        }
                    }
                }
            }

            _fastTagIndexBuilt = true;
        }

        public DataManager()
        {
            if (IsStandaloneInstall)
            {
                string localUserData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
                try
                {
                    Directory.CreateDirectory(localUserData);
                    DataDir = localUserData;
                }
                catch
                {
                    string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Z-Station_Studio");
                    Directory.CreateDirectory(fallback);
                    DataDir = fallback;
                }
                SettingsFile = Path.Combine(DataDir, "settings.json");
                return;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            DataDir = Path.Combine(appData, "Z-station");
            string oldNativeDir = Path.Combine(appData, "HNH_SFX_Finder_Native");

            if (!Directory.Exists(DataDir))
            {
                Directory.CreateDirectory(DataDir);
                if (Directory.Exists(oldNativeDir))
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(oldNativeDir))
                        {
                            var dest = Path.Combine(DataDir, Path.GetFileName(file));
                            if (!File.Exists(dest))
                            {
                                File.Copy(file, dest, true);
                            }
                        }
                    }
                    catch { }
                }
            }
            SettingsFile = Path.Combine(DataDir, "settings.json");
        }

        public string GetDefaultCacheRootDirectory() => DataDir;

        public string GetActiveCacheRootDirectory()
        {
            if (!string.IsNullOrWhiteSpace(Settings.CacheDirectory))
            {
                try
                {
                    Directory.CreateDirectory(Settings.CacheDirectory);
                    return Settings.CacheDirectory;
                }
                catch
                {
                    // Fallback to default DataDir if custom path is inaccessible
                }
            }
            return DataDir;
        }

        public string GetWaveformCacheDir() => Path.Combine(GetActiveCacheRootDirectory(), "WaveformCache");
        public string GetVideoCacheDir() => Path.Combine(GetActiveCacheRootDirectory(), "VideoCache");
        public string GetLibraryIndexCacheFile() => Path.Combine(GetActiveCacheRootDirectory(), "library-index-cache.json");
        public string GetTagBackupsDir()
        {
            string? syncDir = Path.GetDirectoryName(DropboxSyncFile);
            if (!string.IsNullOrEmpty(syncDir) && Directory.Exists(syncDir))
            {
                return Path.Combine(syncDir, "TagBackups");
            }
            return Path.Combine(DataDir, "TagBackups");
        }

        public static SfxItem CreateSfxItemFast(string path, List<string> tags, double duration = 0, long knownSize = 0, string? relativePath = null)
        {
            string fn = Path.GetFileName(path);
            string folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
            string extWithDot = Path.GetExtension(path);
            string ext = extWithDot.TrimStart('.').ToUpperInvariant();
            bool isVid = IsVideoFile(extWithDot);
            bool isImg = IsImageFile(extWithDot);
            bool isDsg = IsDesignFile(extWithDot);

            long size = knownSize;
            DateTime lastMod = DateTime.Now;
            bool isCloud = false;

            // Only touch the disk if knownSize <= 0 (not in Library Index Cache)
            if (size <= 0)
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                {
                    size = fi.Length;
                    lastMod = fi.LastWriteTime;
                }
                if (isVid)
                {
                    isCloud = VideoPreviewService.IsCloudPlaceholder(path);
                }
            }

            var item = new SfxItem
            {
                FilePath = path,
                FileName = fn,
                BaseName = Path.GetFileNameWithoutExtension(fn),
                RelativePath = !string.IsNullOrEmpty(relativePath) ? relativePath : fn,
                Extension = ext,
                FolderName = folderName,
                Duration = duration,
                FileSize = size,
                LastModified = lastMod,
                IsVideo = isVid,
                IsImage = isImg,
                IsDesign = isDsg,
                IsCloudOnly = isCloud,
                Tags = tags
            };
            item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
            item.FilePathNorm = NormalizePath(item.FilePath);
            return item;
        }

        public bool IsPathInDeletedFolders(string? path)
        {
            string norm = NormalizePath(path);
            if (string.IsNullOrEmpty(norm) || Settings.DeletedFolderPaths == null || Settings.DeletedFolderPaths.Count == 0)
                return false;

            foreach (var dp in Settings.DeletedFolderPaths)
            {
                string normDp = NormalizePath(dp);
                if (string.IsNullOrEmpty(normDp)) continue;
                if (norm.Equals(normDp, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(normDp + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<List<SfxItem>> LoadDataAsync()
        {
            // 1. Load Settings
            await LoadSettingsAsync();

            // 2. Read metadata cache (duration, size, relativePath) from native Library Index Cache first, then fallback to old Electron index
            var items = new List<SfxItem>();
            var itemsMap = new Dictionary<string, SfxItem>(StringComparer.OrdinalIgnoreCase);
            var cachedMeta = new Dictionary<string, (double Duration, long Size, string RelPath)>(StringComparer.OrdinalIgnoreCase);

            string nativeIndexCacheFile = GetLibraryIndexCacheFile();
            if (File.Exists(nativeIndexCacheFile))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(nativeIndexCacheFile);
                    var entries = JsonSerializer.Deserialize<List<CachedMediaEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entries != null)
                    {
                        foreach (var e in entries)
                        {
                            if (string.IsNullOrWhiteSpace(e.Path)) continue;
                            string norm = NormalizePath(e.Path);
                            if (!string.IsNullOrEmpty(norm))
                            {
                                cachedMeta[norm] = (e.Duration, e.Size, string.IsNullOrEmpty(e.RelativePath) ? Path.GetFileName(e.Path) : e.RelativePath);
                            }
                        }
                    }
                }
                catch { }
            }

            if (!IsStandaloneInstall)
            {
                string oldIndexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Electron", "Z-station", "sfx-index.json");
                if (!File.Exists(oldIndexPath))
                {
                    oldIndexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Electron", "HNH-SFX-Finder", "sfx-index.json");
                }

                if (File.Exists(oldIndexPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(oldIndexPath);
                        using var doc = await JsonDocument.ParseAsync(stream);
                        if (doc.RootElement.TryGetProperty("files", out var filesArr) && filesArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in filesArr.EnumerateArray())
                            {
                                string path = f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                                if (string.IsNullOrEmpty(path)) continue;

                                string norm = NormalizePath(path);
                                if (string.IsNullOrEmpty(norm)) continue;

                                string fn = f.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() ?? Path.GetFileName(path) : Path.GetFileName(path);
                                string rel = f.TryGetProperty("relativePath", out var rEl) ? rEl.GetString() ?? fn : fn;
                                double dur = f.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetDouble() : 0;
                                long size = f.TryGetProperty("size", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetInt64() : 0;

                                if (!cachedMeta.ContainsKey(norm) || (dur > 0 && cachedMeta[norm].Duration <= 0))
                                {
                                    cachedMeta[norm] = (dur, size, rel);
                                }

                                // Only load directly from oldIndexPath if user has no VirtualFolders configured yet
                                if (Settings.VirtualFolders.Count == 0 && !IsPathInDeletedFolders(path) && File.Exists(path) && !itemsMap.ContainsKey(norm))
                                {
                                    var tags = GetTagsForFile(path, fn);
                                    var item = CreateSfxItemFast(path, tags, dur, size, rel);
                                    items.Add(item);
                                    itemsMap[norm] = item;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Index load warning: {ex.Message}");
                    }
                }
            }

            // 3. Load all files referenced in active VirtualFolders
            await Task.Run(() =>
            {
                // Fast parallel verification (takes ~30ms): find any files deleted on disk
                var allVfPaths = Settings.VirtualFolders
                    .SelectMany(vf => vf.Files)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var deadPathBag = new System.Collections.Concurrent.ConcurrentBag<string>();
                Parallel.ForEach(allVfPaths, new ParallelOptions { MaxDegreeOfParallelism = 32 }, fPath =>
                {
                    try
                    {
                        string? root = Path.GetPathRoot(fPath);
                        // If drive is accessible, check if file exists on disk
                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        {
                            if (!File.Exists(fPath))
                            {
                                deadPathBag.Add(fPath);
                            }
                        }
                    }
                    catch { }
                });

                var deadPathSet = new HashSet<string>(deadPathBag, StringComparer.OrdinalIgnoreCase);
                bool hasPrunedDeadFiles = false;

                foreach (var vf in Settings.VirtualFolders)
                {
                    if (deadPathSet.Count > 0)
                    {
                        int beforeCount = vf.Files.Count;
                        vf.Files.RemoveAll(f => deadPathSet.Contains(f));
                        if (vf.Files.Count != beforeCount)
                        {
                            hasPrunedDeadFiles = true;
                        }
                    }

                    foreach (var fPath in vf.Files)
                    {
                        if (string.IsNullOrWhiteSpace(fPath)) continue;
                        string norm = NormalizePath(fPath);
                        if (!itemsMap.ContainsKey(norm))
                        {
                            try
                            {
                                if (cachedMeta.TryGetValue(norm, out var meta) && meta.Size > 0)
                                {
                                    var tags = GetTagsForFile(fPath, Path.GetFileName(fPath));
                                    var item = CreateSfxItemFast(fPath, tags, meta.Duration, meta.Size, meta.RelPath);
                                    items.Add(item);
                                    itemsMap[norm] = item;
                                }
                                else if (File.Exists(fPath))
                                {
                                    var tags = GetTagsForFile(fPath, Path.GetFileName(fPath));
                                    double dur = cachedMeta.TryGetValue(norm, out var m2) ? m2.Duration : 0;
                                    string? rel = cachedMeta.TryGetValue(norm, out var m3) ? m3.RelPath : null;
                                    var item = CreateSfxItemFast(fPath, tags, dur, 0, rel);
                                    items.Add(item);
                                    itemsMap[norm] = item;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (hasPrunedDeadFiles)
                {
                    foreach (var dead in deadPathSet)
                    {
                        string normDead = NormalizePath(dead);
                        cachedMeta.Remove(normDead);
                        Settings.Tags.Remove(dead);
                        string fn = Path.GetFileName(dead);
                        if (!string.IsNullOrEmpty(fn) && !itemsMap.Values.Any(i => i.FileName.Equals(fn, StringComparison.OrdinalIgnoreCase)))
                        {
                            Settings.Tags.Remove(fn);
                        }
                    }
                    _ = SaveSettingsAsync();
                }
            });

            // 4. If index was empty and no VirtualFolders exist, scan registered libraries
            if (items.Count == 0 && Settings.VirtualFolders.Count == 0 && Settings.Libraries.Count > 0)
            {
                foreach (var lib in Settings.Libraries.Where(l => l.Enabled && Directory.Exists(l.Path)))
                {
                    var scanned = await ScanDirectoryAsync(lib.Path);
                    foreach (var sc in scanned)
                    {
                        if (!itemsMap.ContainsKey(sc.FilePathNorm))
                        {
                            items.Add(sc);
                            itemsMap[sc.FilePathNorm] = sc;
                        }
                    }
                }
            }

            // 5. Save index cache if items were loaded
            if (items.Count > 0)
            {
                _ = SaveLibraryIndexCacheAsync(items);
            }

            return items;
        }

        public async Task SaveLibraryIndexCacheAsync(IEnumerable<SfxItem> items)
        {
            try
            {
                string cacheFile = GetLibraryIndexCacheFile();
                string? dir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var snapshot = items.Select(i => new CachedMediaEntry
                {
                    Path = i.FilePath,
                    Duration = i.Duration,
                    Size = i.FileSize,
                    RelativePath = i.RelativePath
                }).ToList();

                string json = JsonSerializer.Serialize(snapshot);
                await WriteTextWithRetryAsync(cacheFile, json);
            }
            catch { }
        }

        public class CacheStatsInfo
        {
            public string ActiveCacheDirectory { get; set; } = string.Empty;
            public bool IsCustomDirectory { get; set; }
            public int WaveformFileCount { get; set; }
            public long WaveformBytes { get; set; }
            public int VideoFileCount { get; set; }
            public long VideoBytes { get; set; }
            public int IndexEntryCount { get; set; }
            public long IndexBytes { get; set; }
            public long TotalBytes => WaveformBytes + VideoBytes + IndexBytes;
        }

        public CacheStatsInfo GetCacheStats(int currentLoadedItemCount = 0)
        {
            var stats = new CacheStatsInfo
            {
                ActiveCacheDirectory = GetActiveCacheRootDirectory(),
                IsCustomDirectory = !string.IsNullOrWhiteSpace(Settings.CacheDirectory),
                IndexEntryCount = currentLoadedItemCount
            };

            try
            {
                string wfDir = GetWaveformCacheDir();
                if (Directory.Exists(wfDir))
                {
                    var wfFiles = new DirectoryInfo(wfDir).GetFiles("*.wf", SearchOption.TopDirectoryOnly);
                    stats.WaveformFileCount = wfFiles.Length;
                    stats.WaveformBytes = wfFiles.Sum(f => f.Length);
                }
            }
            catch { }

            try
            {
                string vidDir = GetVideoCacheDir();
                if (Directory.Exists(vidDir))
                {
                    var vidFiles = new DirectoryInfo(vidDir).GetFiles("*.*", SearchOption.TopDirectoryOnly);
                    stats.VideoFileCount = vidFiles.Length;
                    stats.VideoBytes = vidFiles.Sum(f => f.Length);
                }
            }
            catch { }

            try
            {
                string idxFile = GetLibraryIndexCacheFile();
                if (File.Exists(idxFile))
                {
                    stats.IndexBytes = new FileInfo(idxFile).Length;
                }
            }
            catch { }

            return stats;
        }

        public Task ClearCacheAsync(bool clearWaveform, bool clearVideo, bool clearIndex)
        {
            return Task.Run(() =>
            {
                if (clearWaveform)
                {
                    try
                    {
                        string wfDir = GetWaveformCacheDir();
                        if (Directory.Exists(wfDir))
                        {
                            foreach (var f in Directory.GetFiles(wfDir, "*.wf"))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                if (clearVideo)
                {
                    try
                    {
                        string vidDir = GetVideoCacheDir();
                        if (Directory.Exists(vidDir))
                        {
                            foreach (var f in Directory.GetFiles(vidDir, "*.*"))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                if (clearIndex)
                {
                    try
                    {
                        string idxFile = GetLibraryIndexCacheFile();
                        if (File.Exists(idxFile))
                        {
                            File.Delete(idxFile);
                        }
                    }
                    catch { }
                }
            });
        }

        public async Task<(bool Success, string Message)> ChangeCacheDirectoryAsync(string? newCacheRootDir, bool moveExistingFiles = true)
        {
            string oldRoot = GetActiveCacheRootDirectory();
            string targetRoot = string.IsNullOrWhiteSpace(newCacheRootDir) ? DataDir : newCacheRootDir.Trim();

            try
            {
                Directory.CreateDirectory(targetRoot);
            }
            catch (Exception ex)
            {
                return (false, $"Không thể tạo hoặc truy cập thư mục: {ex.Message}");
            }

            if (moveExistingFiles && !NormalizePath(oldRoot).Equals(NormalizePath(targetRoot), StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() =>
                {
                    // 1. Move WaveformCache
                    string oldWf = Path.Combine(oldRoot, "WaveformCache");
                    string newWf = Path.Combine(targetRoot, "WaveformCache");
                    if (Directory.Exists(oldWf))
                    {
                        try
                        {
                            Directory.CreateDirectory(newWf);
                            foreach (var f in Directory.GetFiles(oldWf, "*.wf"))
                            {
                                string dest = Path.Combine(newWf, Path.GetFileName(f));
                                try
                                {
                                    File.Copy(f, dest, true);
                                    File.Delete(f);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    // 2. Move VideoCache
                    string oldVid = Path.Combine(oldRoot, "VideoCache");
                    string newVid = Path.Combine(targetRoot, "VideoCache");
                    if (Directory.Exists(oldVid))
                    {
                        try
                        {
                            Directory.CreateDirectory(newVid);
                            foreach (var f in Directory.GetFiles(oldVid, "*.*"))
                            {
                                string dest = Path.Combine(newVid, Path.GetFileName(f));
                                try
                                {
                                    File.Copy(f, dest, true);
                                    File.Delete(f);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    // 3. Move library-index-cache.json
                    string oldIdx = Path.Combine(oldRoot, "library-index-cache.json");
                    string newIdx = Path.Combine(targetRoot, "library-index-cache.json");
                    if (File.Exists(oldIdx))
                    {
                        try
                        {
                            File.Copy(oldIdx, newIdx, true);
                            File.Delete(oldIdx);
                        }
                        catch { }
                    }
                });
            }

            Settings.CacheDirectory = string.IsNullOrWhiteSpace(newCacheRootDir) ? null : targetRoot;
            await SaveSettingsAsync();

            return (true, $"Đã chuyển thư mục lưu Cache sang: {targetRoot}");
        }

        public List<string> GetTagsForFile(string filePath, string fileName)
        {
            if (!_fastTagIndexBuilt)
            {
                RebuildFastTagIndex();
            }

            List<string>? byPath = null;
            List<string>? byName = null;

            string normP = NormalizePath(filePath);
            if (!string.IsNullOrEmpty(normP))
            {
                _fastTagsByNormPath.TryGetValue(normP, out byPath);
            }

            if (!string.IsNullOrEmpty(fileName))
            {
                _fastTagsByFileName.TryGetValue(fileName, out byName);
            }

            if (byPath == null && byName == null)
            {
                return new List<string>();
            }

            if (byPath != null && byName == null)
            {
                return new List<string>(byPath);
            }

            if (byName != null && byPath == null)
            {
                return new List<string>(byName);
            }

            var merged = new List<string>(byPath!);
            foreach (var t in byName!)
            {
                if (!merged.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(t);
                }
            }
            return merged;
        }

        public async Task<List<SfxItem>> ScanDirectoryAsync(string dirPath)
        {
            var items = new List<SfxItem>();
            if (!Directory.Exists(dirPath)) return items;

            await Task.Run(() =>
            {
                try
                {
                    var files = Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => MediaExtensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith("._"));

                    foreach (var f in files)
                    {
                        try
                        {
                            string fn = Path.GetFileName(f);
                            string rel = Path.GetRelativePath(dirPath, f);
                            var tags = GetTagsForFile(f, fn);
                            var item = CreateSfxItemFast(f, tags, 0, 0, rel);
                            items.Add(item);
                        }
                        catch {}
                    }
                }
                catch {}
            });

            return items;
        }

        public async Task LoadSettingsAsync()
        {
            AppSettings? local = null;

            // 1. Try local settings first (to preserve local volume, window preferences, custom path)
            if (File.Exists(SettingsFile))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(SettingsFile);
                    local = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (local != null)
                    {
                        Settings = local;
                        if (!string.IsNullOrWhiteSpace(Settings.CustomTagSyncFilePath) && File.Exists(Settings.CustomTagSyncFilePath))
                        {
                            SaveConfiguredTagSyncFilePath(Settings.CustomTagSyncFilePath);
                        }
                    }
                }
                catch {}
            }

            // 2. Check configured / Dropbox master sync file (sfx-settings-sync.json)
            if (!IsStandaloneInstall)
            {
                string dropboxSync = DropboxSyncFile;
                if (File.Exists(dropboxSync))
                {
                    try
                    {
                        var fi = new FileInfo(dropboxSync);
                        if (fi.Length > 10)
                        {
                            string json = await File.ReadAllTextAsync(dropboxSync);
                            var master = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (master != null && (master.VirtualFolders?.Count > 0 || master.Tags?.Count > 0 || master.Libraries?.Count > 0))
                            {
                                // Preserve local machine UI preferences
                                if (local != null)
                                {
                                    master.Volume = local.Volume;
                                    master.IsMuted = local.IsMuted;
                                    master.IsLooping = local.IsLooping;
                                    master.AlwaysOnTop = local.AlwaysOnTop;
                                    master.AutoPreview = local.AutoPreview;
                                    if (!string.IsNullOrWhiteSpace(local.CacheDirectory))
                                        master.CacheDirectory = local.CacheDirectory;
                                    if (!string.IsNullOrWhiteSpace(local.CustomTagSyncFilePath))
                                        master.CustomTagSyncFilePath = local.CustomTagSyncFilePath;
                                }

                                Settings = master;
                                PurgeDeletedVirtualFolders();
                                SanitizeVirtualFolderDirectFiles();
                                RebuildFastTagIndex();

                                // Update local SettingsFile so it matches master
                                string localJson = JsonSerializer.Serialize(Settings, _jsonSaveOptions);
                                await File.WriteAllTextAsync(SettingsFile, localJson).ConfigureAwait(false);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load master settings: {ex.Message}");
                    }
                }
            }

            // 3. Fallback to existing electron settings if still not found
            if (local == null && !IsStandaloneInstall)
            {
                string oldSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Electron", "HNH-SFX-Finder", "settings.json");
                if (File.Exists(oldSettingsPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(oldSettingsPath);
                        var s = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (s != null)
                        {
                            Settings = s;
                        }
                    }
                    catch {}
                }
            }

            PurgeDeletedVirtualFolders();
            EnsureDefaultFolderLinks();
            SanitizeVirtualFolderDirectFiles();
            RebuildFastTagIndex();

            if (!File.Exists(SettingsFile))
            {
                await SaveSettingsAsync();
            }
        }

        public class TagFileStatusInfo
        {
            public string FilePath { get; set; } = string.Empty;
            public bool Exists { get; set; }
            public long FileSizeBytes { get; set; }
            public DateTime LastModified { get; set; }
            public int UniqueTagCount { get; set; }
            public int TaggedFileCount { get; set; }
            public int BinCount { get; set; }
            public int BackupFileCount { get; set; }
        }

        public TagFileStatusInfo GetTagFileStats(IEnumerable<SfxItem>? allFiles = null)
        {
            string syncPath = DropboxSyncFile;
            var info = new TagFileStatusInfo
            {
                FilePath = syncPath,
                Exists = File.Exists(syncPath),
                UniqueTagCount = GetAllUniqueTags(allFiles ?? Enumerable.Empty<SfxItem>()).Count,
                TaggedFileCount = _fastTagsByNormPath.Count > 0 ? _fastTagsByNormPath.Count : (Settings.Tags?.Count ?? 0),
                BinCount = Settings.VirtualFolders?.Count ?? 0
            };

            if (info.Exists)
            {
                try
                {
                    var fi = new FileInfo(syncPath);
                    info.FileSizeBytes = fi.Length;
                    info.LastModified = fi.LastWriteTime;
                }
                catch { }
            }

            try
            {
                string backupDir = GetTagBackupsDir();
                if (Directory.Exists(backupDir))
                {
                    info.BackupFileCount = Directory.GetFiles(backupDir, "*.json").Length;
                }
            }
            catch { }

            return info;
        }

        public async Task<(bool Success, string Message)> LoadAndLinkTagSyncFileAsync(string chosenFilePath, List<SfxItem> allFiles)
        {
            if (string.IsNullOrWhiteSpace(chosenFilePath) || !File.Exists(chosenFilePath))
            {
                return (false, "File không tồn tại hoặc đường dẫn không hợp lệ.");
            }

            try
            {
                string json = await File.ReadAllTextAsync(chosenFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded == null)
                {
                    return (false, "Nội dung file JSON không hợp lệ.");
                }

                // Remove standalone flag if user explicitly links a tag sync file
                try
                {
                    string flagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "standalone-install.flag");
                    if (File.Exists(flagPath)) File.Delete(flagPath);
                }
                catch { }

                SaveConfiguredTagSyncFilePath(chosenFilePath);
                Settings = loaded;
                Settings.CustomTagSyncFilePath = chosenFilePath;

                PurgeDeletedVirtualFolders();
                SanitizeVirtualFolderDirectFiles();
                RebuildFastTagIndex();

                // Apply tags to all loaded files
                foreach (var item in allFiles)
                {
                    item.Tags = GetTagsForFile(item.FilePath, item.FileName);
                    item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
                }

                await SaveSettingsAsync();
                await CreateAutoBackupSnapshotAsync();

                int uniqueTags = GetAllUniqueTags(allFiles).Count;
                return (true, $"Đã liên kết và nạp thành công File Tag:\n{chosenFilePath}\n\n• {uniqueTags:N0} Tag\n• {_fastTagsByNormPath.Count:N0} bài đã gắn Tag\n• {Settings.VirtualFolders.Count:N0} Thư mục Bin");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi đọc file Tag: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message, string BackupPath)> CreateAutoBackupSnapshotAsync(string? customTargetFile = null)
        {
            try
            {
                RebuildFastTagIndex();
                string json = JsonSerializer.Serialize(Settings, _jsonSaveOptions);

                string targetPath;
                if (!string.IsNullOrWhiteSpace(customTargetFile))
                {
                    targetPath = customTargetFile;
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }
                else
                {
                    string backupDir = GetTagBackupsDir();
                    Directory.CreateDirectory(backupDir);
                    targetPath = Path.Combine(backupDir, $"sfx-settings-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                }

                await WriteTextWithRetryAsync(targetPath, json);

                // Keep max 15 auto-backups in TagBackups directory
                if (string.IsNullOrWhiteSpace(customTargetFile))
                {
                    try
                    {
                        var files = new DirectoryInfo(GetTagBackupsDir())
                            .GetFiles("sfx-settings-backup-*.json")
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Skip(15)
                            .ToList();
                        foreach (var old in files)
                        {
                            try { old.Delete(); } catch { }
                        }
                    }
                    catch { }
                }

                return (true, $"Đã sao lưu dữ liệu Tag & Bin an toàn tại:\n{targetPath}", targetPath);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi sao lưu: {ex.Message}", string.Empty);
            }
        }

        public async Task<(bool Success, string Message)> OptimizeTagFileAndIndexAsync(List<SfxItem> allFiles)
        {
            try
            {
                // 1. Backup before optimizing
                await CreateAutoBackupSnapshotAsync();

                // 2. Rebuild fast tag index and clean redundant/empty tag entries
                RebuildFastTagIndex();
                var cleanTagsDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in _fastTagsByNormPath)
                {
                    if (kvp.Value.Count > 0)
                    {
                        cleanTagsDict[kvp.Key] = kvp.Value;
                    }
                }
                foreach (var kvp in _fastTagsByFileName)
                {
                    if (kvp.Value.Count > 0 && !cleanTagsDict.ContainsKey(kvp.Key))
                    {
                        cleanTagsDict[kvp.Key] = kvp.Value;
                    }
                }

                Settings.Tags = cleanTagsDict;
                SanitizeVirtualFolderDirectFiles();
                RebuildFastTagIndex();

                // 3. Re-apply tags to all in-memory files
                foreach (var item in allFiles)
                {
                    item.Tags = GetTagsForFile(item.FilePath, item.FileName);
                    item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
                }

                // 4. Save both Settings/DropboxSyncFile and Library Index Cache for instant 0ms startup
                await SaveSettingsAsync();
                await SaveLibraryIndexCacheAsync(allFiles);

                return (true, $"Đã tối ưu hóa File Tag (sfx-settings-sync.json) và Chỉ mục thành công!\n\n• Đã đồng bộ {GetAllUniqueTags(allFiles).Count:N0} Tag ({_fastTagsByNormPath.Count:N0} bài có Tag)\n• Đã nén gọn khóa đường dẫn & cập nhật Index Cache để mở app siêu tốc!");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi tối ưu hóa: {ex.Message}");
            }
        }

        private bool PurgeDeletedVirtualFolders()
        {
            if ((Settings.DeletedFolderIds == null || Settings.DeletedFolderIds.Count == 0) &&
                (Settings.DeletedFolderPaths == null || Settings.DeletedFolderPaths.Count == 0))
            {
                return false;
            }

            var delIds = new HashSet<string>(Settings.DeletedFolderIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            int before = Settings.VirtualFolders.Count;

            Settings.VirtualFolders.RemoveAll(f =>
                delIds.Contains(f.Id) ||
                (!string.IsNullOrWhiteSpace(f.LinkedPath) && IsPathInDeletedFolders(f.LinkedPath)));

            return Settings.VirtualFolders.Count != before;
        }

        private bool SanitizeVirtualFolderDirectFiles()
        {
            bool changed = false;

            var childrenByParent = Settings.VirtualFolders
                .Where(f => !string.IsNullOrWhiteSpace(f.ParentId))
                .GroupBy(f => f.ParentId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            HashSet<string> GetDescendantFiles(string folderId, HashSet<string> visited)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visited.Add(folderId)) return result;
                if (childrenByParent.TryGetValue(folderId, out var children))
                {
                    foreach (var child in children)
                    {
                        foreach (var f in child.Files)
                        {
                            string np = NormalizePath(f);
                            if (!string.IsNullOrEmpty(np)) result.Add(np);
                        }
                        result.UnionWith(GetDescendantFiles(child.Id, visited));
                    }
                }
                return result;
            }

            foreach (var vf in Settings.VirtualFolders)
            {
                if (vf.Files == null || vf.Files.Count == 0) continue;

                var descFiles = GetDescendantFiles(vf.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (descFiles.Count == 0) continue;

                string normLinked = !string.IsNullOrWhiteSpace(vf.LinkedPath) ? NormalizePath(vf.LinkedPath) : string.Empty;

                int before = vf.Files.Count;
                vf.Files.RemoveAll(f =>
                {
                    if (string.IsNullOrWhiteSpace(f)) return true;
                    string np = NormalizePath(f);
                    if (descFiles.Contains(np)) return true;
                    if (!string.IsNullOrEmpty(normLinked))
                    {
                        string fDir = NormalizePath(Path.GetDirectoryName(f));
                        if (!string.IsNullOrEmpty(fDir) && !fDir.Equals(normLinked, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    return false;
                });

                if (vf.Files.Count != before)
                {
                    changed = true;
                }
            }

            return changed;
        }

        private bool EnsureDefaultFolderLinks()
        {
            // Never auto-create or force-link default folders if the user already has folders or has deleted folders
            if (Settings.VirtualFolders.Count > 0 ||
                (Settings.DeletedFolderIds != null && Settings.DeletedFolderIds.Count > 0) ||
                (Settings.DeletedFolderPaths != null && Settings.DeletedFolderPaths.Count > 0))
            {
                return false;
            }

            return false;
        }

        private void MergeSettingsFromMaster(AppSettings master)
        {
            if (master == null) return;

            // Merge DeletedFolderIds & DeletedFolderPaths first so deleted folders are NEVER restored
            if (master.DeletedFolderIds != null)
            {
                Settings.DeletedFolderIds ??= new List<string>();
                foreach (var id in master.DeletedFolderIds)
                {
                    if (!string.IsNullOrWhiteSpace(id) && !Settings.DeletedFolderIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        Settings.DeletedFolderIds.Add(id);
                    }
                }
            }

            if (master.DeletedFolderPaths != null)
            {
                Settings.DeletedFolderPaths ??= new List<string>();
                foreach (var p in master.DeletedFolderPaths)
                {
                    string np = NormalizePath(p);
                    if (!string.IsNullOrEmpty(np) && !Settings.DeletedFolderPaths.Any(x => NormalizePath(x).Equals(np, StringComparison.OrdinalIgnoreCase)))
                    {
                        Settings.DeletedFolderPaths.Add(np);
                    }
                }
            }

            if (master.DeletedTags != null)
            {
                Settings.DeletedTags ??= new List<string>();
                foreach (var dt in master.DeletedTags)
                {
                    if (!string.IsNullOrWhiteSpace(dt) && !Settings.DeletedTags.Any(d => SearchNormalizer.Normalize(d) == SearchNormalizer.Normalize(dt)))
                    {
                        Settings.DeletedTags.Add(dt);
                    }
                }
            }

            // Adopt master VirtualFolders, CustomTags, Tags, Libraries, Favorites
            if (master.VirtualFolders != null)
            {
                Settings.VirtualFolders = master.VirtualFolders;
            }

            if (master.CustomTags != null)
            {
                Settings.CustomTags = master.CustomTags;
            }

            if (master.Tags != null)
            {
                Settings.Tags = master.Tags;
            }

            if (master.Libraries != null && master.Libraries.Count > 0)
            {
                Settings.Libraries = master.Libraries;
            }

            if (master.Favorites != null)
            {
                Settings.Favorites = master.Favorites;
            }

            PurgeDeletedVirtualFolders();
            SanitizeVirtualFolderDirectFiles();
            RebuildFastTagIndex();
        }

        private static readonly JsonSerializerOptions _jsonSaveOptions = new() { WriteIndented = true };
        private readonly System.Threading.SemaphoreSlim _saveSemaphore = new(1, 1);
        public DateTime LastLocalSaveUtc { get; private set; } = DateTime.MinValue;

        public static async Task WriteTextWithRetryAsync(string filePath, string content, int maxRetries = 5, int delayMs = 200)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    string tempFile = filePath + $".tmp.{Guid.NewGuid():N}";
                    await File.WriteAllTextAsync(tempFile, content).ConfigureAwait(false);

                    try
                    {
                        File.Move(tempFile, filePath, overwrite: true);
                        return;
                    }
                    catch
                    {
                        if (File.Exists(tempFile))
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                        await File.WriteAllTextAsync(filePath, content).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex) when (attempt < maxRetries && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    await Task.Delay(delayMs * attempt).ConfigureAwait(false);
                }
            }
        }

        public async Task SaveSettingsAsync()
        {
            RebuildFastTagIndex();
            LastLocalSaveUtc = DateTime.UtcNow;

            await _saveSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                LastLocalSaveUtc = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(Settings, _jsonSaveOptions);
                await WriteTextWithRetryAsync(SettingsFile, json).ConfigureAwait(false);

                // Also dual-save to Dropbox master sync file if path exists (skip in standalone install mode)
                if (!IsStandaloneInstall)
                {
                    string dropboxSync = DropboxSyncFile;
                    string? dir = Path.GetDirectoryName(dropboxSync);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        LastLocalSaveUtc = DateTime.UtcNow;
                        await WriteTextWithRetryAsync(dropboxSync, json).ConfigureAwait(false);
                        LastLocalSaveUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save settings error: {ex.Message}");
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        public void SaveSettingsNow()
        {
            RebuildFastTagIndex();
            LastLocalSaveUtc = DateTime.UtcNow;

            try
            {
                string json = JsonSerializer.Serialize(Settings, _jsonSaveOptions);
                File.WriteAllText(SettingsFile, json);

                if (!IsStandaloneInstall)
                {
                    string dropboxSync = DropboxSyncFile;
                    string? dir = Path.GetDirectoryName(dropboxSync);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        File.WriteAllText(dropboxSync, json);
                        LastLocalSaveUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveSettingsNow error: {ex.Message}");
            }
        }

        public async Task<bool> SyncWithDropboxMasterAsync(List<SfxItem> allFiles)
        {
            string dropboxSync = DropboxSyncFile;
            if (!File.Exists(dropboxSync)) return false;

            try
            {
                var fi = new FileInfo(dropboxSync);
                if (fi.Length <= 10) return false;

                string json = await File.ReadAllTextAsync(dropboxSync);
                var master = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (master != null && (master.VirtualFolders?.Count > 0 || master.Tags?.Count > 0 || master.Libraries?.Count > 0))
                {
                    // Preserve local UI preferences
                    master.Volume = Settings.Volume;
                    master.IsMuted = Settings.IsMuted;
                    master.IsLooping = Settings.IsLooping;
                    master.AlwaysOnTop = Settings.AlwaysOnTop;
                    master.AutoPreview = Settings.AutoPreview;
                    if (!string.IsNullOrWhiteSpace(Settings.CacheDirectory))
                        master.CacheDirectory = Settings.CacheDirectory;
                    if (!string.IsNullOrWhiteSpace(Settings.CustomTagSyncFilePath))
                        master.CustomTagSyncFilePath = Settings.CustomTagSyncFilePath;

                    // Union deleted IDs and paths so deletions are never forgotten
                    if (Settings.DeletedFolderIds != null)
                    {
                        master.DeletedFolderIds ??= new List<string>();
                        foreach (var id in Settings.DeletedFolderIds)
                        {
                            if (!string.IsNullOrWhiteSpace(id) && !master.DeletedFolderIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                                master.DeletedFolderIds.Add(id);
                        }
                    }
                    if (Settings.DeletedFolderPaths != null)
                    {
                        master.DeletedFolderPaths ??= new List<string>();
                        foreach (var p in Settings.DeletedFolderPaths)
                        {
                            string np = NormalizePath(p);
                            if (!string.IsNullOrEmpty(np) && !master.DeletedFolderPaths.Any(x => NormalizePath(x).Equals(np, StringComparison.OrdinalIgnoreCase)))
                                master.DeletedFolderPaths.Add(np);
                        }
                    }
                    if (Settings.DeletedTags != null)
                    {
                        master.DeletedTags ??= new List<string>();
                        foreach (var dt in Settings.DeletedTags)
                        {
                            if (!string.IsNullOrWhiteSpace(dt) && !master.DeletedTags.Any(d => SearchNormalizer.Normalize(d) == SearchNormalizer.Normalize(dt)))
                                master.DeletedTags.Add(dt);
                        }
                    }

                    Settings = master;
                    PurgeDeletedVirtualFolders();
                    SanitizeVirtualFolderDirectFiles();
                    RebuildFastTagIndex();

                    // NO SyncAllLinkedFoldersAsync! No auto-scanning hard drive!
                    // Synchronize in-memory allFiles with the updated VirtualFolders & Libraries:
                    var validVirtualFiles = new HashSet<string>(
                        (Settings.VirtualFolders ?? new List<VirtualFolder>()).SelectMany(f => f.Files ?? new List<string>()).Select(NormalizePath),
                        StringComparer.OrdinalIgnoreCase);

                    var libraryDirs = Settings.Libraries
                        .Where(l => l.Enabled && !string.IsNullOrWhiteSpace(l.Path))
                        .Select(l => NormalizePath(l.Path))
                        .ToList();

                    lock (allFiles)
                    {
                        // 1. Remove files that belonged to deleted folders or deleted paths
                        allFiles.RemoveAll(item =>
                        {
                            if (IsPathInDeletedFolders(item.FilePath)) return true;
                            if (validVirtualFiles.Contains(item.FilePathNorm)) return false;
                            bool inLibrary = libraryDirs.Any(ld =>
                                item.FilePathNorm.StartsWith(ld + "\\", StringComparison.OrdinalIgnoreCase) ||
                                item.FilePathNorm.Equals(ld, StringComparison.OrdinalIgnoreCase));
                            return !inLibrary;
                        });

                        // 2. Add any new files referenced in master VirtualFolders that are not yet in allFiles
                        var existingFilesNorm = new HashSet<string>(allFiles.Select(f => f.FilePathNorm), StringComparer.OrdinalIgnoreCase);
                        foreach (var vf in Settings.VirtualFolders ?? new List<VirtualFolder>())
                        {
                            if (vf.Files == null) continue;
                            foreach (var fp in vf.Files)
                            {
                                if (string.IsNullOrWhiteSpace(fp)) continue;
                                string norm = NormalizePath(fp);
                                if (!string.IsNullOrEmpty(norm) && !existingFilesNorm.Contains(norm))
                                {
                                    if (File.Exists(fp))
                                    {
                                        var tags = GetTagsForFile(fp, Path.GetFileName(fp));
                                        var item = CreateSfxItemFast(fp, tags, 0, 0, null);
                                        allFiles.Add(item);
                                        existingFilesNorm.Add(norm);
                                    }
                                }
                            }
                        }

                        // 3. Re-apply tags to all loaded in-memory files
                        foreach (var item in allFiles)
                        {
                            item.Tags = GetTagsForFile(item.FilePath, item.FileName);
                            item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
                        }
                    }

                    // Save to local settings file ONLY (do not push back to Dropbox since we just read from it!)
                    LastLocalSaveUtc = DateTime.UtcNow;
                    string localJson = JsonSerializer.Serialize(Settings, _jsonSaveOptions);
                    await File.WriteAllTextAsync(SettingsFile, localJson).ConfigureAwait(false);

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Sync error: {ex.Message}");
            }

            return false;
        }

        public List<string> GetAllUniqueTags(IEnumerable<SfxItem> items)
        {
            if (!_fastTagIndexBuilt)
            {
                RebuildFastTagIndex();
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in Settings.CustomTags)
            {
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            }

            foreach (var tagList in _fastTagsByNormPath.Values)
            {
                foreach (var t in tagList)
                {
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
                }
            }

            foreach (var item in items)
            {
                foreach (var t in item.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
                }
            }

            return set.Where(t => !_fastDeletedTagsNorm.Contains(SearchNormalizer.Normalize(t)))
                      .OrderBy(t => t)
                      .ToList();
        }

        public static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string p = path.Replace('/', '\\').Trim().TrimEnd('\\');
            while (p.Contains(@"\\"))
            {
                p = p.Replace(@"\\", @"\");
            }
            return p.ToLowerInvariant();
        }

        public List<VirtualFolderNode> BuildVirtualFolderHierarchy(bool includeSubfoldersInCount = false)
        {
            var nodes = new Dictionary<string, VirtualFolderNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var vf in Settings.VirtualFolders)
            {
                if (string.IsNullOrWhiteSpace(vf.Id)) continue;

                var node = new VirtualFolderNode
                {
                    Id = vf.Id,
                    Name = vf.Name,
                    ParentId = string.IsNullOrWhiteSpace(vf.ParentId) ? null : vf.ParentId,
                    LinkedPath = vf.LinkedPath,
                    AutoSync = vf.AutoSync,
                    IsExpanded = vf.IsOpen,
                    IncludeSubfoldersInCount = includeSubfoldersInCount
                };

                foreach (var f in vf.Files)
                {
                    string np = NormalizePath(f);
                    if (!string.IsNullOrEmpty(np)) node.DirectFiles.Add(np);
                }

                nodes[vf.Id] = node;
            }

            var roots = new List<VirtualFolderNode>();

            foreach (var node in nodes.Values)
            {
                if (string.IsNullOrEmpty(node.ParentId) || !nodes.TryGetValue(node.ParentId, out var parent))
                {
                    roots.Add(node);
                }
                else
                {
                    parent.Children.Add(node);
                }
            }

            // Recursively calculate descendant files and ensure parent DirectFiles exclude subfolder files
            void CalculateDescendants(VirtualFolderNode n)
            {
                foreach (var child in n.Children)
                {
                    CalculateDescendants(child);
                    n.DirectFiles.ExceptWith(child.AllDescendantFiles);
                    n.AllDescendantFiles.UnionWith(child.AllDescendantFiles);
                }

                if (!string.IsNullOrWhiteSpace(n.LinkedPath) && n.Children.Count > 0)
                {
                    string normLink = NormalizePath(n.LinkedPath);
                    n.DirectFiles.RemoveWhere(np =>
                    {
                        string fDir = NormalizePath(Path.GetDirectoryName(np));
                        return !string.IsNullOrEmpty(fDir) && !fDir.Equals(normLink, StringComparison.OrdinalIgnoreCase);
                    });
                }

                n.AllDescendantFiles.UnionWith(n.DirectFiles);
            }

            foreach (var r in roots)
            {
                CalculateDescendants(r);
            }

            return roots;
        }

        public void SetVirtualFolderOpenState(string folderId, bool isOpen)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase));
            if (vf != null && vf.IsOpen != isOpen)
            {
                vf.IsOpen = isOpen;
                _ = SaveSettingsAsync();
            }
        }

        #region DaVinci Resolve Style Bin & Import Management

        public async Task<VirtualFolder> AddVirtualFolderAsync(string name, string? parentId = null, string? linkedPath = null)
        {
            var vf = new VirtualFolder
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "New Bin" : name.Trim(),
                ParentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId,
                LinkedPath = linkedPath,
                IsOpen = false
            };
            Settings.VirtualFolders.Add(vf);
            await SaveSettingsAsync();
            return vf;
        }

        public async Task<bool> RenameVirtualFolderAsync(string folderId, string newName)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id == folderId);
            if (vf == null) return false;
            vf.Name = string.IsNullOrWhiteSpace(newName) ? vf.Name : newName.Trim();
            await SaveSettingsAsync();
            return true;
        }

        public Task<bool> DeleteVirtualFolderAsync(string folderId, List<SfxItem>? allFiles = null)
        {
            return DeleteVirtualFoldersAsync(new[] { folderId }, allFiles);
        }

        public async Task<bool> DeleteVirtualFoldersAsync(IEnumerable<string> folderIds, List<SfxItem>? allFiles = null)
        {
            var toDelete = new HashSet<string>(folderIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (toDelete.Count == 0) return false;

            bool addedAny;
            do
            {
                addedAny = false;
                foreach (var f in Settings.VirtualFolders)
                {
                    if (!toDelete.Contains(f.Id) && f.ParentId != null && toDelete.Contains(f.ParentId))
                    {
                        toDelete.Add(f.Id);
                        addedAny = true;
                    }
                }
            } while (addedAny);

            Settings.DeletedFolderIds ??= new List<string>();
            Settings.DeletedFolderPaths ??= new List<string>();

            var folderById = Settings.VirtualFolders.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
            var deletedFilesNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? TryResolvePhysicalPath(VirtualFolder vf)
            {
                if (!string.IsNullOrWhiteSpace(vf.LinkedPath))
                {
                    return vf.LinkedPath;
                }
                if (!string.IsNullOrWhiteSpace(vf.ParentId) && folderById.TryGetValue(vf.ParentId, out var parentVf))
                {
                    string? parentPhys = TryResolvePhysicalPath(parentVf);
                    if (!string.IsNullOrWhiteSpace(parentPhys))
                    {
                        string candidate = Path.Combine(parentPhys, vf.Name);
                        if (Directory.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
                if (vf.Files != null && vf.Files.Count > 0)
                {
                    var dirs = vf.Files
                        .Select(Path.GetDirectoryName)
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (dirs.Count == 1 && Directory.Exists(dirs[0]!))
                    {
                        return dirs[0];
                    }
                }
                return null;
            }

            foreach (var f in Settings.VirtualFolders.Where(x => toDelete.Contains(x.Id)))
            {
                if (!Settings.DeletedFolderIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.DeletedFolderIds.Add(f.Id);
                }

                string? physPath = TryResolvePhysicalPath(f);
                if (!string.IsNullOrWhiteSpace(physPath))
                {
                    string normPhys = NormalizePath(physPath);
                    if (!string.IsNullOrEmpty(normPhys) &&
                        !Settings.DeletedFolderPaths.Any(dp => NormalizePath(dp).Equals(normPhys, StringComparison.OrdinalIgnoreCase)))
                    {
                        Settings.DeletedFolderPaths.Add(normPhys);
                    }
                }

                foreach (var filePath in f.Files)
                {
                    string nf = NormalizePath(filePath);
                    if (!string.IsNullOrEmpty(nf))
                    {
                        deletedFilesNorm.Add(nf);
                    }
                }
            }

            Settings.VirtualFolders.RemoveAll(f => toDelete.Contains(f.Id));

            // Remove any deleted child files that might have been duplicated in remaining parent folders
            foreach (var remainingVf in Settings.VirtualFolders)
            {
                if (remainingVf.Files != null && remainingVf.Files.Count > 0)
                {
                    remainingVf.Files.RemoveAll(fp =>
                    {
                        string nf = NormalizePath(fp);
                        return deletedFilesNorm.Contains(nf) || IsPathInDeletedFolders(fp);
                    });
                }
            }

            if (allFiles != null)
            {
                var remainingFilesNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var remainingVf in Settings.VirtualFolders)
                {
                    foreach (var fp in remainingVf.Files)
                    {
                        string nf = NormalizePath(fp);
                        if (!string.IsNullOrEmpty(nf))
                        {
                            remainingFilesNorm.Add(nf);
                        }
                    }
                }

                lock (allFiles)
                {
                    allFiles.RemoveAll(item =>
                        !remainingFilesNorm.Contains(item.FilePathNorm) &&
                        (deletedFilesNorm.Contains(item.FilePathNorm) || IsPathInDeletedFolders(item.FilePath)));
                }
            }

            await SaveSettingsAsync();
            return true;
        }

        public bool IsDescendantOf(string childId, string potentialAncestorId)
        {
            if (string.IsNullOrEmpty(childId) || string.IsNullOrEmpty(potentialAncestorId)) return false;
            string? checkId = childId;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrEmpty(checkId) && visited.Add(checkId))
            {
                var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(checkId, StringComparison.OrdinalIgnoreCase));
                if (vf == null) break;
                if (!string.IsNullOrEmpty(vf.ParentId) && vf.ParentId.Equals(potentialAncestorId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                checkId = vf.ParentId;
            }
            return false;
        }

        public async Task<bool> MoveVirtualFolderAsync(string folderId, string? newParentId)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase));
            if (vf == null) return false;

            // Cannot set self as parent
            if (!string.IsNullOrEmpty(newParentId) && newParentId.Equals(folderId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Already has this parent
            if (string.Equals(vf.ParentId, newParentId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Cannot move into a descendant (prevent cycles)
            if (!string.IsNullOrEmpty(newParentId) && IsDescendantOf(newParentId, folderId))
            {
                return false;
            }

            vf.ParentId = string.IsNullOrWhiteSpace(newParentId) ? null : newParentId;
            await SaveSettingsAsync();
            return true;
        }

        public async Task<bool> AddFilesToVirtualFolderAsync(string folderId, IEnumerable<string> filePaths)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id == folderId);
            if (vf == null) return false;

            var existing = new HashSet<string>(vf.Files.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var p in filePaths)
            {
                string np = NormalizePath(p);
                if (!existing.Contains(np))
                {
                    vf.Files.Add(p);
                    existing.Add(np);
                    changed = true;
                }
            }

            if (changed) await SaveSettingsAsync();
            return changed;
        }

        public async Task<bool> RemoveFilesFromVirtualFolderAsync(string folderId, IEnumerable<string> filePaths)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id == folderId);
            if (vf == null) return false;

            var toRemove = new HashSet<string>(filePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
            int removed = vf.Files.RemoveAll(f => toRemove.Contains(NormalizePath(f)));
            if (removed > 0)
            {
                await SaveSettingsAsync();
                return true;
            }
            return false;
        }

        public async Task<int> DeleteMediaFilesAsync(IEnumerable<string> filePaths, List<SfxItem>? allFiles = null, string? targetFolderId = null)
        {
            var toRemoveNorm = new HashSet<string>(
                filePaths.Select(NormalizePath).Where(p => !string.IsNullOrEmpty(p)),
                StringComparer.OrdinalIgnoreCase);

            if (toRemoveNorm.Count == 0) return 0;

            Settings.DeletedFolderPaths ??= new List<string>();
            var existingDeletedNorm = new HashSet<string>(
                Settings.DeletedFolderPaths.Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (var np in toRemoveNorm)
            {
                if (!existingDeletedNorm.Contains(np))
                {
                    Settings.DeletedFolderPaths.Add(np);
                    existingDeletedNorm.Add(np);
                }
            }

            foreach (var vf in Settings.VirtualFolders)
            {
                if (!string.IsNullOrEmpty(targetFolderId))
                {
                    bool inScope = vf.Id.Equals(targetFolderId, StringComparison.OrdinalIgnoreCase) ||
                                   IsDescendantOf(vf.Id, targetFolderId);
                    if (!inScope) continue;
                }

                vf.Files?.RemoveAll(f => toRemoveNorm.Contains(NormalizePath(f)));
            }

            if (allFiles != null)
            {
                var remainingFilesNorm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var remainingVf in Settings.VirtualFolders)
                {
                    if (remainingVf.Files == null) continue;
                    foreach (var fp in remainingVf.Files)
                    {
                        string nf = NormalizePath(fp);
                        if (!string.IsNullOrEmpty(nf))
                        {
                            remainingFilesNorm.Add(nf);
                        }
                    }
                }

                lock (allFiles)
                {
                    allFiles.RemoveAll(item =>
                        toRemoveNorm.Contains(item.FilePathNorm) &&
                        (string.IsNullOrEmpty(targetFolderId) || !remainingFilesNorm.Contains(item.FilePathNorm)));
                }
            }

            await SaveSettingsAsync();
            return toRemoveNorm.Count;
        }

        public async Task<List<SfxItem>> ImportAudioFilesAsync(IEnumerable<string> filePaths, List<SfxItem> allFiles, string? targetFolderId = null)
        {
            var addedItems = new List<SfxItem>();
            var existingMap = allFiles.ToDictionary(f => f.FilePathNorm, StringComparer.OrdinalIgnoreCase);
            var pathsToAdd = new List<string>();

            await Task.Run(() =>
            {
                foreach (var path in filePaths)
                {
                    string ext = Path.GetExtension(path);
                    if (!File.Exists(path) || !MediaExtensions.Contains(ext)) continue;

                    string norm = NormalizePath(path);
                    pathsToAdd.Add(path);

                    if (!existingMap.TryGetValue(norm, out var item))
                    {
                        try
                        {
                            var fi = new FileInfo(path);
                            bool isVid = IsVideoFile(ext);
                            bool isAud = IsAudioFile(ext);
                            bool isCloud = VideoPreviewService.IsCloudPlaceholder(path);
                            double dur = 0;

                            if (isAud && !isCloud)
                            {
                                try
                                {
                                    using var reader = new UniversalAudioReader(path);
                                    dur = reader.TotalTime.TotalSeconds;
                                }
                                catch { }
                            }

                            var tags = GetTagsForFile(path, fi.Name);
                            item = CreateSfxItemFast(path, tags, dur, fi.Length);

                            allFiles.Add(item);
                            addedItems.Add(item);
                            existingMap[norm] = item;
                        }
                        catch { }
                    }
                }
            });

            if (pathsToAdd.Count > 0 && Settings.DeletedFolderPaths != null && Settings.DeletedFolderPaths.Count > 0)
            {
                var readdedNorm = new HashSet<string>(pathsToAdd.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
                Settings.DeletedFolderPaths.RemoveAll(dp => readdedNorm.Contains(NormalizePath(dp)));
            }

            if (!string.IsNullOrEmpty(targetFolderId) && pathsToAdd.Count > 0)
            {
                await AddFilesToVirtualFolderAsync(targetFolderId, pathsToAdd);
            }

            if (addedItems.Count > 0 || pathsToAdd.Count > 0)
            {
                await SaveSettingsAsync();
            }

            return addedItems;
        }

        public async Task<VirtualFolder?> ImportFolderAsBinHierarchyAsync(string dirPath, List<SfxItem> allFiles, string? parentFolderId = null)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return null;

            var dirInfo = new DirectoryInfo(dirPath);
            string folderName = dirInfo.Name;
            string normRootDir = NormalizePath(dirInfo.FullName);
            string? cleanParentId = string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId;

            // User explicitly imported this folder: un-blacklist its path if it was previously deleted
            Settings.DeletedFolderPaths?.RemoveAll(dp =>
            {
                string normDp = NormalizePath(dp);
                return normDp.Equals(normRootDir, StringComparison.OrdinalIgnoreCase) ||
                       normDp.StartsWith(normRootDir + "\\", StringComparison.OrdinalIgnoreCase);
            });

            // 1. Find existing Bin under the same parent or create a new root Bin for this dropped folder
            var rootBin = Settings.VirtualFolders.FirstOrDefault(f =>
                string.Equals(f.ParentId, cleanParentId, StringComparison.OrdinalIgnoreCase) &&
                ((!string.IsNullOrWhiteSpace(f.LinkedPath) && NormalizePath(f.LinkedPath).Equals(normRootDir, StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase)));

            if (rootBin == null)
            {
                rootBin = new VirtualFolder
                {
                    Id = "vf_" + Guid.NewGuid().ToString("N")[..12],
                    Name = folderName,
                    ParentId = cleanParentId,
                    LinkedPath = dirInfo.FullName,
                    AutoSync = true,
                    IsOpen = true
                };
                Settings.VirtualFolders.Add(rootBin);
            }
            else
            {
                rootBin.LinkedPath = dirInfo.FullName;
                rootBin.AutoSync = true;
                rootBin.IsOpen = true;
            }

            await Task.Run(() =>
            {
                // 2. Preserve full nested subdirectory structure (even empty or intermediate subfolders)
                var dirToBin = new Dictionary<string, VirtualFolder>(StringComparer.OrdinalIgnoreCase)
                {
                    [normRootDir] = rootBin
                };

                foreach (var vf in Settings.VirtualFolders)
                {
                    if (!string.IsNullOrWhiteSpace(vf.LinkedPath))
                    {
                        dirToBin[NormalizePath(vf.LinkedPath)] = vf;
                    }
                }

                try
                {
                    var allSubDirs = dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                        .Where(d => !d.Name.StartsWith("."))
                        .OrderBy(d => d.FullName.Length)
                        .ToList();

                    foreach (var subDir in allSubDirs)
                    {
                        string relDir = Path.GetRelativePath(dirInfo.FullName, subDir.FullName);
                        string[] parts = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                        VirtualFolder currentParent = rootBin;
                        string currentPhys = dirInfo.FullName;

                        foreach (var part in parts)
                        {
                            currentPhys = Path.Combine(currentPhys, part);
                            string normCurPhys = NormalizePath(currentPhys);

                            if (!dirToBin.TryGetValue(normCurPhys, out var childVf))
                            {
                                childVf = Settings.VirtualFolders.FirstOrDefault(f =>
                                    string.Equals(f.ParentId, currentParent.Id, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));

                                if (childVf == null)
                                {
                                    childVf = new VirtualFolder
                                    {
                                        Id = "vf_" + Guid.NewGuid().ToString("N")[..12],
                                        Name = part,
                                        ParentId = currentParent.Id,
                                        LinkedPath = currentPhys,
                                        AutoSync = true,
                                        IsOpen = false
                                    };
                                    Settings.VirtualFolders.Add(childVf);
                                }
                                else
                                {
                                    childVf.LinkedPath = currentPhys;
                                    childVf.AutoSync = true;
                                }

                                dirToBin[normCurPhys] = childVf;
                            }

                            currentParent = childVf;
                        }
                    }
                }
                catch { }

                // 3. Populate all media files across the entire folder hierarchy in one fast pass
                var allFilesMap = new Dictionary<string, SfxItem>(StringComparer.OrdinalIgnoreCase);
                lock (allFiles)
                {
                    foreach (var item in allFiles)
                    {
                        allFilesMap[item.FilePathNorm] = item;
                    }
                }

                SyncSingleLinkedFolderCore(rootBin, allFiles, allFilesMap, forceAllMediaExtensions: true);
            });

            await SaveSettingsAsync();
            return rootBin;
        }

        public async Task<VirtualFolder?> UpdateVirtualFolderLinkAsync(string folderId, string? newName, string? linkedPath, bool autoSync)
        {
            var vf = Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase));
            if (vf == null) return null;

            if (!string.IsNullOrWhiteSpace(newName))
            {
                vf.Name = newName.Trim();
            }

            vf.LinkedPath = string.IsNullOrWhiteSpace(linkedPath) ? null : linkedPath.Trim();
            vf.AutoSync = !string.IsNullOrWhiteSpace(vf.LinkedPath) && autoSync;

            if (!string.IsNullOrWhiteSpace(vf.LinkedPath))
            {
                string normLinked = NormalizePath(vf.LinkedPath);
                Settings.DeletedFolderPaths?.RemoveAll(dp =>
                {
                    string normDp = NormalizePath(dp);
                    return normDp.Equals(normLinked, StringComparison.OrdinalIgnoreCase);
                });
            }

            await SaveSettingsAsync();
            return vf;
        }

        public int CountLinkedFoldersInSubtree(string folderId)
        {
            int count = 0;
            foreach (var vf in Settings.VirtualFolders)
            {
                if ((vf.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase) || IsDescendantOf(vf.Id, folderId))
                    && !string.IsNullOrWhiteSpace(vf.LinkedPath))
                {
                    count++;
                }
            }
            return count;
        }

        public async Task<int> UnlinkVirtualFolderAsync(string folderId, bool includeDescendants = true)
        {
            int unlinkedCount = 0;
            foreach (var vf in Settings.VirtualFolders)
            {
                bool match = vf.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase) ||
                             (includeDescendants && IsDescendantOf(vf.Id, folderId));
                if (match && (!string.IsNullOrWhiteSpace(vf.LinkedPath) || vf.AutoSync))
                {
                    if (!string.IsNullOrWhiteSpace(vf.LinkedPath))
                    {
                        unlinkedCount++;
                    }
                    vf.LinkedPath = null;
                    vf.AutoSync = false;
                }
            }

            if (unlinkedCount > 0)
            {
                await SaveSettingsAsync();
            }
            return unlinkedCount;
        }

        public List<string> GetRootLinkedDirectories()
        {
            var allDirs = Settings.VirtualFolders
                .Where(f => f.AutoSync && !string.IsNullOrWhiteSpace(f.LinkedPath) && Directory.Exists(f.LinkedPath))
                .Select(f => f.LinkedPath!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length)
                .ToList();

            var roots = new List<string>();
            foreach (var dir in allDirs)
            {
                string norm = NormalizePath(dir);
                bool covered = roots.Any(r =>
                {
                    string nr = NormalizePath(r);
                    return norm.Equals(nr, StringComparison.OrdinalIgnoreCase) ||
                           norm.StartsWith(nr + "\\", StringComparison.OrdinalIgnoreCase);
                });
                if (!covered)
                {
                    roots.Add(dir);
                }
            }
            return roots;
        }

        public async Task<(int AddedFiles, int RemovedFiles, int NewSubBins)> SyncVirtualFolderAsync(string folderId, List<SfxItem> allFiles)
        {
            var rootVf = Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase));
            if (rootVf == null || string.IsNullOrWhiteSpace(rootVf.LinkedPath) || !Directory.Exists(rootVf.LinkedPath))
            {
                return (0, 0, 0);
            }

            int added = 0, removed = 0, newBins = 0;
            await Task.Run(() =>
            {
                var allFilesMap = new Dictionary<string, SfxItem>(StringComparer.OrdinalIgnoreCase);
                lock (allFiles)
                {
                    foreach (var item in allFiles)
                    {
                        allFilesMap[item.FilePathNorm] = item;
                    }
                }

                var res = SyncSingleLinkedFolderCore(rootVf, allFiles, allFilesMap);
                added = res.AddedFiles;
                removed = res.RemovedFiles;
                newBins = res.NewSubBins;
            });

            if (added > 0 || removed > 0 || newBins > 0)
            {
                await SaveSettingsAsync();
            }

            return (added, removed, newBins);
        }

        public async Task<(int AddedFiles, int RemovedFiles, int NewSubBins)> SyncAllLinkedFoldersAsync(List<SfxItem> allFiles, bool autoOnly = true)
        {
            int totalAdded = 0, totalRemoved = 0, totalNewBins = 0;

            await Task.Run(() =>
            {
                var candidates = Settings.VirtualFolders
                    .Where(f => !string.IsNullOrWhiteSpace(f.LinkedPath) && (!autoOnly || f.AutoSync) && Directory.Exists(f.LinkedPath))
                    .OrderBy(f => string.IsNullOrEmpty(f.ParentId) ? 0 : 1)
                    .ThenBy(f => f.LinkedPath!.Length)
                    .ToList();

                if (candidates.Count == 0) return;

                var allFilesMap = new Dictionary<string, SfxItem>(StringComparer.OrdinalIgnoreCase);
                lock (allFiles)
                {
                    foreach (var item in allFiles)
                    {
                        allFilesMap[item.FilePathNorm] = item;
                    }
                }

                var scannedPhysicalRoots = new List<string>();

                foreach (var vf in candidates)
                {
                    string normDir = NormalizePath(vf.LinkedPath);
                    bool alreadyCovered = scannedPhysicalRoots.Any(r =>
                        normDir.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                        normDir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));

                    if (alreadyCovered) continue;

                    scannedPhysicalRoots.Add(normDir);
                    var res = SyncSingleLinkedFolderCore(vf, allFiles, allFilesMap);
                    totalAdded += res.AddedFiles;
                    totalRemoved += res.RemovedFiles;
                    totalNewBins += res.NewSubBins;
                }

                if (totalRemoved > 0)
                {
                    lock (allFiles)
                    {
                        allFiles.RemoveAll(item => !string.IsNullOrEmpty(item.FilePath) && !File.Exists(item.FilePath));
                    }
                }
            });

            if (totalAdded > 0 || totalRemoved > 0 || totalNewBins > 0)
            {
                await SaveSettingsAsync();
            }

            return (totalAdded, totalRemoved, totalNewBins);
        }

        private (int AddedFiles, int RemovedFiles, int NewSubBins) SyncSingleLinkedFolderCore(
            VirtualFolder rootVf,
            List<SfxItem> allFiles,
            Dictionary<string, SfxItem> allFilesMap,
            bool forceAllMediaExtensions = false)
        {
            string rootDir = rootVf.LinkedPath!;
            string normRootDir = NormalizePath(rootDir);

            if (!Directory.Exists(rootDir) || IsPathInDeletedFolders(rootDir)) return (0, 0, 0);

            int addedFiles = 0, removedFiles = 0, newSubBins = 0;

            // 1. Collect all VirtualFolders in rootVf's subtree
            var subtreeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootVf.Id };
            bool grew;
            do
            {
                grew = false;
                foreach (var f in Settings.VirtualFolders)
                {
                    if (!subtreeIds.Contains(f.Id) && !string.IsNullOrEmpty(f.ParentId) && subtreeIds.Contains(f.ParentId))
                    {
                        subtreeIds.Add(f.Id);
                        grew = true;
                    }
                }
            } while (grew);

            var subtreeFolders = Settings.VirtualFolders.Where(f => subtreeIds.Contains(f.Id)).ToList();

            // 2. Remove stale files that no longer exist on disk & build set of existing files in subtree
            var allExistingInSubtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linkedDirToFolder = new Dictionary<string, VirtualFolder>(StringComparer.OrdinalIgnoreCase);

            foreach (var vf in subtreeFolders)
            {
                if (!string.IsNullOrWhiteSpace(vf.LinkedPath))
                {
                    linkedDirToFolder[NormalizePath(vf.LinkedPath)] = vf;
                }

                if (!string.IsNullOrWhiteSpace(vf.LinkedPath) && Directory.Exists(vf.LinkedPath))
                {
                    int before = vf.Files.Count;
                    var deadFiles = vf.Files.Where(p => !string.IsNullOrWhiteSpace(p) && !File.Exists(p)).ToList();
                    if (deadFiles.Count > 0)
                    {
                        foreach (var df in deadFiles)
                        {
                            string normDf = NormalizePath(df);
                            if (!string.IsNullOrEmpty(normDf))
                            {
                                allFilesMap.Remove(normDf);
                            }
                        }
                        var deadSet = new HashSet<string>(deadFiles, StringComparer.OrdinalIgnoreCase);
                        vf.Files.RemoveAll(p => deadSet.Contains(p));
                        removedFiles += (before - vf.Files.Count);
                    }
                }

                foreach (var fPath in vf.Files)
                {
                    string np = NormalizePath(fPath);
                    if (!string.IsNullOrEmpty(np))
                    {
                        allExistingInSubtree.Add(np);
                    }
                }
            }

            bool isAudioOnlyTree = !forceAllMediaExtensions && (
                rootVf.Id.Equals("vf_1790526643864_9kcvl", StringComparison.OrdinalIgnoreCase) ||
                rootVf.Id.Equals("vf_kho_nhac", StringComparison.OrdinalIgnoreCase) ||
                IsDescendantOf(rootVf.Id, "vf_1790526643864_9kcvl") ||
                IsDescendantOf(rootVf.Id, "vf_kho_nhac"));

            bool isVideoOverlayTree = !forceAllMediaExtensions && (
                rootVf.Id.Equals("vf_video_overlay", StringComparison.OrdinalIgnoreCase) ||
                rootVf.Name.Equals("Video Overlay", StringComparison.OrdinalIgnoreCase) ||
                IsDescendantOf(rootVf.Id, "vf_video_overlay"));

            var allowedExts = isAudioOnlyTree
                ? AudioExtensions
                : (isVideoOverlayTree ? VideoExtensions : MediaExtensions);

            // 3. Scan disk directory for matching media files
            IEnumerable<string> diskFiles;
            try
            {
                diskFiles = Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => allowedExts.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith("._"));
            }
            catch
            {
                return (addedFiles, removedFiles, newSubBins);
            }

            foreach (var filePath in diskFiles)
            {
                // Skip any file belonging to a deleted Bin path!
                if (IsPathInDeletedFolders(filePath)) continue;

                string normFile = NormalizePath(filePath);
                if (string.IsNullOrEmpty(normFile)) continue;

                // Ensure file is in global _allFiles list
                if (!allFilesMap.ContainsKey(normFile))
                {
                    try
                    {
                        var tags = GetTagsForFile(filePath, Path.GetFileName(filePath));
                        var newItem = CreateSfxItemFast(filePath, tags);
                        lock (allFiles)
                        {
                            allFiles.Add(newItem);
                        }
                        allFilesMap[normFile] = newItem;
                    }
                    catch { }
                }

                // If already in rootVf or any of its sub-bins, skip
                if (allExistingInSubtree.Contains(normFile)) continue;

                // New file discovered on disk! Place into matching sub-bin or rootVf
                string fileDir = Path.GetDirectoryName(filePath) ?? rootDir;
                string normFileDir = NormalizePath(fileDir);

                if (IsPathInDeletedFolders(fileDir)) continue;

                VirtualFolder? targetBin = rootVf;

                if (linkedDirToFolder.TryGetValue(normFileDir, out var exactBin))
                {
                    targetBin = exactBin;
                }
                else if (!normFileDir.Equals(normRootDir, StringComparison.OrdinalIgnoreCase) &&
                         normFileDir.StartsWith(normRootDir + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    string relDir = Path.GetRelativePath(rootDir, fileDir);
                    string[] parts = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                    VirtualFolder currentParent = rootVf;
                    string currentPhys = rootDir;
                    bool skippedSubtree = false;

                    foreach (var part in parts)
                    {
                        currentPhys = Path.Combine(currentPhys, part);
                        string normCurPhys = NormalizePath(currentPhys);

                        if (IsPathInDeletedFolders(currentPhys))
                        {
                            skippedSubtree = true;
                            break;
                        }

                        if (!linkedDirToFolder.TryGetValue(normCurPhys, out var childVf))
                        {
                            childVf = Settings.VirtualFolders.FirstOrDefault(f =>
                                string.Equals(f.ParentId, currentParent.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));

                            if (childVf == null)
                            {
                                childVf = new VirtualFolder
                                {
                                    Id = "vf_" + Guid.NewGuid().ToString("N")[..12],
                                    Name = part,
                                    ParentId = currentParent.Id,
                                    LinkedPath = currentPhys,
                                    AutoSync = true,
                                    IsOpen = false
                                };
                                Settings.VirtualFolders.Add(childVf);
                                subtreeIds.Add(childVf.Id);
                                newSubBins++;
                                linkedDirToFolder[normCurPhys] = childVf;
                            }
                            else if (string.IsNullOrWhiteSpace(childVf.LinkedPath))
                            {
                                // Child bin was explicitly unlinked by user; do not re-link or auto-populate
                                skippedSubtree = true;
                                break;
                            }
                            else
                            {
                                linkedDirToFolder[normCurPhys] = childVf;
                            }
                        }

                        currentParent = childVf;
                    }

                    if (skippedSubtree) continue;
                    targetBin = currentParent;
                }

                if (targetBin != null)
                {
                    targetBin.Files.Add(filePath);
                    allExistingInSubtree.Add(normFile);
                    addedFiles++;
                }
            }

            return (addedFiles, removedFiles, newSubBins);
        }

        #endregion
    }
}
