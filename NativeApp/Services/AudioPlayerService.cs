using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ZStationStudio.Services
{
    public class AudioPlayerService : IDisposable
    {
        private WaveOutEvent? _outputDevice;
        private UniversalAudioReader? _audioFile;
        private readonly object _deviceLock = new();
        private int _loadGeneration;
        private volatile bool _isLoading;
        private volatile bool _desiredAutoPlay;
        private readonly System.Timers.Timer _positionTimer;
        private readonly ConcurrentDictionary<string, float[]> _waveformCache = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _waveformCts;
        private string _waveformCacheDir;

        // Virtual timeline state for silent video overlays
        private bool _isVirtualTimeline;
        private bool _isVirtualPlaying;
        private double _virtualDuration;
        private double _virtualOffsetSec;
        private readonly System.Diagnostics.Stopwatch _virtualStopwatch = new();

        public event Action<double, double>? PositionChanged; // currentSec, totalSec
        public event Action? PlaybackStopped;
        public event Action<string, float[], double>? WaveformReady; // path, peaks, duration

        public bool IsPlaying => (_isLoading && _desiredAutoPlay) || (_isVirtualTimeline
            ? _isVirtualPlaying
            : (_outputDevice?.PlaybackState == PlaybackState.Playing));

        public double CurrentTime => _isVirtualTimeline
            ? Math.Clamp(_virtualOffsetSec + (_isVirtualPlaying ? _virtualStopwatch.Elapsed.TotalSeconds : 0), 0, TotalDuration)
            : (_audioFile?.CurrentTime.TotalSeconds ?? 0);

        public double TotalDuration => _isVirtualTimeline
            ? (_virtualDuration > 0 ? _virtualDuration : 4.0)
            : (_audioFile?.TotalTime.TotalSeconds ?? 0);

        public double InPosition { get; set; } = 0;
        public double OutPosition { get; set; } = 0;
        public bool HasSelection => OutPosition > InPosition && OutPosition <= TotalDuration;

        private double _volume = 1.0;
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                if (_audioFile != null && !IsMuted)
                {
                    _audioFile.Volume = (float)_volume;
                }
            }
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                if (_audioFile != null)
                {
                    _audioFile.Volume = _isMuted ? 0f : (float)_volume;
                }
            }
        }

        public bool IsLooping { get; set; }
        public string? CurrentFilePath { get; private set; }
        public string WaveformCacheDirectory => _waveformCacheDir;
        public int MemoryCachedWaveformCount => _waveformCache.Count;

        public AudioPlayerService()
        {
            _waveformCacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Z-station", "WaveformCache");
            try { Directory.CreateDirectory(_waveformCacheDir); } catch { }

            _positionTimer = new System.Timers.Timer(25); // ~40 FPS smooth UI playhead
            _positionTimer.Elapsed += OnPositionTimerElapsed;
        }

        public void SetWaveformCacheDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            _waveformCacheDir = dir;
            try { Directory.CreateDirectory(_waveformCacheDir); } catch { }
        }

        public void ClearMemoryCache()
        {
            _waveformCache.Clear();
        }

        public void UpdateVirtualDuration(double duration)
        {
            if (_isVirtualTimeline && duration > 0)
            {
                _virtualDuration = duration;
            }
        }

        public async Task LoadFileAsync(string filePath, bool autoPlay = false, double fallbackDuration = 0)
        {
            if (!File.Exists(filePath)) return;

            int myGen = Interlocked.Increment(ref _loadGeneration);
            _desiredAutoPlay = autoPlay;
            _isLoading = true;

            StopDeviceAndReaderOnly();
            CurrentFilePath = filePath;
            InPosition = 0;
            OutPosition = 0;

            bool isVideo = DataManager.VideoExtensions.Contains(Path.GetExtension(filePath));
            bool isCloudPlaceholder = isVideo && VideoPreviewService.IsCloudPlaceholder(filePath);

            try
            {
                if (!isCloudPlaceholder)
                {
                    UniversalAudioReader? newReader = null;
                    WaveOutEvent? newDevice = null;
                    try
                    {
                        float initialVol = IsMuted ? 0f : (float)Volume;
                        bool loadedOk = await Task.Run(() =>
                        {
                            if (myGen != Volatile.Read(ref _loadGeneration)) return false;
                            newReader = new UniversalAudioReader(filePath)
                            {
                                Volume = initialVol
                            };
                            if (myGen != Volatile.Read(ref _loadGeneration))
                            {
                                newReader.Dispose();
                                newReader = null;
                                return false;
                            }
                            newDevice = new WaveOutEvent
                            {
                                DesiredLatency = 100,
                                NumberOfBuffers = 3
                            };
                            newDevice.Init(newReader);
                            return true;
                        });

                        if (loadedOk && newReader != null && newDevice != null)
                        {
                            if (myGen != Volatile.Read(ref _loadGeneration))
                            {
                                DisposeDetachedInBackground(newDevice, newReader);
                                return;
                            }

                            newDevice.PlaybackStopped += OnDevicePlaybackStopped;

                            lock (_deviceLock)
                            {
                                if (myGen != Volatile.Read(ref _loadGeneration))
                                {
                                    newDevice.PlaybackStopped -= OnDevicePlaybackStopped;
                                    DisposeDetachedInBackground(newDevice, newReader);
                                    return;
                                }

                                DisposeCurrentDeviceLocked();
                                _audioFile = newReader;
                                _outputDevice = newDevice;
                                _isVirtualTimeline = false;
                            }

                            double duration = newReader.TotalTime.TotalSeconds;
                            PositionChanged?.Invoke(0, duration);

                            // Load or extract waveform peaks asynchronously
                            ExtractWaveformAsync(filePath, duration);

                            _isLoading = false;
                            if (_desiredAutoPlay && myGen == Volatile.Read(ref _loadGeneration))
                            {
                                Play();
                            }
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        DisposeDetachedInBackground(newDevice, newReader);
                        System.Diagnostics.Debug.WriteLine($"Audio load info (using virtual timeline if video): {ex.Message}");
                    }
                }

                if (myGen != Volatile.Read(ref _loadGeneration))
                {
                    return;
                }

                // Fallback to Virtual Timeline for silent Video Overlays or Cloud video placeholders
                if (isVideo)
                {
                    lock (_deviceLock)
                    {
                        DisposeCurrentDeviceLocked();
                        _isVirtualTimeline = true;
                        _isVirtualPlaying = false;
                        _virtualOffsetSec = 0;
                        _virtualStopwatch.Reset();
                        _virtualDuration = fallbackDuration > 0 ? fallbackDuration : 4.0;
                    }

                    PositionChanged?.Invoke(0, TotalDuration);

                    _isLoading = false;
                    if (_desiredAutoPlay && myGen == Volatile.Read(ref _loadGeneration))
                    {
                        Play();
                    }
                }
            }
            finally
            {
                if (myGen == Volatile.Read(ref _loadGeneration))
                {
                    _isLoading = false;
                }
            }
        }

        public void Play()
        {
            _desiredAutoPlay = true;
            if (_isLoading) return;

            if (_isVirtualTimeline)
            {
                if (HasSelection && (CurrentTime < InPosition || CurrentTime >= OutPosition))
                {
                    Seek(InPosition);
                }
                else if (CurrentTime >= TotalDuration - 0.05)
                {
                    Seek(HasSelection ? InPosition : 0);
                }

                _isVirtualPlaying = true;
                _virtualStopwatch.Restart();
                _positionTimer.Start();
                return;
            }

            lock (_deviceLock)
            {
                if (_outputDevice == null || _audioFile == null) return;

                if (HasSelection && (CurrentTime < InPosition || CurrentTime >= OutPosition))
                {
                    Seek(InPosition);
                }
                else if (CurrentTime >= TotalDuration - 0.05)
                {
                    Seek(HasSelection ? InPosition : 0);
                }

                _outputDevice.Play();
                _positionTimer.Start();
            }
        }

        public void Pause()
        {
            _desiredAutoPlay = false;
            _positionTimer.Stop();

            if (_isVirtualPlaying)
            {
                _virtualOffsetSec = CurrentTime;
                _virtualStopwatch.Reset();
                _isVirtualPlaying = false;
            }

            lock (_deviceLock)
            {
                try
                {
                    _outputDevice?.Pause();
                }
                catch { }
            }
        }

        private static void DisposeDetachedInBackground(WaveOutEvent? dev, UniversalAudioReader? reader)
        {
            if (dev == null && reader == null) return;
            _ = Task.Run(() =>
            {
                if (dev != null)
                {
                    try { dev.Stop(); } catch { }
                    try { dev.Dispose(); } catch { }
                }
                if (reader != null)
                {
                    try { reader.Dispose(); } catch { }
                }
            });
        }

        private void DisposeCurrentDeviceLocked()
        {
            var oldDevice = _outputDevice;
            var oldFile = _audioFile;
            _outputDevice = null;
            _audioFile = null;

            if (oldDevice != null)
            {
                try { oldDevice.PlaybackStopped -= OnDevicePlaybackStopped; } catch { }
            }

            DisposeDetachedInBackground(oldDevice, oldFile);
        }

        private void StopDeviceAndReaderOnly()
        {
            _positionTimer.Stop();
            _isVirtualPlaying = false;
            _virtualOffsetSec = 0;
            _virtualStopwatch.Reset();
            _isVirtualTimeline = false;

            lock (_deviceLock)
            {
                DisposeCurrentDeviceLocked();
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _loadGeneration);
            _desiredAutoPlay = false;
            _isLoading = false;
            StopDeviceAndReaderOnly();
        }

        public void TogglePlay()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        public void Seek(double seconds)
        {
            if (_isVirtualTimeline)
            {
                seconds = Math.Clamp(seconds, 0, TotalDuration);
                _virtualOffsetSec = seconds;
                if (_isVirtualPlaying)
                {
                    _virtualStopwatch.Restart();
                }
                PositionChanged?.Invoke(seconds, TotalDuration);
                return;
            }

            var file = _audioFile;
            if (file == null) return;
            seconds = Math.Clamp(seconds, 0, TotalDuration);
            try
            {
                file.CurrentTime = TimeSpan.FromSeconds(seconds);
                PositionChanged?.Invoke(seconds, TotalDuration);
            }
            catch { }
        }

        private void OnPositionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_isVirtualTimeline)
            {
                if (!_isVirtualPlaying) return;

                double cur = CurrentTime;
                double total = TotalDuration;
                double endBound = HasSelection ? OutPosition : total;

                if (cur >= endBound)
                {
                    double startBound = HasSelection ? InPosition : 0;
                    if (IsLooping)
                    {
                        Seek(startBound);
                        return;
                    }
                    else
                    {
                        Pause();
                        Seek(startBound);
                        PlaybackStopped?.Invoke();
                        return;
                    }
                }

                PositionChanged?.Invoke(cur, total);
                return;
            }

            var file = _audioFile;
            if (file == null || !IsPlaying) return;

            double audioCur;
            double audioTotal;
            try
            {
                audioCur = file.CurrentTime.TotalSeconds;
                audioTotal = file.TotalTime.TotalSeconds;
            }
            catch
            {
                return;
            }

            // Handle In/Out bounds
            if (HasSelection && audioCur >= OutPosition)
            {
                if (IsLooping)
                {
                    Seek(InPosition);
                    return;
                }
                else
                {
                    Pause();
                    Seek(InPosition);
                    return;
                }
            }

            PositionChanged?.Invoke(audioCur, audioTotal);
        }

        private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _positionTimer.Stop();
            if (IsLooping && _audioFile != null)
            {
                double start = HasSelection ? InPosition : 0;
                Seek(start);
                Play();
            }
            else
            {
                PlaybackStopped?.Invoke();
            }
        }

        public string GetWaveformCacheFilePath(string filePath)
        {
            string norm = DataManager.NormalizePath(filePath);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
            string hex = Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
            return Path.Combine(_waveformCacheDir, $"{hex}.wf");
        }

        public bool HasCachedWaveformOnDisk(string filePath)
        {
            try
            {
                return File.Exists(GetWaveformCacheFilePath(filePath));
            }
            catch
            {
                return false;
            }
        }

        private bool TryLoadWaveformFromDisk(string filePath, out float[] peaks, out double cachedDuration)
        {
            peaks = Array.Empty<float>();
            cachedDuration = 0;
            try
            {
                string wfFile = GetWaveformCacheFilePath(filePath);
                if (!File.Exists(wfFile)) return false;

                using var fs = new FileStream(wfFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                cachedDuration = br.ReadDouble();
                int count = br.ReadInt32();
                if (count <= 0 || count > 4096) return false;

                peaks = new float[count];
                for (int i = 0; i < count; i++)
                {
                    peaks[i] = br.ReadSingle();
                }

                // Invalidate corrupt half-empty cache files (legacy bug where stereo files had 2nd half as 0)
                if (count >= 400)
                {
                    int half = count / 2;
                    bool secondHalfAllZero = true;
                    for (int i = half; i < count; i++)
                    {
                        if (peaks[i] > 0.0001f)
                        {
                            secondHalfAllZero = false;
                            break;
                        }
                    }
                    if (secondHalfAllZero)
                    {
                        peaks = Array.Empty<float>();
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SaveWaveformToDisk(string filePath, float[] peaks, double duration)
        {
            try
            {
                Directory.CreateDirectory(_waveformCacheDir);
                string wfFile = GetWaveformCacheFilePath(filePath);
                using var fs = new FileStream(wfFile, FileMode.Create, FileAccess.Write, FileShare.None);
                using var bw = new BinaryWriter(fs);
                bw.Write(duration);
                bw.Write(peaks.Length);
                for (int i = 0; i < peaks.Length; i++)
                {
                    bw.Write(peaks[i]);
                }
            }
            catch
            {
                // Ignore disk write failure
            }
        }

        private void ExtractWaveformAsync(string filePath, double duration)
        {
            if (_waveformCache.TryGetValue(filePath, out var cachedPeaks))
            {
                WaveformReady?.Invoke(filePath, cachedPeaks, duration);
                return;
            }

            if (TryLoadWaveformFromDisk(filePath, out var diskPeaks, out var diskDur))
            {
                _waveformCache[filePath] = diskPeaks;
                WaveformReady?.Invoke(filePath, diskPeaks, duration > 0 ? duration : diskDur);
                return;
            }

            _waveformCts?.Cancel();
            _waveformCts = new CancellationTokenSource();
            var token = _waveformCts.Token;

            Task.Run(() =>
            {
                try
                {
                    const int targetBuckets = 600;
                    using var reader = new UniversalAudioReader(filePath);
                    int channels = Math.Max(1, reader.WaveFormat.Channels);
                    long totalFloats = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                    long floatsPerBucket = Math.Max(channels, totalFloats / targetBuckets);
                    floatsPerBucket -= (floatsPerBucket % channels);
                    if (floatsPerBucket <= 0) floatsPerBucket = channels;

                    float[] peaks = new float[targetBuckets];
                    float[] buffer = new float[floatsPerBucket];

                    for (int b = 0; b < targetBuckets; b++)
                    {
                        if (token.IsCancellationRequested) return;

                        int read = reader.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        float max = 0;
                        for (int i = 0; i < read; i++)
                        {
                            float abs = Math.Abs(buffer[i]);
                            if (abs > max) max = abs;
                        }
                        peaks[b] = Math.Min(1.0f, max);
                    }

                    double actualDur = duration > 0 ? duration : reader.TotalTime.TotalSeconds;
                    _waveformCache[filePath] = peaks;
                    SaveWaveformToDisk(filePath, peaks, actualDur);

                    if (!token.IsCancellationRequested)
                    {
                        WaveformReady?.Invoke(filePath, peaks, actualDur);
                    }
                }
                catch
                {
                    // Fallback empty waveform if file decode fails
                }
            }, token);
        }

        public Task<(bool Success, double Duration)> PrebuildWaveformForFileAsync(string filePath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                if (ct.IsCancellationRequested || !File.Exists(filePath))
                    return (false, 0.0);

                if (TryLoadWaveformFromDisk(filePath, out var existingPeaks, out var existingDur))
                {
                    _waveformCache.TryAdd(filePath, existingPeaks);
                    return (true, existingDur);
                }

                try
                {
                    const int targetBuckets = 600;
                    using var reader = new UniversalAudioReader(filePath);
                    double dur = reader.TotalTime.TotalSeconds;
                    int channels = Math.Max(1, reader.WaveFormat.Channels);
                    long totalFloats = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                    long floatsPerBucket = Math.Max(channels, totalFloats / targetBuckets);
                    floatsPerBucket -= (floatsPerBucket % channels);
                    if (floatsPerBucket <= 0) floatsPerBucket = channels;

                    float[] peaks = new float[targetBuckets];
                    float[] buffer = new float[floatsPerBucket];

                    for (int b = 0; b < targetBuckets; b++)
                    {
                        if (ct.IsCancellationRequested) return (false, 0.0);

                        int read = reader.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        float max = 0;
                        for (int i = 0; i < read; i++)
                        {
                            float abs = Math.Abs(buffer[i]);
                            if (abs > max) max = abs;
                        }
                        peaks[b] = Math.Min(1.0f, max);
                    }

                    _waveformCache[filePath] = peaks;
                    SaveWaveformToDisk(filePath, peaks, dur);
                    return (true, dur);
                }
                catch
                {
                    return (false, 0.0);
                }
            }, ct);
        }

        public string? ExportTrimmedWav(string sourcePath, double inSec, double outSec)
        {
            if (!File.Exists(sourcePath) || inSec >= outSec) return null;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "ZStationTrim");
                Directory.CreateDirectory(tempDir);

                string baseName = Path.GetFileNameWithoutExtension(sourcePath);
                string destPath = Path.Combine(tempDir, $"{baseName}_trim_{inSec:0.00}-{outSec:0.00}.wav");

                if (File.Exists(destPath)) return destPath;

                using var reader = new UniversalAudioReader(sourcePath);
                reader.CurrentTime = TimeSpan.FromSeconds(inSec);

                int bytesPerSecond = reader.WaveFormat.AverageBytesPerSecond;
                long bytesToRead = (long)((outSec - inSec) * bytesPerSecond);

                using var writer = new WaveFileWriter(destPath, reader.WaveFormat);
                byte[] buffer = new byte[16384];
                long bytesWritten = 0;

                while (bytesWritten < bytesToRead)
                {
                    int toRead = (int)Math.Min(buffer.Length, bytesToRead - bytesWritten);
                    toRead -= (toRead % reader.WaveFormat.BlockAlign);
                    if (toRead <= 0) break;

                    int read = reader.Read(buffer, 0, toRead);
                    if (read <= 0) break;

                    writer.Write(buffer, 0, read);
                    bytesWritten += read;
                }

                return destPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Export trimmed error: {ex.Message}");
                return sourcePath; // Fallback to original
            }
        }

        public string EnsureDragCompatibleFile(string sourcePath)
        {
            if (!UniversalAudioReader.NeedsWavConversionForExternalDrag(sourcePath))
            {
                return sourcePath;
            }

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "ZStationTrim");
                Directory.CreateDirectory(tempDir);

                string baseName = Path.GetFileNameWithoutExtension(sourcePath);
                string destPath = Path.Combine(tempDir, $"{baseName}.wav");
                if (File.Exists(destPath)) return destPath;

                using var reader = new UniversalAudioReader(sourcePath);
                WaveFileWriter.CreateWaveFile16(destPath, reader);
                return destPath;
            }
            catch
            {
                return sourcePath;
            }
        }

        public void Dispose()
        {
            Stop();
            _positionTimer.Dispose();
            _waveformCts?.Dispose();
        }
    }
}
