using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZStationStudio.Services
{
    public class UpdateManifest
    {
        public string Version { get; set; } = "1.2.2";
        public string PublishedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public string ReleaseNotes { get; set; } = string.Empty;
        public string PackageUrl { get; set; } = UpdateService.DefaultPackageUrl;
        public string ManifestUrl { get; set; } = UpdateService.DefaultManifestUrl;
        public long PackageSizeBytes { get; set; }
        public long SourceExeLastWriteUtcTicks { get; set; }
        public string SourceExeSha256 { get; set; } = string.Empty;
    }

    public class UpdateSourceConfig
    {
        public string ManifestUrl { get; set; } = UpdateService.DefaultManifestUrl;
        public string PackageUrl { get; set; } = UpdateService.DefaultPackageUrl;
        public string GitRepoUrl { get; set; } = UpdateService.DefaultGitRepoUrl;
        public string SharedUpdatesFolder { get; set; } = @"F:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\Updates";
    }

    public class UpdateCheckResult
    {
        public bool IsSuccess { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public bool HasUnpublishedLocalBuild { get; set; }
        public string CurrentVersion { get; set; } = "1.2.3";
        public UpdateManifest? LatestManifest { get; set; }
        public string? LocalPackagePath { get; set; }
        public string? SourceDescription { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class UpdateService
    {
        public const string DefaultAppVersion = "1.2.3";
        public const string DefaultGitRepoUrl = "https://github.com/dincenzo2-wq/Z-Station-Studio.git";
        public const string DefaultManifestUrl = "https://raw.githubusercontent.com/dincenzo2-wq/Z-Station-Studio/main/version.json";
        public const string DefaultPackageUrl = "https://github.com/dincenzo2-wq/Z-Station-Studio/raw/main/Z-Station_Studio_Update.zip";
        public const string ManifestFileName = "version.json";
        public const string PackageFileName = "Z-Station_Studio_Update.zip";
        public const string ConfigFileName = "update_source.json";
        public const string LocalVersionFileName = "app_version.json";

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        static UpdateService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ZStationStudio-Updater/1.0");
        }

        public string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        public static string ComputeFileSha256(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return string.Empty;
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return string.Empty;
            }
        }

        public string GetCurrentVersion()
        {
            string bestVer = DefaultAppVersion;

            try
            {
                string localVerFile = Path.Combine(BaseDir, LocalVersionFileName);
                if (File.Exists(localVerFile))
                {
                    var doc = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(localVerFile));
                    if (doc != null && !string.IsNullOrWhiteSpace(doc.Version))
                    {
                        bestVer = doc.Version.Trim();
                    }
                }
            }
            catch { }

            // Compare running .exe binary against Updates\version.json if available locally
            try
            {
                string? updatesDir = ResolveLocalUpdatesDirectory();
                if (!string.IsNullOrEmpty(updatesDir))
                {
                    string pubVerFile = Path.Combine(updatesDir, ManifestFileName);
                    if (File.Exists(pubVerFile))
                    {
                        var pubDoc = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(pubVerFile));
                        if (pubDoc != null && !string.IsNullOrWhiteSpace(pubDoc.Version) && CompareVersions(pubDoc.Version, bestVer) > 0)
                        {
                            string currentExe = Environment.ProcessPath ?? Path.Combine(BaseDir, "Z-Station_Studio.exe");
                            if (File.Exists(currentExe))
                            {
                                // 1. If the running .exe has the exact same SHA-256 fingerprint as the published version, it IS that version
                                if (!string.IsNullOrWhiteSpace(pubDoc.SourceExeSha256))
                                {
                                    string curHash = ComputeFileSha256(currentExe);
                                    if (!string.IsNullOrEmpty(curHash) && curHash.Equals(pubDoc.SourceExeSha256, StringComparison.OrdinalIgnoreCase))
                                    {
                                        bestVer = pubDoc.Version.Trim();
                                    }
                                }

                                // 2. If running a freshly compiled dev build newer than the published manifest on the dev machine
                                var parentDir = Directory.GetParent(updatesDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
                                bool hasDevSource = !string.IsNullOrEmpty(parentDir) && Directory.Exists(Path.Combine(parentDir, "NativeApp"));
                                if (hasDevSource && pubDoc.SourceExeLastWriteUtcTicks > 0)
                                {
                                    long exeTicks = File.GetLastWriteTimeUtc(currentExe).Ticks;
                                    if (exeTicks >= pubDoc.SourceExeLastWriteUtcTicks - TimeSpan.FromSeconds(5).Ticks)
                                    {
                                        bestVer = pubDoc.Version.Trim();
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return bestVer;
        }

        public UpdateManifest? GetLocalManifest()
        {
            try
            {
                string localVerFile = Path.Combine(BaseDir, LocalVersionFileName);
                if (File.Exists(localVerFile))
                {
                    return JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(localVerFile));
                }
            }
            catch { }
            return null;
        }

        public UpdateSourceConfig LoadSourceConfig()
        {
            var config = new UpdateSourceConfig();
            try
            {
                string cfgPath = Path.Combine(BaseDir, ConfigFileName);
                if (File.Exists(cfgPath))
                {
                    var loaded = JsonSerializer.Deserialize<UpdateSourceConfig>(File.ReadAllText(cfgPath));
                    if (loaded != null) config = loaded;
                }
                else
                {
                    // Also check in discovered Updates folder
                    string? updatesDir = ResolveLocalUpdatesDirectory(config.SharedUpdatesFolder);
                    if (!string.IsNullOrEmpty(updatesDir))
                    {
                        string sharedCfgPath = Path.Combine(updatesDir, ConfigFileName);
                        if (File.Exists(sharedCfgPath))
                        {
                            var loaded = JsonSerializer.Deserialize<UpdateSourceConfig>(File.ReadAllText(sharedCfgPath));
                            if (loaded != null) config = loaded;
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(config.ManifestUrl))
                config.ManifestUrl = DefaultManifestUrl;
            if (string.IsNullOrWhiteSpace(config.PackageUrl))
                config.PackageUrl = DefaultPackageUrl;
            if (string.IsNullOrWhiteSpace(config.GitRepoUrl))
                config.GitRepoUrl = DefaultGitRepoUrl;

            return config;
        }

        public void SaveSourceConfig(UpdateSourceConfig config)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, opts);

            try
            {
                File.WriteAllText(Path.Combine(BaseDir, ConfigFileName), json, Encoding.UTF8);
            }
            catch { }

            try
            {
                string? updatesDir = ResolveLocalUpdatesDirectory(config.SharedUpdatesFolder);
                if (!string.IsNullOrEmpty(updatesDir) && Directory.Exists(updatesDir))
                {
                    File.WriteAllText(Path.Combine(updatesDir, ConfigFileName), json, Encoding.UTF8);
                }
            }
            catch { }
        }

        public string? ResolveLocalUpdatesDirectory(string? configuredFolder = null)
        {
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(configuredFolder))
            {
                candidates.Add(configuredFolder);
            }

            // 1. Check relative to current app directory
            candidates.Add(Path.Combine(BaseDir, "Updates"));
            try
            {
                var parent = Directory.GetParent(BaseDir.TrimEnd(Path.DirectorySeparatorChar));
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "Updates"));
                }
            }
            catch { }

            // 2. Check Dropbox info.json if Dropbox is installed on this PC
            try
            {
                string[] dropboxInfoPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json")
                };

                foreach (var infoPath in dropboxInfoPaths)
                {
                    if (File.Exists(infoPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                        foreach (var key in new[] { "business", "personal" })
                        {
                            if (doc.RootElement.TryGetProperty(key, out var prop) &&
                                prop.TryGetProperty("path", out var pathEl))
                            {
                                string? dbPath = pathEl.GetString();
                                if (!string.IsNullOrWhiteSpace(dbPath))
                                {
                                    candidates.Add(Path.Combine(dbPath, "Tài Nguyên Dựng Phim", "DaVinci Resolve Studio", "Z-Station_Studio", "Updates"));
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Check standard drive paths
            candidates.Add(@"F:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\Updates");
            candidates.Add(@"D:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\Updates");
            candidates.Add(@"E:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\Updates");

            foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    return dir;
                }
            }

            return null;
        }

        public static string NormalizeDirectDownloadUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
            string url = rawUrl.Trim();

            // Convert Dropbox share link to direct download stream (?dl=1)
            if (url.Contains("dropbox.com/", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("?dl=0", StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Replace("?dl=0", "?dl=1", StringComparison.OrdinalIgnoreCase);
                }
                else if (url.Contains("&dl=0", StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Replace("&dl=0", "&dl=1", StringComparison.OrdinalIgnoreCase);
                }
                else if (!url.Contains("dl=1", StringComparison.OrdinalIgnoreCase))
                {
                    url += (url.Contains('?') ? "&dl=1" : "?dl=1");
                }
            }
            // Convert Google Drive file view link to direct download link
            else if (url.Contains("drive.google.com/file/d/", StringComparison.OrdinalIgnoreCase))
            {
                int start = url.IndexOf("/file/d/", StringComparison.OrdinalIgnoreCase) + 8;
                int end = url.IndexOf('/', start);
                if (end < 0) end = url.IndexOf('?', start);
                string fileId = end > start ? url.Substring(start, end - start) : url.Substring(start);
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    url = $"https://drive.google.com/uc?export=download&id={fileId}";
                }
            }

            return url;
        }

        public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
        {
            string currentVer = GetCurrentVersion();
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVer
            };

            var config = LoadSourceConfig();
            UpdateManifest? bestManifest = null;
            string? localPkgPath = null;
            string? sourceDesc = null;
            string? lastError = null;

            // 1. Check Cloud Manifest URL if configured
            string manifestUrl = NormalizeDirectDownloadUrl(config.ManifestUrl);
            if (!string.IsNullOrWhiteSpace(manifestUrl))
            {
                try
                {
                    string bustUrl = manifestUrl + (manifestUrl.Contains('?') ? $"&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : $"?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                    string json = await _httpClient.GetStringAsync(bustUrl, ct);
                    var cloudManifest = JsonSerializer.Deserialize<UpdateManifest>(json);
                    if (cloudManifest != null && !string.IsNullOrWhiteSpace(cloudManifest.Version))
                    {
                        if (string.IsNullOrWhiteSpace(cloudManifest.PackageUrl) && !string.IsNullOrWhiteSpace(config.PackageUrl))
                        {
                            cloudManifest.PackageUrl = config.PackageUrl;
                        }
                        bestManifest = cloudManifest;
                        sourceDesc = "Cloud Online";
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"Không thể kết nối máy chủ cập nhật: {ex.Message}";
                }
            }

            // 2. Check Local / Shared Dropbox Updates Folder
            string? localUpdatesDir = ResolveLocalUpdatesDirectory(config.SharedUpdatesFolder);
            if (!string.IsNullOrEmpty(localUpdatesDir))
            {
                try
                {
                    string localManifestPath = Path.Combine(localUpdatesDir, ManifestFileName);
                    if (File.Exists(localManifestPath))
                    {
                        string json = await File.ReadAllTextAsync(localManifestPath, ct);
                        var localManifest = JsonSerializer.Deserialize<UpdateManifest>(json);
                        if (localManifest != null && !string.IsNullOrWhiteSpace(localManifest.Version))
                        {
                            if (bestManifest == null || CompareVersions(localManifest.Version, bestManifest.Version) >= 0)
                            {
                                bestManifest = localManifest;
                                string candidateZip = Path.Combine(localUpdatesDir, PackageFileName);
                                if (File.Exists(candidateZip))
                                {
                                    localPkgPath = candidateZip;
                                }
                                sourceDesc = "Dropbox / Thư mục đồng bộ";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError ??= ex.Message;
                }
            }

            if (bestManifest == null)
            {
                result.IsSuccess = false;
                result.ErrorMessage = lastError ?? "Chưa tìm thấy nguồn bản cập nhật. Nếu bạn là người quản trị, hãy bấm 'Phát hành bản mới' hoặc cấu hình Link Cloud.";
                return result;
            }

            result.IsSuccess = true;
            result.LatestManifest = bestManifest;
            result.LocalPackagePath = localPkgPath;
            result.SourceDescription = sourceDesc;
            result.IsUpdateAvailable = CompareVersions(bestManifest.Version, currentVer) > 0;

            // Detect if Admin's running .exe is newer than what was last published
            if (!result.IsUpdateAvailable && !string.IsNullOrEmpty(localUpdatesDir))
            {
                try
                {
                    string currentExe = Environment.ProcessPath ?? Path.Combine(BaseDir, "Z-Station_Studio.exe");
                    if (File.Exists(currentExe))
                    {
                        var exeTimeUtc = File.GetLastWriteTimeUtc(currentExe);
                        if (bestManifest.SourceExeLastWriteUtcTicks > 0)
                        {
                            var pubExeTimeUtc = new DateTime(bestManifest.SourceExeLastWriteUtcTicks, DateTimeKind.Utc);
                            if ((exeTimeUtc - pubExeTimeUtc).TotalSeconds > 30)
                            {
                                result.HasUnpublishedLocalBuild = true;
                            }
                        }
                        else if (DateTime.TryParse(bestManifest.PublishedAt, out var pubLocal))
                        {
                            if ((File.GetLastWriteTime(currentExe) - pubLocal).TotalMinutes > 2)
                            {
                                result.HasUnpublishedLocalBuild = true;
                            }
                        }
                    }
                }
                catch { }
            }

            return result;
        }

        public async Task<(bool Success, string Message)> DownloadAndApplyUpdateAsync(
            UpdateCheckResult checkResult,
            Action<int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (checkResult.LatestManifest == null)
            {
                return (false, "Không có thông tin phiên bản cập nhật.");
            }

            var manifest = checkResult.LatestManifest;
            string tempRoot = Path.Combine(Path.GetTempPath(), "ZStation_OTA_Update");
            string zipPath = Path.Combine(tempRoot, PackageFileName);
            string extractDir = Path.Combine(tempRoot, "extracted");

            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(extractDir);

                bool downloaded = false;

                // Option 1: Copy directly from synced Dropbox/Shared folder if available locally
                if (!string.IsNullOrEmpty(checkResult.LocalPackagePath) && File.Exists(checkResult.LocalPackagePath))
                {
                    onProgress?.Invoke(20, "Đang tải gói cập nhật từ thư mục đồng bộ...");
                    await Task.Run(() => File.Copy(checkResult.LocalPackagePath, zipPath, true), ct);
                    onProgress?.Invoke(65, "Đã tải xong gói cập nhật.");
                    downloaded = true;
                }

                // Option 2: Download via Cloud URL
                if (!downloaded)
                {
                    var config = LoadSourceConfig();
                    string pkgUrl = !string.IsNullOrWhiteSpace(manifest.PackageUrl)
                        ? NormalizeDirectDownloadUrl(manifest.PackageUrl)
                        : NormalizeDirectDownloadUrl(config.PackageUrl);

                    if (string.IsNullOrWhiteSpace(pkgUrl))
                    {
                        return (false, "Bản cập nhật chưa có đường dẫn tải (Package URL).");
                    }

                    onProgress?.Invoke(5, "Đang kết nối tải bản cập nhật...");
                    using var response = await _httpClient.GetAsync(pkgUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength ?? (manifest.PackageSizeBytes > 0 ? manifest.PackageSizeBytes : null);
                    using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int pct = (int)Math.Clamp(10 + (totalRead * 60.0 / totalBytes.Value), 10, 70);
                            double readMb = totalRead / (1024.0 * 1024.0);
                            double totalMb = totalBytes.Value / (1024.0 * 1024.0);
                            onProgress?.Invoke(pct, $"Đang tải bản cập nhật... {readMb:0.0} / {totalMb:0.0} MB ({pct}%)");
                        }
                        else
                        {
                            double readMb = totalRead / (1024.0 * 1024.0);
                            onProgress?.Invoke(45, $"Đang tải bản cập nhật... {readMb:0.0} MB");
                        }
                    }
                }

                onProgress?.Invoke(75, "Đang giải nén phiên bản mới...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, true), ct);

                string newExe = Path.Combine(extractDir, "Z-Station_Studio.exe");
                if (!File.Exists(newExe))
                {
                    var foundExe = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (foundExe == null)
                    {
                        return (false, "Gói cập nhật không hợp lệ (không tìm thấy file thực thi).");
                    }
                    newExe = foundExe;
                }

                // Write updated version marker into extractDir
                var opts = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(Path.Combine(extractDir, LocalVersionFileName), JsonSerializer.Serialize(manifest, opts), Encoding.UTF8, ct);

                onProgress?.Invoke(95, "Đang chuẩn bị tự động khởi động lại ứng dụng...");

                string currentExePath = Environment.ProcessPath ?? Path.Combine(BaseDir, "Z-Station_Studio.exe");
                string targetDir = Path.GetDirectoryName(currentExePath) ?? BaseDir.TrimEnd(Path.DirectorySeparatorChar);
                string currentExeName = Path.GetFileName(currentExePath);
                int currentPid = Environment.ProcessId;

                string scriptPath = Path.Combine(tempRoot, "apply_update.cmd");
                string scriptContent = $@"@echo off
chcp 65001 >nul
setlocal
timeout /t 1 /nobreak >nul
:waitloop
tasklist /FI ""PID eq {currentPid}"" 2>NUL | find /I ""{currentPid}"" >NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /t 1 /nobreak >nul
    goto waitloop
)
copy /Y ""{newExe}"" ""{Path.Combine(targetDir, currentExeName)}"" >nul
if exist ""{Path.Combine(targetDir, "Z-Station_Studio.exe")}"" (
    copy /Y ""{newExe}"" ""{Path.Combine(targetDir, "Z-Station_Studio.exe")}"" >nul
)
if exist ""{Path.Combine(extractDir, LocalVersionFileName)}"" (
    copy /Y ""{Path.Combine(extractDir, LocalVersionFileName)}"" ""{Path.Combine(targetDir, LocalVersionFileName)}"" >nul
)
if exist ""{Path.Combine(extractDir, ConfigFileName)}"" (
    copy /Y ""{Path.Combine(extractDir, ConfigFileName)}"" ""{Path.Combine(targetDir, ConfigFileName)}"" >nul
)
start """" ""{currentExePath}""
timeout /t 2 /nobreak >nul
rmdir /S /Q ""{tempRoot}"" >nul 2>&1
";
                await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(false), ct);

                onProgress?.Invoke(100, "Hoàn tất! Đang khởi động lại Z-Station Studio...");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{scriptPath}\"\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                return (true, "Đang khởi động lại bản mới...");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi cập nhật: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message, string PublishedVersion)> PublishCurrentBuildAsUpdateAsync(
            string newVersion,
            string releaseNotes,
            string? manifestUrl,
            string? packageUrl,
            Action<int, string>? onProgress = null)
        {
            try
            {
                var config = LoadSourceConfig();
                if (!string.IsNullOrWhiteSpace(manifestUrl)) config.ManifestUrl = manifestUrl.Trim();
                if (!string.IsNullOrWhiteSpace(packageUrl)) config.PackageUrl = packageUrl.Trim();
                if (string.IsNullOrWhiteSpace(config.ManifestUrl)) config.ManifestUrl = DefaultManifestUrl;
                if (string.IsNullOrWhiteSpace(config.PackageUrl)) config.PackageUrl = DefaultPackageUrl;
                if (string.IsNullOrWhiteSpace(config.GitRepoUrl)) config.GitRepoUrl = DefaultGitRepoUrl;

                string updatesDir = ResolveLocalUpdatesDirectory(config.SharedUpdatesFolder)
                    ?? Path.Combine(BaseDir, "Updates");
                Directory.CreateDirectory(updatesDir);

                string currentExe = Environment.ProcessPath ?? Path.Combine(BaseDir, "Z-Station_Studio.exe");
                if (!File.Exists(currentExe))
                {
                    return (false, "Không tìm thấy file thực thi hiện tại để đóng gói.", newVersion);
                }

                onProgress?.Invoke(15, "Đang chuẩn bị gói phát hành...");

                var manifest = new UpdateManifest
                {
                    Version = newVersion.Trim(),
                    PublishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes)
                        ? "Nâng cấp tính năng và tối ưu hiệu năng Z-Station Studio."
                        : releaseNotes.Trim(),
                    ManifestUrl = config.ManifestUrl,
                    PackageUrl = config.PackageUrl,
                    SourceExeLastWriteUtcTicks = File.GetLastWriteTimeUtc(currentExe).Ticks,
                    SourceExeSha256 = ComputeFileSha256(currentExe)
                };

                var opts = new JsonSerializerOptions { WriteIndented = true };
                string manifestJson = JsonSerializer.Serialize(manifest, opts);

                // Save local version marker first
                await File.WriteAllTextAsync(Path.Combine(BaseDir, LocalVersionFileName), manifestJson, Encoding.UTF8);
                SaveSourceConfig(config);

                string stageDir = Path.Combine(Path.GetTempPath(), "ZStation_Publish_Stage_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDir);

                try
                {
                    File.Copy(currentExe, Path.Combine(stageDir, "Z-Station_Studio.exe"), true);
                    await File.WriteAllTextAsync(Path.Combine(stageDir, LocalVersionFileName), manifestJson, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(stageDir, ConfigFileName), JsonSerializer.Serialize(config, opts), Encoding.UTF8);

                    string destZip = Path.Combine(updatesDir, PackageFileName);
                    if (File.Exists(destZip)) File.Delete(destZip);

                    onProgress?.Invoke(45, "Đang nén gói cập nhật (Z-Station_Studio_Update.zip)...");
                    await Task.Run(() => ZipFile.CreateFromDirectory(stageDir, destZip, CompressionLevel.Optimal, false));

                    manifest.PackageSizeBytes = new FileInfo(destZip).Length;
                    manifestJson = JsonSerializer.Serialize(manifest, opts);

                    await File.WriteAllTextAsync(Path.Combine(updatesDir, ManifestFileName), manifestJson, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(BaseDir, LocalVersionFileName), manifestJson, Encoding.UTF8);
                    try
                    {
                        var parentDir = Directory.GetParent(updatesDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            await File.WriteAllTextAsync(Path.Combine(parentDir, LocalVersionFileName), manifestJson, Encoding.UTF8);
                            string parentExe = Path.Combine(parentDir, "Z-Station_Studio.exe");
                            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(parentExe), StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Copy(currentExe, parentExe, true); } catch { }
                            }
                        }
                        string appDataInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Z-Station_Studio");
                        if (Directory.Exists(appDataInstallDir))
                        {
                            await File.WriteAllTextAsync(Path.Combine(appDataInstallDir, LocalVersionFileName), manifestJson, Encoding.UTF8);
                            string appDataExe = Path.Combine(appDataInstallDir, "Z-Station_Studio.exe");
                            if (!string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(appDataExe), StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Copy(currentExe, appDataExe, true); } catch { }
                            }
                        }
                    }
                    catch { }
                }
                finally
                {
                    try { Directory.Delete(stageDir, true); } catch { }
                }

                onProgress?.Invoke(75, "Đang đẩy bản cập nhật lên GitHub Cloud (Z-Station-Studio)...");
                var gitRes = await PushUpdatesToGitHubAsync(updatesDir, config.GitRepoUrl, manifest.Version);

                onProgress?.Invoke(100, $"Đã phát hành phiên bản v{manifest.Version} thành công!");
                if (gitRes.Success)
                {
                    return (true, $"Đã đóng gói và đẩy bản cập nhật v{manifest.Version} lên GitHub thành công!\n• Repo: {config.GitRepoUrl}", manifest.Version);
                }
                else
                {
                    return (true, $"Đã đóng gói v{manifest.Version} tại:\n{updatesDir}\n\n(Lưu ý đẩy GitHub: {gitRes.Message})", manifest.Version);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi phát hành bản cập nhật: {ex.Message}", newVersion);
            }
        }

        private async Task<(bool Success, string Message)> PushUpdatesToGitHubAsync(string updatesDir, string repoUrl, string version)
        {
            string gitStageDir = Path.Combine(Path.GetTempPath(), "ZStation_GitPush_" + Guid.NewGuid().ToString("N"));
            try
            {
                string gitExe = @"C:\Program Files\Git\cmd\git.exe";
                if (!File.Exists(gitExe)) gitExe = "git";

                Directory.CreateDirectory(gitStageDir);
                foreach (var fileName in new[] { ManifestFileName, PackageFileName, ConfigFileName })
                {
                    string src = Path.Combine(updatesDir, fileName);
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(gitStageDir, fileName), true);
                    }
                }

                async Task<(int ExitCode, string Output)> RunGitAsync(string args)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = gitExe,
                        Arguments = args,
                        WorkingDirectory = gitStageDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return (-1, "Không thể chạy git.");
                    string stdout = await proc.StandardOutput.ReadToEndAsync();
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    return (proc.ExitCode, (stdout + "\n" + stderr).Trim());
                }

                await RunGitAsync("init");
                await RunGitAsync("config user.name \"Z-Station Studio Admin\"");
                await RunGitAsync("config user.email \"admin@zstation.studio\"");
                await RunGitAsync($"remote add origin \"{repoUrl}\"");

                // Use single root commit + force push so repo only ever stores the latest 63MB zip and never bloats
                await RunGitAsync($"add \"{ManifestFileName}\" \"{PackageFileName}\" \"{ConfigFileName}\"");
                await RunGitAsync($"commit -m \"Release v{version}\"");
                await RunGitAsync("branch -M main");
                var pushRes = await RunGitAsync("push -f origin main");

                if (pushRes.ExitCode == 0)
                {
                    return (true, "Đã đồng bộ lên GitHub thành công.");
                }
                return (false, pushRes.Output);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(gitStageDir, true); } catch { }
            }
        }

        public static string IncrementPatchVersion(string version)
        {
            var parts = NormalizeVersionParts(version);
            parts[2] = parts[2] + 1;
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        }

        public static int CompareVersions(string? v1, string? v2)
        {
            var p1 = NormalizeVersionParts(v1);
            var p2 = NormalizeVersionParts(v2);
            for (int i = 0; i < 3; i++)
            {
                int cmp = p1[i].CompareTo(p2[i]);
                if (cmp != 0) return cmp;
            }
            return 0;
        }

        private static int[] NormalizeVersionParts(string? v)
        {
            int[] res = new[] { 1, 0, 0 };
            if (string.IsNullOrWhiteSpace(v)) return res;
            string clean = v.Trim().TrimStart('v', 'V');
            var tokens = clean.Split('.', '-', '_');
            for (int i = 0; i < Math.Min(3, tokens.Length); i++)
            {
                if (int.TryParse(tokens[i], out int val))
                {
                    res[i] = val;
                }
            }
            return res;
        }
    }
}
