using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ZStationStudio.Models
{
    public class SfxItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string BaseName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }

        private static readonly SolidColorBrush _videoBadgeBg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x18, 0x2B, 0x3A)));
        private static readonly SolidColorBrush _videoBadgeBorder = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7)));
        private static readonly SolidColorBrush _videoBadgeFg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));

        private static readonly SolidColorBrush _imageBadgeBg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x14, 0x28, 0x1E)));
        private static readonly SolidColorBrush _imageBadgeBorder = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)));
        private static readonly SolidColorBrush _imageBadgeFg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));

        private static readonly SolidColorBrush _designBadgeBg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x28, 0x1A, 0x36)));
        private static readonly SolidColorBrush _designBadgeBorder = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x7E, 0x22, 0xCE)));
        private static readonly SolidColorBrush _designBadgeFg = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC)));

        private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private bool _isVideo;
        public bool IsVideo
        {
            get => _isVideo;
            set
            {
                if (_isVideo != value)
                {
                    _isVideo = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VideoBadgeVisibility));
                    OnPropertyChanged(nameof(AudioIconVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeText));
                    OnPropertyChanged(nameof(MediaTypeBadgeBackground));
                    OnPropertyChanged(nameof(MediaTypeBadgeBorder));
                    OnPropertyChanged(nameof(MediaTypeBadgeForeground));
                    OnPropertyChanged(nameof(RowActionButtonToolTip));
                    OnPropertyChanged(nameof(PlayPathData));
                }
            }
        }

        private bool _isImage;
        public bool IsImage
        {
            get => _isImage;
            set
            {
                if (_isImage != value)
                {
                    _isImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ImageBadgeVisibility));
                    OnPropertyChanged(nameof(ImageIconVisibility));
                    OnPropertyChanged(nameof(AudioIconVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeText));
                    OnPropertyChanged(nameof(MediaTypeBadgeBackground));
                    OnPropertyChanged(nameof(MediaTypeBadgeBorder));
                    OnPropertyChanged(nameof(MediaTypeBadgeForeground));
                    OnPropertyChanged(nameof(RowActionButtonToolTip));
                    OnPropertyChanged(nameof(PlayPathData));
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        private bool _isDesign;
        public bool IsDesign
        {
            get => _isDesign;
            set
            {
                if (_isDesign != value)
                {
                    _isDesign = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DesignBadgeVisibility));
                    OnPropertyChanged(nameof(DesignIconVisibility));
                    OnPropertyChanged(nameof(AudioIconVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeVisibility));
                    OnPropertyChanged(nameof(MediaTypeBadgeText));
                    OnPropertyChanged(nameof(MediaTypeBadgeBackground));
                    OnPropertyChanged(nameof(MediaTypeBadgeBorder));
                    OnPropertyChanged(nameof(MediaTypeBadgeForeground));
                    OnPropertyChanged(nameof(RowActionButtonToolTip));
                    OnPropertyChanged(nameof(PlayPathData));
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        public bool IsAudio => !IsVideo && !IsImage && !IsDesign;

        public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageBadgeVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DesignBadgeVisibility => IsDesign ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AudioIconVisibility => IsAudio ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageIconVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DesignIconVisibility => IsDesign ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MediaTypeBadgeVisibility => (IsVideo || IsImage || IsDesign) ? Visibility.Visible : Visibility.Collapsed;
        public string MediaTypeBadgeText => IsVideo ? "🎬 VIDEO" : (IsImage ? "🖼 IMAGE" : (IsDesign ? "🎨 DESIGN" : string.Empty));
        public Brush MediaTypeBadgeBackground => IsVideo
            ? _videoBadgeBg
            : (IsImage ? _imageBadgeBg : (IsDesign ? _designBadgeBg : Brushes.Transparent));
        public Brush MediaTypeBadgeBorder => IsVideo
            ? _videoBadgeBorder
            : (IsImage ? _imageBadgeBorder : (IsDesign ? _designBadgeBorder : Brushes.Transparent));
        public Brush MediaTypeBadgeForeground => IsVideo
            ? _videoBadgeFg
            : (IsImage ? _imageBadgeFg : (IsDesign ? _designBadgeFg : Brushes.Transparent));

        private bool _isCloudOnly;
        public bool IsCloudOnly
        {
            get => _isCloudOnly;
            set
            {
                if (_isCloudOnly != value)
                {
                    _isCloudOnly = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CloudBadgeVisibility));
                }
            }
        }

        public Visibility CloudBadgeVisibility => IsCloudOnly ? Visibility.Visible : Visibility.Collapsed;

        private string _resolutionBadge = string.Empty;
        public string ResolutionBadge
        {
            get => string.IsNullOrEmpty(_resolutionBadge) ? Extension : $"{_resolutionBadge} • {Extension}";
            set
            {
                if (_resolutionBadge != value)
                {
                    _resolutionBadge = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        private ImageSource? _thumbnailSource;
        public ImageSource? ThumbnailSource
        {
            get => _thumbnailSource;
            set
            {
                if (!ReferenceEquals(_thumbnailSource, value))
                {
                    _thumbnailSource = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayImageSource));
                    OnPropertyChanged(nameof(HasThumbnailVisibility));
                    OnPropertyChanged(nameof(PlaceholderVisibility));
                }
            }
        }

        private ImageSource[]? _scrubFrames;
        public ImageSource[]? ScrubFrames
        {
            get => _scrubFrames;
            set
            {
                _scrubFrames = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayImageSource));
                OnPropertyChanged(nameof(HasThumbnailVisibility));
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }

        private bool _isHoverScrubbing;
        public bool IsHoverScrubbing
        {
            get => _isHoverScrubbing;
            set
            {
                if (_isHoverScrubbing != value)
                {
                    _isHoverScrubbing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ScrubOverlayVisibility));
                    OnPropertyChanged(nameof(DisplayImageSource));
                }
            }
        }

        private double _scrubRatio;
        public double ScrubRatio
        {
            get => _scrubRatio;
            set
            {
                double clamped = Math.Clamp(value, 0.0, 1.0);
                if (Math.Abs(_scrubRatio - clamped) > 0.0005)
                {
                    _scrubRatio = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayImageSource));
                    OnPropertyChanged(nameof(ScrubTimeText));
                }
            }
        }

        private Thickness _scrubLineMargin = new(0);
        public Thickness ScrubLineMargin
        {
            get => _scrubLineMargin;
            set
            {
                if (_scrubLineMargin != value)
                {
                    _scrubLineMargin = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _scrubProgressWidth;
        public double ScrubProgressWidth
        {
            get => _scrubProgressWidth;
            set
            {
                if (Math.Abs(_scrubProgressWidth - value) > 0.5)
                {
                    _scrubProgressWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        public Visibility ScrubOverlayVisibility => IsHoverScrubbing ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? DisplayImageSource
        {
            get
            {
                if (IsHoverScrubbing && _scrubFrames != null && _scrubFrames.Length > 0)
                {
                    int idx = Math.Clamp((int)(_scrubRatio * _scrubFrames.Length), 0, _scrubFrames.Length - 1);
                    return _scrubFrames[idx] ?? _thumbnailSource;
                }
                return _thumbnailSource;
            }
        }

        public Visibility HasThumbnailVisibility => DisplayImageSource != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => DisplayImageSource == null ? Visibility.Visible : Visibility.Collapsed;

        public string ScrubTimeText
        {
            get
            {
                if (Duration > 0)
                {
                    var cur = TimeSpan.FromSeconds(Duration * _scrubRatio);
                    var tot = TimeSpan.FromSeconds(Duration);
                    return $"{cur:mm\\:ss\\.f} / {tot:mm\\:ss\\.f}";
                }
                return $"{(int)(_scrubRatio * 100)}%";
            }
        }

        public void UpdateScrub(double ratio, double pixelWidth)
        {
            double clamped = Math.Clamp(ratio, 0.0, 1.0);
            _scrubRatio = clamped;
            _isHoverScrubbing = true;
            double x = Math.Clamp(clamped * pixelWidth, 0, Math.Max(0, pixelWidth - 2));
            _scrubLineMargin = new Thickness(x, 0, 0, 0);
            _scrubProgressWidth = x;

            OnPropertyChanged(nameof(IsHoverScrubbing));
            OnPropertyChanged(nameof(ScrubOverlayVisibility));
            OnPropertyChanged(nameof(ScrubRatio));
            OnPropertyChanged(nameof(ScrubLineMargin));
            OnPropertyChanged(nameof(ScrubProgressWidth));
            OnPropertyChanged(nameof(ScrubTimeText));
            OnPropertyChanged(nameof(DisplayImageSource));
            OnPropertyChanged(nameof(HasThumbnailVisibility));
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }

        public void StopScrub()
        {
            if (_isHoverScrubbing)
            {
                _isHoverScrubbing = false;
                OnPropertyChanged(nameof(IsHoverScrubbing));
                OnPropertyChanged(nameof(ScrubOverlayVisibility));
                OnPropertyChanged(nameof(DisplayImageSource));
                OnPropertyChanged(nameof(HasThumbnailVisibility));
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }

        private double _duration;
        public double Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DurationText));
                    OnPropertyChanged(nameof(ScrubTimeText));
                }
            }
        }

        public string DurationText
        {
            get
            {
                if (IsImage || IsDesign)
                {
                    return !string.IsNullOrEmpty(_resolutionBadge) ? _resolutionBadge : "--";
                }
                if (Duration <= 0) return "--:--";
                var ts = TimeSpan.FromSeconds(Duration);
                return ts.TotalHours >= 1
                    ? ts.ToString(@"hh\:mm\:ss")
                    : ts.ToString(@"mm\:ss\.f");
            }
        }

        public string FileSizeText
        {
            get
            {
                if (FileSize <= 0) return string.Empty;
                double mb = FileSize / (1024.0 * 1024.0);
                if (mb >= 1.0) return $"{mb:0.1} MB";
                double kb = FileSize / 1024.0;
                return $"{kb:0} KB";
            }
        }

        private List<string> _tags = new();
        private HashSet<string> _normalizedTags = new(StringComparer.Ordinal);
        public HashSet<string> NormalizedTags => _normalizedTags;

        public List<string> Tags
        {
            get => _tags;
            set
            {
                _tags = value ?? new List<string>();
                var normSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in _tags)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        normSet.Add(Services.SearchNormalizer.Normalize(t));
                    }
                }
                _normalizedTags = normSet;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TagsDisplay));
                OnPropertyChanged(nameof(HasTags));
            }
        }

        public string TagsDisplay => Tags.Count > 0 ? string.Join(", ", Tags) : string.Empty;
        public bool HasTags => Tags.Count > 0;

        public string SearchNormalized { get; set; } = string.Empty;
        public string FilePathNorm { get; set; } = string.Empty;

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PlayIcon));
                    OnPropertyChanged(nameof(PlayPathData));
                }
            }
        }

        public string PlayIcon => IsPlaying ? "⏸" : "▶";
        public string RowActionButtonToolTip => (IsImage || IsDesign) ? "Xem trước hình ảnh / thiết kế" : "Phát / Dừng";

        public string PlayPathData
        {
            get
            {
                if (IsPlaying)
                {
                    return "M 15,3 h 3 a 1,1 0 0,1 1,1 v 16 a 1,1 0 0,1 -1,1 h -3 a 1,1 0 0,1 -1,-1 v -16 a 1,1 0 0,1 1,-1 z M 6,3 h 3 a 1,1 0 0,1 1,1 v 16 a 1,1 0 0,1 -1,1 h -3 a 1,1 0 0,1 -1,-1 v -16 a 1,1 0 0,1 1,-1 z";
                }
                if (IsImage || IsDesign)
                {
                    return "M 1,12 c 3,-7 8,-8 11,-8 s 8,1 11,8 c -3,7 -8,8 -11,8 s -8,-1 -11,-8 z M 12,8 a 4,4 0 1,0 0,8 a 4,4 0 1,0 0,-8 z";
                }
                return "M 5,5 a 2,2 0 0,1 3.008,-1.728 l 11.997,6.998 a 2,2 0 0,1 0.003,3.458 l -12,7 A 2,2 0 0,1 5,19 z";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
