using System;
using System.IO;
using System.Text;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ZStationStudio.Services
{
    /// <summary>
    /// Universal audio reader that seamlessly decodes standard WAV/MP3/AIFF (via AudioFileReader),
    /// MediaFoundation streams (ID3v2.4 MP3, AAC, M4A, MP4, MOV, WAV with custom chunks),
    /// Ogg Opus (even when misnamed as .mp3), Ogg Vorbis, and files with leading downloader text prefixes.
    /// </summary>
    public class UniversalAudioReader : WaveStream, ISampleProvider
    {
        private WaveStream _readerStream;
        private readonly SampleChannel _sampleChannel;
        private readonly int _destBytesPerSample;
        private readonly int _sourceBytesPerSample;
        private readonly long _length;
        private readonly object _lockObject = new();
        private Stream? _ownedBaseStream;
        private long _cachedPosition;

        private static readonly object _opusCacheLock = new();
        private static string? _lastOpusFile;
        private static byte[]? _lastOpusPcm;
        private static int _lastOpusChannels = 2;

        public UniversalAudioReader(string fileName)
        {
            _readerStream = CreateReaderStream(fileName, out _ownedBaseStream);
            _sourceBytesPerSample = (_readerStream.WaveFormat.BitsPerSample / 8) * _readerStream.WaveFormat.Channels;
            _sampleChannel = new SampleChannel(_readerStream, false);
            _destBytesPerSample = 4 * _sampleChannel.WaveFormat.Channels;
            _length = SourceToDest(_readerStream.Length);
            _cachedPosition = 0;
        }

        private static WaveStream CreateReaderStream(string fileName, out Stream? ownedStream)
        {
            ownedStream = null;

            // 1. Inspect the first 512 bytes for Ogg Opus / Ogg Vorbis or leading text garbage before ID3/OggS
            byte[] header = new byte[512];
            int bytesRead = 0;
            try
            {
                using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bytesRead = fs.Read(header, 0, header.Length);
            }
            catch { }

            if (bytesRead >= 36)
            {
                int oggOffset = FindAsciiPattern(header, bytesRead, "OggS");
                if (oggOffset >= 0 && oggOffset + 36 <= bytesRead)
                {
                    string codecTag = Encoding.ASCII.GetString(header, oggOffset + 28, Math.Min(8, bytesRead - (oggOffset + 28)));
                    if (codecTag.StartsWith("OpusHead", StringComparison.Ordinal))
                    {
                        return DecodeOggOpusToWaveStream(fileName, oggOffset, header, bytesRead, out ownedStream);
                    }
                    if (codecTag.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
                    {
                        var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (oggOffset > 0) fs.Position = oggOffset;
                        ownedStream = fs;
                        return new VorbisWaveReader(fs);
                    }
                }
            }

            // 2. If extension is .ogg or .opus, try Vorbis or Opus
            string ext = Path.GetExtension(fileName);
            if (ext.Equals(".opus", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeOggOpusToWaveStream(fileName, 0, header, bytesRead, out ownedStream);
            }
            if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new VorbisWaveReader(fileName);
                }
                catch
                {
                    return DecodeOggOpusToWaveStream(fileName, 0, header, bytesRead, out ownedStream);
                }
            }

            // 3. Try standard NAudio AudioFileReader fast-path for clean WAV/AIFF/MP3
            // Skip Mp3FileReader if the file has leading text garbage before ID3 (e.g., downloader text prefix)
            bool hasLeadingGarbageBeforeId3 = false;
            if (bytesRead >= 10 && !(header[0] == 'I' && header[1] == 'D' && header[2] == '3'))
            {
                int id3Pos = FindAsciiPattern(header, bytesRead, "ID3");
                if (id3Pos > 0)
                {
                    hasLeadingGarbageBeforeId3 = true;
                }
            }

            if (!hasLeadingGarbageBeforeId3)
            {
                try
                {
                    if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        WaveStream ws = new WaveFileReader(fileName);
                        if (ws.WaveFormat.Encoding != WaveFormatEncoding.Pcm &&
                            ws.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                        {
                            ws = WaveFormatConversionStream.CreatePcmStream(ws);
                            ws = new BlockAlignReductionStream(ws);
                        }
                        return ws;
                    }
                    if (ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase))
                    {
                        return new AiffFileReader(fileName);
                    }
                    if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        return new Mp3FileReader(fileName);
                    }
                }
                catch
                {
                    // Fall through to MediaFoundationReader below!
                }
            }

            // 4. Fallback to Windows MediaFoundationReader (handles ID3v2.4 MP3, M4A, AAC, MP4, MOV, and MP3 with prefixes)
            return new MediaFoundationReader(fileName);
        }

        private static WaveStream DecodeOggOpusToWaveStream(string fileName, int oggOffset, byte[] header, int headerLen, out Stream? ownedStream)
        {
            lock (_opusCacheLock)
            {
                if (_lastOpusPcm != null && string.Equals(_lastOpusFile, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var cachedStream = new MemoryStream(_lastOpusPcm, writable: false);
                    ownedStream = cachedStream;
                    return new RawSourceWaveStream(cachedStream, new WaveFormat(48000, 16, _lastOpusChannels));
                }
            }

            int channels = 2;
            if (oggOffset + 38 <= headerLen)
            {
                int ch = header[oggOffset + 37];
                if (ch >= 1 && ch <= 2) channels = ch;
            }

            using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (oggOffset > 0)
            {
                fs.Position = oggOffset;
            }

            var decoder = OpusDecoder.Create(48000, channels);
            var oggIn = new OpusOggReadStream(decoder, fs);
            using var pcmStream = new MemoryStream(capacity: (int)Math.Min(fs.Length * 6, 64 * 1024 * 1024));

            byte[]? byteBuf = null;
            while (oggIn.HasNextPacket)
            {
                short[]? packet = oggIn.DecodeNextPacket();
                if (packet != null && packet.Length > 0)
                {
                    int byteCount = packet.Length * 2;
                    if (byteBuf == null || byteBuf.Length < byteCount)
                    {
                        byteBuf = new byte[byteCount];
                    }
                    Buffer.BlockCopy(packet, 0, byteBuf, 0, byteCount);
                    pcmStream.Write(byteBuf, 0, byteCount);
                }
            }

            byte[] pcmBytes = pcmStream.ToArray();
            lock (_opusCacheLock)
            {
                _lastOpusFile = fileName;
                _lastOpusPcm = pcmBytes;
                _lastOpusChannels = channels;
            }

            var memStream = new MemoryStream(pcmBytes, writable: false);
            ownedStream = memStream;
            return new RawSourceWaveStream(memStream, new WaveFormat(48000, 16, channels));
        }

        private static int FindAsciiPattern(byte[] buffer, int length, string pattern)
        {
            int patLen = pattern.Length;
            for (int i = 0; i <= length - patLen; i++)
            {
                bool match = true;
                for (int j = 0; j < patLen; j++)
                {
                    if (buffer[i + j] != (byte)pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        public static bool NeedsWavConversionForExternalDrag(string fileName)
        {
            try
            {
                if (!File.Exists(fileName)) return false;
                byte[] header = new byte[256];
                using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header, 0, header.Length);
                if (read < 12) return false;

                // Misnamed Ogg Opus / Vorbis inside .mp3
                if (FindAsciiPattern(header, read, "OggS") >= 0 &&
                    !Path.GetExtension(fileName).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Leading text garbage before ID3 tag
                if (!(header[0] == 'I' && header[1] == 'D' && header[2] == '3') &&
                    FindAsciiPattern(header, read, "ID3") > 0)
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        public override WaveFormat WaveFormat => _sampleChannel.WaveFormat;

        public override long Length => _length;

        public override long Position
        {
            get => System.Threading.Interlocked.Read(ref _cachedPosition);
            set
            {
                lock (_lockObject)
                {
                    long srcPos = Math.Clamp(DestToSource(value), 0, Math.Max(0, _readerStream.Length));
                    _readerStream.Position = srcPos;
                    System.Threading.Interlocked.Exchange(ref _cachedPosition, SourceToDest(_readerStream.Position));
                }
            }
        }

        public float Volume
        {
            get => _sampleChannel.Volume;
            set => _sampleChannel.Volume = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var waveBuffer = new WaveBuffer(buffer);
            int samplesRequired = count / 4;
            int samplesRead = Read(waveBuffer.FloatBuffer, offset / 4, samplesRequired);
            return samplesRead * 4;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lockObject)
            {
                int read = _sampleChannel.Read(buffer, offset, count);
                System.Threading.Interlocked.Exchange(ref _cachedPosition, SourceToDest(_readerStream.Position));
                return read;
            }
        }

        private long SourceToDest(long sourceBytes)
        {
            if (_sourceBytesPerSample <= 0) return 0;
            return _destBytesPerSample * (sourceBytes / _sourceBytesPerSample);
        }

        private long DestToSource(long destBytes)
        {
            if (_destBytesPerSample <= 0) return 0;
            return _sourceBytesPerSample * (destBytes / _destBytesPerSample);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _readerStream?.Dispose(); } catch { }
                try { _ownedBaseStream?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
