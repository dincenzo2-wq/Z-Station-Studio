using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ZStationStudio.Models;
using ZStationStudio.Services;
using ZStationStudio.Controls;

namespace ZStationStudio
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkModeTitleBar();
        }

        private void ApplyDarkModeTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int useDarkMode = 1;
                // Attribute 20: Windows 11 and Windows 10 20H1+
                if (DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int)) != 0)
                {
                    // Attribute 19: Windows 10 older builds
                    DwmSetWindowAttribute(hwnd, 19, ref useDarkMode, sizeof(int));
                }

                // DWMWA_CAPTION_COLOR = 35 (Windows 11): 0x00141414 (matches #141414 theater black)
                int captionColor = 0x00141414;
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                // DWMWA_TEXT_COLOR = 36 (Windows 11): White text
                int textColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
            }
            catch
            {
                // Safe fallback
            }
        }

        private readonly DataManager _dataManager = new();
        private readonly AudioPlayerService _audioPlayer = new();
        private readonly VideoPreviewService _videoPreviewService = new();
        private readonly UpdateService _updateService = new();
        private UpdateCheckResult? _lastUpdateCheckResult;

        private List<SfxItem> _allFiles = new();
        private ObservableCollection<SfxItem> _visibleFiles = new();
        private readonly Dictionary<string, (List<SfxItem> Filtered, List<VirtualFolderNode> Subfolders)> _filterResultCache = new(StringComparer.Ordinal);
        private System.Threading.CancellationTokenSource? _prebuildWaveformCts;

        private void InvalidateFilterCache()
        {
            _filterResultCache.Clear();
        }

        private void ApplyConfiguredCacheDirectories()
        {
            _audioPlayer.SetWaveformCacheDirectory(_dataManager.GetWaveformCacheDir());
            _videoPreviewService.SetCacheDirectory(_dataManager.GetVideoCacheDir());
            UpdateHeaderCacheBadge();
        }

        private readonly HashSet<string> _selectedFilterTags = new(StringComparer.OrdinalIgnoreCase);
        private bool _filterLogicAnd = true; // true = VÀ (Khớp tất cả tag), false = HOẶC (Khớp bất kỳ tag nào)
        private VirtualFolderNode? _selectedVirtualFolder = null;
        private List<VirtualFolderNode> _rootFolders = new();
        private bool _filterUntaggedOnly = false;
        private bool _isRightSidebarOpen = true;
        private string _rightTagSearchFilter = string.Empty;
        private bool _isThumbnailViewMode = false;
        private bool _isSyncingSelection = false;

        private Point _treeDragStartPoint;
        private VirtualFolderNode? _potentialDragNode = null;
        private bool _mouseDownNodeWasAlreadySelected = false;
        private string? _editingLinkFolderId = null;
        private readonly List<FileSystemWatcher> _folderWatchers = new();

        private Point _dragStartPoint;
        private SfxItem? _selectedItem;

        private List<SfxItem> GetSelectedSfxList()
        {
            List<SfxItem> list;
            if (_isThumbnailViewMode && SfxThumbnailListBox != null)
            {
                list = SfxThumbnailListBox.SelectedItems.OfType<SfxItem>().ToList();
            }
            else
            {
                list = SfxListView?.SelectedItems.OfType<SfxItem>().ToList() ?? new List<SfxItem>();
            }

            if (list.Count == 0 && _selectedItem != null)
            {
                list.Add(_selectedItem);
            }
            return list;
        }

        public MainWindow()
        {
            InitializeComponent();

            // Wire up Audio Player events
            _audioPlayer.PositionChanged += OnAudioPositionChanged;
            _audioPlayer.PlaybackStopped += OnAudioPlaybackStopped;
            _audioPlayer.WaveformReady += OnAudioWaveformReady;

            // Wire up Waveform UI events
            WaveformView.SeekRequested += (sec) =>
            {
                _audioPlayer.Seek(sec);
                UpdateVideoMonitorFrameForTime(sec, WaveformView.Duration);
            };
            WaveformView.RangeSelected += OnWaveformRangeSelected;

            // Global Tunneling Key Down for audio shortcuts (Space, I, O, L, Ctrl+F)
            PreviewKeyDown += OnMainWindowPreviewKeyDown;
            Activated += MainWindow_Activated;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            if (!IsLoaded) return;

            // Check if the currently selected file was deleted while the user was outside the app (e.g. in Explorer)
            if (_selectedItem != null && !string.IsNullOrWhiteSpace(_selectedItem.FilePath))
            {
                string path = _selectedItem.FilePath;
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && !File.Exists(path))
                {
                    var deadItem = _selectedItem;
                    _selectedItem = null;
                    _allFiles.Remove(deadItem);
                    _visibleFiles.Remove(deadItem);
                    foreach (var vf in _dataManager.Settings.VirtualFolders)
                    {
                        vf.Files.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
                    }
                    _ = _dataManager.SaveSettingsAsync();
                    _ = _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                    AllCountBadge.Text = _allFiles.Count.ToString("N0");
                    UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                    InvalidateFilterCache();
                    ApplyFilter();
                }
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Title = "Z Station Studio";
                ApplyDarkModeTitleBar();

                // Ensure valid desktop shortcut
                DesktopShortcutHelper.EnsureDesktopShortcut();

                // Apply saved settings & configured cache directories
                await _dataManager.LoadSettingsAsync();
                ApplyConfiguredCacheDirectories();
                Topmost = false;

                _audioPlayer.Volume = _dataManager.Settings.Volume;
                VolumeSlider.Value = _dataManager.Settings.Volume;
                _audioPlayer.IsLooping = _dataManager.Settings.IsLooping;
                UpdateLoopButtonUI();

                AutoPreviewCheck.IsChecked = _dataManager.Settings.AutoPreview;
                ApplyRightSidebarVisibility();

                // Load All SFX Data (and auto-sync linked folders)
                _allFiles = await _dataManager.LoadDataAsync();

                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");

                // Build Tag Chips & Dedicated Available Tags
                BuildTagChips();
                BuildQuickAvailableTags();

                // Build Virtual Folders Tree
                BuildFolderTree();

                // Update Sort Header Indicators
                UpdateSortHeaderUI();

                // Apply initial filter (All files)
                ApplyFilter();
                UpdateHeaderCacheBadge();
                RefreshHeaderUpdateButtonUI();

                // Start live FileSystemWatchers on linked folders so new files auto-update in real time
                SetupLinkedFolderWatchers();

                // Silently check if a new update is available in the background to highlight the Update button
                _ = CheckForAppUpdateSilentlyOnStartupAsync();
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "debug_startup.log"), $"[{DateTime.Now}] {ex}\n\n");
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "debug_close.log"), $"[{DateTime.Now}] MainWindow_Closing triggered! Stack:\n{Environment.StackTrace}\n\n");
            }
            catch { }

            DisposeFolderWatchers();
            _audioPlayer.Stop();
            _audioPlayer.Dispose();

            // Save Settings
            _dataManager.Settings.AlwaysOnTop = false;
            _dataManager.Settings.Volume = _audioPlayer.Volume;
            _dataManager.Settings.IsLooping = _audioPlayer.IsLooping;
            _dataManager.Settings.AutoPreview = AutoPreviewCheck.IsChecked == true;
            _dataManager.SaveSettingsNow();
        }

        #region Tag Chips & Dedicated Available Tags & Sidebar

        private void ToggleRightSidebarBtn_Click(object sender, RoutedEventArgs e)
        {
            _isRightSidebarOpen = !_isRightSidebarOpen;
            ApplyRightSidebarVisibility();
        }

        private void ApplyRightSidebarVisibility()
        {
            if (RightSidebarBorder == null || RightSidebarColumn == null) return;

            if (_isRightSidebarOpen)
            {
                RightSidebarBorder.Visibility = Visibility.Visible;
                if (RightGridSplitter != null) RightGridSplitter.Visibility = Visibility.Visible;
                RightSidebarColumn.Width = new GridLength(305, GridUnitType.Pixel);
                RightSidebarColumn.MinWidth = 240;

                if (ToggleRightSidebarBtn != null)
                {
                    ToggleRightSidebarBtn.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x14, 0x1C));
                    ToggleRightSidebarBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
                    ToggleRightSidebarBtn.Foreground = Brushes.White;
                }
            }
            else
            {
                RightSidebarBorder.Visibility = Visibility.Collapsed;
                if (RightGridSplitter != null) RightGridSplitter.Visibility = Visibility.Collapsed;
                RightSidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
                RightSidebarColumn.MinWidth = 0;

                if (ToggleRightSidebarBtn != null)
                {
                    ToggleRightSidebarBtn.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x19, 0x14));
                    ToggleRightSidebarBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x36, 0x1D));
                    ToggleRightSidebarBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));
                }
            }

            UpdateMiddleToolbarResponsiveLayout();
        }

        private void TagFilterLogicBtn_Click(object sender, RoutedEventArgs e)
        {
            _filterLogicAnd = !_filterLogicAnd;
            UpdateTagFilterLogicUI();
            ApplyFilter();
        }

        private void UpdateTagFilterLogicUI()
        {
            if (TagFilterLogicBtn == null) return;
            TagFilterLogicBtn.Visibility = Visibility.Visible;

            if (_filterLogicAnd)
            {
                TagFilterLogicBtn.Content = _selectedFilterTags.Count > 1 ? "Khớp: VÀ (Tất cả)" : "Lọc: VÀ";
                TagFilterLogicBtn.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                TagFilterLogicBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
                TagFilterLogicBtn.Foreground = Brushes.White;
                TagFilterLogicBtn.ToolTip = "Chế độ lọc multi tag: VÀ (Khớp tất cả các tag đã chọn) - Click để chuyển sang HOẶC";
            }
            else
            {
                TagFilterLogicBtn.Content = _selectedFilterTags.Count > 1 ? "Khớp: HOẶC (Bất kỳ)" : "Lọc: HOẶC";
                TagFilterLogicBtn.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                TagFilterLogicBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));
                TagFilterLogicBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                TagFilterLogicBtn.ToolTip = "Chế độ lọc multi tag: HOẶC (Khớp bất kỳ tag nào trong số tag đã chọn) - Click để chuyển sang VÀ";
            }
        }

        private void BuildTagChips()
        {
            InvalidateFilterCache();
            TagChipsPanel.Children.Clear();

            // 1. "Tất cả" chip
            var allBtn = CreateChipButton("Tất cả", null);
            TagChipsPanel.Children.Add(allBtn);

            // 2. "Chưa tag" chip
            int untaggedCount = _allFiles.Count(f => f.Tags.Count == 0);
            var untaggedBtn = CreateChipButton($"Chưa tag ({untaggedCount:N0})", "__UNTAGGED__");
            TagChipsPanel.Children.Add(untaggedBtn);

            // 3. Unique tag chips (all tags registered or used)
            var tags = _dataManager.GetAllUniqueTags(_allFiles);
            foreach (var tag in tags)
            {
                var chip = CreateChipButton(tag, tag);
                TagChipsPanel.Children.Add(chip);
            }

            UpdateTagChipsSelection();
            UpdateTagFilterLogicUI();
            UpdateQuickAvailableTagsUI();
        }

        private Button CreateChipButton(string label, string? tagValue)
        {
            var btn = new Button
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 5, 10, 5),
                Tag = tagValue,
                Cursor = Cursors.Hand,
                FontSize = 13.5
            };

            if (tagValue == null || tagValue == "__UNTAGGED__")
            {
                btn.Content = label;
            }
            else
            {
                // Compound chip: [ Vector Tag Icon | Label ]
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var icon = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconTag"),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = Brushes.Transparent,
                    Width = 11,
                    Height = 11,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(icon);

                var lbl = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13.5
                };
                sp.Children.Add(lbl);

                btn.Content = sp;
            }

            btn.Click += (s, e) =>
            {
                if (tagValue == null) // "Tất cả" chip
                {
                    _selectedFilterTags.Clear();
                    _filterUntaggedOnly = false;
                    UpdateTagChipsSelection();
                    UpdateTagFilterLogicUI();
                    ApplyFilter(autoSwitchViewMode: true);
                    return;
                }

                if (tagValue == "__UNTAGGED__")
                {
                    _filterUntaggedOnly = !_filterUntaggedOnly;
                    _selectedFilterTags.Clear();
                    UpdateTagChipsSelection();
                    UpdateTagFilterLogicUI();
                    ApplyFilter(autoSwitchViewMode: true);
                    return;
                }

                // Chế độ Lọc Multi-tag: Click tag để thêm/bớt khỏi bộ lọc
                if (_selectedFilterTags.Contains(tagValue))
                {
                    _selectedFilterTags.Remove(tagValue);
                }
                else
                {
                    _selectedFilterTags.Add(tagValue);
                }

                _filterUntaggedOnly = false;
                UpdateTagChipsSelection();
                UpdateTagFilterLogicUI();
                ApplyFilter(autoSwitchViewMode: true);
            };

            return btn;
        }

        // ================= SORTING & COLUMN HEADERS =================
        private string _sortColumn = "Name";
        private bool _isSortAscending = true;

        private void SortHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string col)
            {
                if (_sortColumn == col)
                {
                    _isSortAscending = !_isSortAscending;
                }
                else
                {
                    _sortColumn = col;
                    _isSortAscending = true;
                }

                UpdateSortHeaderUI();
                ApplyFilter();
            }
        }

        private void UpdateSortHeaderUI()
        {
            if (SortNameArrow == null) return;

            string arrow = _isSortAscending ? " ▴" : " ▾";

            SortNameArrow.Visibility = _sortColumn == "Name" ? Visibility.Visible : Visibility.Collapsed;
            SortNameArrow.Text = arrow;

            SortFolderArrow.Visibility = _sortColumn == "Folder" ? Visibility.Visible : Visibility.Collapsed;
            SortFolderArrow.Text = arrow;

            SortDurationArrow.Visibility = _sortColumn == "Duration" ? Visibility.Visible : Visibility.Collapsed;
            SortDurationArrow.Text = arrow;

            SortExtArrow.Visibility = _sortColumn == "Extension" ? Visibility.Visible : Visibility.Collapsed;
            SortExtArrow.Text = arrow;

            SortSizeArrow.Visibility = _sortColumn == "Size" ? Visibility.Visible : Visibility.Collapsed;
            SortSizeArrow.Text = arrow;
        }

        private void BuildQuickAvailableTags()
        {
            UpdateQuickAvailableTagsUI();
        }

        private void UpdateQuickAvailableTagsUI()
        {
            if (RightAvailableTagsWrapPanel == null) return;

            var targetFiles = GetSelectedSfxList();

            // 1. Update Selected File Info Card
            if (targetFiles.Count == 0)
            {
                if (RightSelectedFileTitle != null) RightSelectedFileTitle.Text = "Chưa chọn âm thanh";
                if (RightSelectedFileMeta != null) RightSelectedFileMeta.Text = "Click vào một âm thanh để xem và gán tag";
                if (RightCurrentTagsWrapPanel != null)
                {
                    RightCurrentTagsWrapPanel.Children.Clear();
                    if (RightNoCurrentTagsText != null)
                    {
                        RightNoCurrentTagsText.Text = "(Chưa chọn âm thanh)";
                        RightCurrentTagsWrapPanel.Children.Add(RightNoCurrentTagsText);
                    }
                }
            }
            else if (targetFiles.Count == 1)
            {
                var f = targetFiles[0];
                if (RightSelectedFileTitle != null) RightSelectedFileTitle.Text = f.FileName;
                string resExtra = !string.IsNullOrEmpty(f.ResolutionBadge) ? $" • {f.ResolutionBadge}" : "";
                string durStr = f.DurationText;
                string sizeStr = f.FileSizeText;
                string meta = f.FolderName;
                if (!string.IsNullOrEmpty(f.Extension)) meta += $" • {f.Extension}{resExtra}";
                if (!string.IsNullOrEmpty(durStr) && durStr != "--:--") meta += $" • {durStr}";
                if (!string.IsNullOrEmpty(sizeStr)) meta += $" • {sizeStr}";
                if (RightSelectedFileMeta != null) RightSelectedFileMeta.Text = meta;

                if (RightCurrentTagsWrapPanel != null)
                {
                    RightCurrentTagsWrapPanel.Children.Clear();
                    if (f.Tags == null || f.Tags.Count == 0)
                    {
                        if (RightNoCurrentTagsText != null)
                        {
                            RightNoCurrentTagsText.Text = "(Chưa có tag nào)";
                            RightCurrentTagsWrapPanel.Children.Add(RightNoCurrentTagsText);
                        }
                    }
                    else
                    {
                        foreach (var tag in f.Tags)
                        {
                            var pill = CreateCurrentTagPill(tag, isRemovable: true);
                            RightCurrentTagsWrapPanel.Children.Add(pill);
                        }
                    }
                }
            }
            else // Multi-select
            {
                if (RightSelectedFileTitle != null) RightSelectedFileTitle.Text = $"{targetFiles.Count:N0} Media đã chọn";
                if (RightSelectedFileMeta != null) RightSelectedFileMeta.Text = "Đang chọn nhiều file để gán/gỡ tag hàng loạt";

                if (RightCurrentTagsWrapPanel != null)
                {
                    RightCurrentTagsWrapPanel.Children.Clear();

                    var commonTags = targetFiles
                        .Select(x => x.Tags)
                        .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase).ToList());

                    if (commonTags.Count == 0)
                    {
                        if (RightNoCurrentTagsText != null)
                        {
                            RightNoCurrentTagsText.Text = "(Không có tag chung)";
                            RightCurrentTagsWrapPanel.Children.Add(RightNoCurrentTagsText);
                        }
                    }
                    else
                    {
                        foreach (var tag in commonTags)
                        {
                            var pill = CreateCurrentTagPill(tag, isRemovable: true);
                            RightCurrentTagsWrapPanel.Children.Add(pill);
                        }
                    }
                }
            }

            // 2. Populate Available Tags Grid (Bảng tag)
            PopulateRightAvailableTags(targetFiles);
        }

        private UIElement CreateCurrentTagPill(string tag, bool isRemovable)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 225, 29, 72)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x1B, 0x2E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 3.5, 8, 3.5),
                Margin = new Thickness(0, 0, 6, 6),
                VerticalAlignment = VerticalAlignment.Center
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tb = new TextBlock
            {
                Text = tag,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xA4, 0xAF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(tb);

            if (isRemovable)
            {
                var delBtn = new Button
                {
                    Content = "✕",
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Gỡ tag '{tag}' khỏi file đang chọn"
                };
                delBtn.Click += async (s, e) =>
                {
                    e.Handled = true;
                    await ToggleTagForSelectedItemsAsync(tag);
                };
                sp.Children.Add(delBtn);
            }

            border.Child = sp;
            return border;
        }

        private void PopulateRightAvailableTags(List<SfxItem>? targetFiles = null)
        {
            if (RightAvailableTagsWrapPanel == null) return;
            targetFiles ??= GetSelectedSfxList();

            RightAvailableTagsWrapPanel.Children.Clear();

            var allTags = _dataManager.GetAllUniqueTags(_allFiles);
            string filter = _rightTagSearchFilter.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                string normFilter = SearchNormalizer.Normalize(filter);
                allTags = allTags.Where(t => SearchNormalizer.Normalize(t).Contains(normFilter)).ToList();
            }

            if (RightTagCountSummary != null)
            {
                RightTagCountSummary.Text = $"{allTags.Count} tag";
            }

            Style? pillStyle = TryFindResource("RightTagPillStyle") as Style;

            foreach (var tag in allTags)
            {
                string normTag = SearchNormalizer.Normalize(tag);
                bool allHave = targetFiles.Count > 0 && targetFiles.All(f => f.NormalizedTags.Contains(normTag));
                bool someHave = !allHave && targetFiles.Count > 0 && targetFiles.Any(f => f.NormalizedTags.Contains(normTag));

                var btn = new Button
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 5, 10, 5),
                    Cursor = Cursors.Hand,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Tag = tag
                };

                if (pillStyle != null)
                {
                    btn.Style = pillStyle;
                }

                if (allHave)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x14, 0x1E));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
                    btn.BorderThickness = new Thickness(1.2);
                    btn.Foreground = Brushes.White;
                    btn.Content = $"✓  {tag}";
                    btn.ToolTip = targetFiles.Count > 1
                        ? $"Click để GỠ tag '{tag}' khỏi tất cả {targetFiles.Count} SFX đang chọn"
                        : $"Click để GỠ tag '{tag}' khỏi SFX này";
                }
                else if (someHave)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x1C, 0x14));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                    btn.BorderThickness = new Thickness(1.2);
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A));
                    btn.Content = $"◐  {tag}";
                    btn.ToolTip = $"Một số SFX đã có tag này. Click để GÁN '{tag}' cho TẤT CẢ {targetFiles.Count} SFX đang chọn";
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x20));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x38));
                    btn.BorderThickness = new Thickness(1);
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7));
                    btn.Content = $"+  {tag}";
                    btn.ToolTip = targetFiles.Count > 0
                        ? $"Click để GÁN tag '{tag}' cho âm thanh đang chọn"
                        : "Chọn một âm thanh để gán tag này";
                }

                btn.Click += async (s, e) =>
                {
                    await ToggleTagForSelectedItemsAsync(tag);
                };

                RightAvailableTagsWrapPanel.Children.Add(btn);
            }
        }

        private async Task ToggleTagForSelectedItemsAsync(string tag)
        {
            var targetFiles = GetSelectedSfxList();
            if (targetFiles.Count == 0)
            {
                ZDialog.Show("Vui lòng click chọn một hoặc nhiều âm thanh trong danh sách trước khi gán tag!", "Chưa chọn SFX", ZDialogKind.Info, owner: this);
                return;
            }

            tag = tag.Trim();
            string normTag = SearchNormalizer.Normalize(tag);

            // Kiểm tra xem tất cả các file đã chọn đã có tag này chưa
            bool allHaveTag = targetFiles.All(f => f.NormalizedTags.Contains(normTag));

            if (allHaveTag)
            {
                // Gỡ tag khỏi tất cả các file
                foreach (var f in targetFiles)
                {
                    f.Tags = f.Tags.Where(t => SearchNormalizer.Normalize(t) != normTag).ToList();
                    f.SearchNormalized = SearchNormalizer.Normalize($"{f.FileName} {f.FolderName} {f.TagsDisplay}");
                    _dataManager.Settings.Tags[f.FilePath] = f.Tags;
                    _dataManager.Settings.Tags[f.FilePath.Replace('/', '\\')] = f.Tags;
                    _dataManager.Settings.Tags[f.FileName] = f.Tags;
                }
            }
            else
            {
                // Gán tag cho tất cả các file chưa có
                foreach (var f in targetFiles)
                {
                    if (!f.NormalizedTags.Contains(normTag))
                    {
                        var updated = new List<string>(f.Tags) { tag };
                        f.Tags = updated;
                        f.SearchNormalized = SearchNormalizer.Normalize($"{f.FileName} {f.FolderName} {f.TagsDisplay}");
                        _dataManager.Settings.Tags[f.FilePath] = f.Tags;
                        _dataManager.Settings.Tags[f.FilePath.Replace('/', '\\')] = f.Tags;
                        _dataManager.Settings.Tags[f.FileName] = f.Tags;
                    }
                }

                if (!_dataManager.Settings.CustomTags.Any(t => SearchNormalizer.Normalize(t) == normTag))
                {
                    _dataManager.Settings.CustomTags.Add(tag);
                }
                _dataManager.Settings.DeletedTags.RemoveAll(t => SearchNormalizer.Normalize(t) == normTag);
            }

            InvalidateFilterCache();
            await _dataManager.SaveSettingsAsync();

            bool chipExists = TagChipsPanel.Children.OfType<Button>()
                .Any(b => b.Tag is string tv && SearchNormalizer.Normalize(tv) == normTag);

            if (!chipExists)
            {
                BuildTagChips();
            }
            else
            {
                UpdateTagChipsSelection();
            }
            UpdateQuickAvailableTagsUI();
        }

        // ================= RIGHT SIDEBAR NEW TAG & SEARCH INPUTS =================
        private void RightNewTagInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (RightNewTagPlaceholder != null)
            {
                RightNewTagPlaceholder.Visibility = string.IsNullOrEmpty(RightNewTagInput?.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void RightNewTagInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await AddNewRightTagFromInputAsync();
                e.Handled = true;
            }
        }

        private async void RightAddNewTagBtn_Click(object sender, RoutedEventArgs e)
        {
            await AddNewRightTagFromInputAsync();
        }

        private async Task AddNewRightTagFromInputAsync()
        {
            if (RightNewTagInput == null) return;
            string newTag = RightNewTagInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(newTag)) return;

            string normTag = SearchNormalizer.Normalize(newTag);
            if (!_dataManager.Settings.CustomTags.Any(t => SearchNormalizer.Normalize(t) == normTag))
            {
                _dataManager.Settings.CustomTags.Add(newTag);
            }
            _dataManager.Settings.DeletedTags.RemoveAll(t => SearchNormalizer.Normalize(t) == normTag);

            var targetFiles = GetSelectedSfxList();
            if (targetFiles.Count > 0)
            {
                await ToggleTagForSelectedItemsAsync(newTag);
            }
            else
            {
                await _dataManager.SaveSettingsAsync();
                BuildTagChips();
                UpdateQuickAvailableTagsUI();
            }

            RightNewTagInput.Text = string.Empty;
        }

        private void RightTagSearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            _rightTagSearchFilter = RightTagSearchInput?.Text ?? string.Empty;
            if (RightTagSearchPlaceholder != null)
            {
                RightTagSearchPlaceholder.Visibility = string.IsNullOrEmpty(_rightTagSearchFilter) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ClearRightTagSearchBtn != null)
            {
                ClearRightTagSearchBtn.Visibility = string.IsNullOrEmpty(_rightTagSearchFilter) ? Visibility.Collapsed : Visibility.Visible;
            }
            PopulateRightAvailableTags();
        }

        private void ClearRightTagSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (RightTagSearchInput != null)
            {
                RightTagSearchInput.Text = string.Empty;
            }
        }

        // ================= TOP BAR QUICK NEW TAG INPUT =================
        private void NewTagInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            NewTagPlaceholder.Visibility = string.IsNullOrEmpty(NewTagInput?.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void NewTagInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await AddNewCustomTagFromInputAsync();
                e.Handled = true;
            }
        }

        private async void AddNewTagBtn_Click(object sender, RoutedEventArgs e)
        {
            await AddNewCustomTagFromInputAsync();
        }

        private async Task AddNewCustomTagFromInputAsync()
        {
            if (NewTagInput == null) return;
            string newTag = NewTagInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(newTag)) return;

            string normTag = SearchNormalizer.Normalize(newTag);
            if (!_dataManager.Settings.CustomTags.Any(t => SearchNormalizer.Normalize(t) == normTag))
            {
                _dataManager.Settings.CustomTags.Add(newTag);
            }

            // Remove from DeletedTags if previously deleted
            _dataManager.Settings.DeletedTags.RemoveAll(t => SearchNormalizer.Normalize(t) == normTag);

            var targetFiles = GetSelectedSfxList();
            if (targetFiles.Count > 0)
            {
                await ToggleTagForSelectedItemsAsync(newTag);
            }
            else
            {
                await _dataManager.SaveSettingsAsync();
                BuildTagChips();
                UpdateQuickAvailableTagsUI();
            }

            NewTagInput.Text = string.Empty;
        }

        private void UpdateTagChipsSelection()
        {
            int untaggedCount = _allFiles.Count(f => f.Tags.Count == 0);
            UntaggedCountBadge.Text = untaggedCount.ToString("N0");

            foreach (var child in TagChipsPanel.Children.OfType<Button>())
            {
                string? tagVal = child.Tag as string;

                if (tagVal == null) // "Tất cả"
                {
                    bool isAllActive = _selectedFilterTags.Count == 0 && !_filterUntaggedOnly;
                    child.Background = isAllActive ? new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
                    child.BorderBrush = isAllActive ? new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    child.Foreground = isAllActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
                    continue;
                }

                if (tagVal == "__UNTAGGED__")
                {
                    child.Content = $"Chưa tag ({untaggedCount:N0})";
                    child.Background = _filterUntaggedOnly ? new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
                    child.BorderBrush = _filterUntaggedOnly ? new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    child.Foreground = _filterUntaggedOnly ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
                    continue;
                }

                bool isFilterActive = _selectedFilterTags.Contains(tagVal);

                if (child.Content is StackPanel sp && sp.Children.Count >= 2 &&
                    sp.Children[0] is System.Windows.Shapes.Path tagIcon &&
                    sp.Children[1] is TextBlock lbl)
                {
                    tagIcon.Stroke = isFilterActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
                    lbl.Text = isFilterActive ? $"{tagVal} ✓" : tagVal;
                    child.Background = isFilterActive ? new SolidColorBrush(Color.FromRgb(0x2B, 0x14, 0x1C)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
                    child.BorderBrush = isFilterActive ? new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    child.Foreground = isFilterActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
                    child.ToolTip = isFilterActive
                        ? $"Click để bỏ lọc tag '{tagVal}'"
                        : $"Click để thêm tag '{tagVal}' vào bộ lọc multi-tag";
                }
            }
        }

        // ================= TAG MANAGEMENT MODAL & SYNC =================
        private void ManageTagsBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshModalTagList();
            TagManageModal.Visibility = Visibility.Visible;
            ModalNewTagInput.Focus();
        }

        private void CloseTagManageModal_Click(object sender, RoutedEventArgs e)
        {
            TagManageModal.Visibility = Visibility.Collapsed;
        }

        private async void ModalNewTagInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await AddModalTagAsync();
                e.Handled = true;
            }
        }

        private async void ModalAddTagBtn_Click(object sender, RoutedEventArgs e)
        {
            await AddModalTagAsync();
        }

        private async Task AddModalTagAsync()
        {
            string val = ModalNewTagInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(val)) return;

            string norm = SearchNormalizer.Normalize(val);
            if (!_dataManager.Settings.CustomTags.Any(t => SearchNormalizer.Normalize(t) == norm))
            {
                _dataManager.Settings.CustomTags.Add(val);
                _dataManager.Settings.DeletedTags.RemoveAll(t => SearchNormalizer.Normalize(t) == norm);
                await _dataManager.SaveSettingsAsync();
                BuildTagChips();
                BuildQuickAvailableTags();
                RefreshModalTagList();
            }

            ModalNewTagInput.Text = string.Empty;
        }

        private void RefreshModalTagList()
        {
            RefreshTagFileStatsUI();
            ModalTagListPanel.Children.Clear();
            var tags = _dataManager.GetAllUniqueTags(_allFiles);

            var tagGeometry = TryFindResource("IconTag") as Geometry;
            var editGeometry = TryFindResource("IconEdit") as Geometry;
            var trashGeometry = TryFindResource("IconTrash") as Geometry;

            foreach (var tag in tags)
            {
                int count = _allFiles.Count(f => f.Tags.Any(t => SearchNormalizer.Normalize(t) == SearchNormalizer.Normalize(tag)));

                var cardBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    SnapsToDevicePixels = true
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var inputContainer = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Height = 30,
                    Padding = new Thickness(9, 0, 8, 0)
                };

                var inputGrid = new Grid();
                inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (tagGeometry != null)
                {
                    var tagIcon = new System.Windows.Shapes.Path
                    {
                        Data = tagGeometry,
                        Stroke = count > 0
                            ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                            : new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x58)),
                        StrokeThickness = 1.8,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Stretch = Stretch.Uniform,
                        Width = 12,
                        Height = 12,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(tagIcon, 0);
                    inputGrid.Children.Add(tagIcon);
                }

                var txtBox = new TextBox
                {
                    Text = tag,
                    Tag = tag,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    CaretBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtBox, 1);
                inputGrid.Children.Add(txtBox);
                inputContainer.Child = inputGrid;
                Grid.SetColumn(inputContainer, 0);

                var badge = new Border
                {
                    Background = count > 0
                        ? new SolidColorBrush(Color.FromRgb(0x13, 0x22, 0x30))
                        : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                    BorderBrush = count > 0
                        ? new SolidColorBrush(Color.FromRgb(0x1F, 0x3B, 0x54))
                        : new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 4, 9, 4),
                    MinWidth = 68,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"{count:N0} SFX",
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = count > 0
                            ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
                    }
                };
                Grid.SetColumn(badge, 1);

                var saveStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (editGeometry != null)
                {
                    saveStack.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = editGeometry,
                        Stroke = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)),
                        StrokeThickness = 1.8,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stretch = Stretch.Uniform,
                        Width = 11,
                        Height = 11,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                saveStack.Children.Add(new TextBlock { Text = "Lưu", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

                var saveBtn = new Button
                {
                    Content = saveStack,
                    Height = 30,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(11, 0, 11, 0),
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5)),
                    ToolTip = "Lưu tên mới cho tag này (hoặc nhấn Enter)"
                };
                Grid.SetColumn(saveBtn, 2);

                string originalTag = tag;
                txtBox.GotFocus += (s, e) =>
                {
                    inputContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                };
                txtBox.LostFocus += (s, e) =>
                {
                    inputContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                };
                txtBox.TextChanged += (s, e) =>
                {
                    bool isModified = !string.IsNullOrWhiteSpace(txtBox.Text.Trim()) && txtBox.Text.Trim() != originalTag;
                    saveBtn.Background = isModified
                        ? new SolidColorBrush(Color.FromRgb(0x14, 0x28, 0x1D))
                        : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
                    saveBtn.BorderBrush = isModified
                        ? new SolidColorBrush(Color.FromRgb(0x23, 0x4B, 0x35))
                        : new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35));
                    saveBtn.Foreground = isModified
                        ? new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69))
                        : new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                };
                txtBox.KeyDown += async (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        string newName = txtBox.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(newName) && newName != originalTag)
                        {
                            await RenameTagGloballyAsync(originalTag, newName);
                        }
                    }
                };

                saveBtn.Click += async (s, e) =>
                {
                    string newName = txtBox.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(newName) && newName != originalTag)
                    {
                        await RenameTagGloballyAsync(originalTag, newName);
                    }
                };

                var delStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (trashGeometry != null)
                {
                    delStack.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = trashGeometry,
                        Stroke = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
                        StrokeThickness = 1.8,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Stretch = Stretch.Uniform,
                        Width = 11,
                        Height = 11,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                delStack.Children.Add(new TextBlock { Text = "Xóa", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

                var delBtn = new Button
                {
                    Content = delStack,
                    Height = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(11, 0, 11, 0),
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(0x23, 0x15, 0x17)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x20, 0x24)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
                    ToolTip = "Xóa tag này vĩnh viễn khỏi toàn bộ thư viện"
                };
                Grid.SetColumn(delBtn, 3);

                delBtn.Click += async (s, e) =>
                {
                    if (ZDialog.Confirm($"Bạn có chắc chắn muốn xóa tag \"{originalTag}\" khỏi toàn bộ thư viện SFX không?", "Xóa tag", ZDialogKind.Danger, "Xóa", "Hủy", this))
                    {
                        await DeleteTagGloballyAsync(originalTag);
                    }
                };

                row.Children.Add(inputContainer);
                row.Children.Add(badge);
                row.Children.Add(saveBtn);
                row.Children.Add(delBtn);
                cardBorder.Child = row;
                ModalTagListPanel.Children.Add(cardBorder);
            }
        }

        private void RefreshTagFileStatsUI()
        {
            if (TagFileCurrentPathText == null || TagFileStatsSummaryText == null) return;
            var stats = _dataManager.GetTagFileStats(_allFiles);
            TagFileCurrentPathText.Text = stats.FilePath;
            TagFileStatsSummaryText.Text = $"{stats.UniqueTagCount} Tag • {stats.TaggedFileCount:N0} File gắn Tag • {stats.BinCount} Bin";
        }

        private async void SelectTagFileBtn_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = DataManager.DropboxSyncFile;
            string initialDir = Path.GetDirectoryName(currentPath) ?? AppContext.BaseDirectory;
            if (!Directory.Exists(initialDir)) initialDir = AppContext.BaseDirectory;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn file dữ liệu Tag & Cấu trúc Bin (sfx-settings-sync.json)",
                Filter = "Tag Sync JSON (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = initialDir,
                FileName = Path.GetFileName(currentPath)
            };

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                SelectTagFileBtn.IsEnabled = false;
                try
                {
                    var res = await _dataManager.LoadAndLinkTagSyncFileAsync(dlg.FileName, _allFiles);
                    if (res.Success)
                    {
                        AllCountBadge.Text = _allFiles.Count.ToString("N0");
                        UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                        BuildTagChips();
                        BuildQuickAvailableTags();
                        BuildFolderTree();
                        RefreshModalTagList();
                        ApplyFilter();
                        ZDialog.Show(res.Message, "Liên kết & Nạp File Tag thành công", ZDialogKind.Success, owner: this);
                    }
                    else
                    {
                        ZDialog.Show(res.Message, "Không thể nạp File Tag", ZDialogKind.Danger, owner: this);
                    }
                }
                finally
                {
                    SelectTagFileBtn.IsEnabled = true;
                }
            }
        }

        private async void OptimizeTagFileBtn_Click(object sender, RoutedEventArgs e)
        {
            OptimizeTagFileBtn.IsEnabled = false;
            try
            {
                var res = await _dataManager.OptimizeTagFileAndIndexAsync(_allFiles);
                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                BuildTagChips();
                BuildQuickAvailableTags();
                BuildFolderTree();
                RefreshModalTagList();
                ApplyFilter();
                UpdateHeaderCacheBadge();
                ZDialog.Show(res.Message, "Tối ưu File Tag & Tăng tốc Load", ZDialogKind.Success, owner: this);
            }
            catch (Exception ex)
            {
                ZDialog.Show($"Lỗi khi tối ưu file Tag: {ex.Message}", "Lỗi tối ưu", ZDialogKind.Danger, owner: this);
            }
            finally
            {
                OptimizeTagFileBtn.IsEnabled = true;
            }
        }

        private async void BackupTagFileBtn_Click(object sender, RoutedEventArgs e)
        {
            BackupTagFileBtn.IsEnabled = false;
            try
            {
                await _dataManager.SaveSettingsAsync();
                var res = await _dataManager.CreateAutoBackupSnapshotAsync();
                RefreshTagFileStatsUI();
                if (res.Success)
                {
                    ZDialog.Show(
                        res.Message,
                        "Sao lưu File Tag thành công",
                        ZDialogKind.Success,
                        owner: this);
                }
                else
                {
                    ZDialog.Show(res.Message, "Lỗi sao lưu", ZDialogKind.Warning, owner: this);
                }
            }
            finally
            {
                BackupTagFileBtn.IsEnabled = true;
            }
        }

        private void OpenTagFileFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string filePath = DataManager.DropboxSyncFile;
                if (File.Exists(filePath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                else
                {
                    string? dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                ZDialog.Show($"Không thể mở thư mục chứa file Tag: {ex.Message}", "Lỗi", ZDialogKind.Warning, owner: this);
            }
        }

        private async Task RenameTagGloballyAsync(string oldTag, string newTag)
        {
            string oldNorm = SearchNormalizer.Normalize(oldTag);
            string newNorm = SearchNormalizer.Normalize(newTag);

            // Update CustomTags
            for (int i = 0; i < _dataManager.Settings.CustomTags.Count; i++)
            {
                if (SearchNormalizer.Normalize(_dataManager.Settings.CustomTags[i]) == oldNorm)
                {
                    _dataManager.Settings.CustomTags[i] = newTag;
                }
            }
            if (!_dataManager.Settings.CustomTags.Any(t => SearchNormalizer.Normalize(t) == newNorm))
            {
                _dataManager.Settings.CustomTags.Add(newTag);
            }

            // Update in-memory files and settings.Tags
            foreach (var item in _allFiles)
            {
                bool changed = false;
                for (int i = 0; i < item.Tags.Count; i++)
                {
                    if (SearchNormalizer.Normalize(item.Tags[i]) == oldNorm)
                    {
                        item.Tags[i] = newTag;
                        changed = true;
                    }
                }
                if (changed)
                {
                    item.Tags = item.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
                    _dataManager.Settings.Tags[item.FilePath] = item.Tags;
                    _dataManager.Settings.Tags[item.FilePath.Replace('/', '\\')] = item.Tags;
                }
            }

            if (_selectedFilterTags.Contains(oldTag))
            {
                _selectedFilterTags.Remove(oldTag);
                _selectedFilterTags.Add(newTag);
            }

            await _dataManager.SaveSettingsAsync();
            BuildTagChips();
            BuildQuickAvailableTags();
            RefreshModalTagList();
            ApplyFilter();
        }

        private async Task DeleteTagGloballyAsync(string tag)
        {
            string norm = SearchNormalizer.Normalize(tag);

            if (!_dataManager.Settings.DeletedTags.Any(d => SearchNormalizer.Normalize(d) == norm))
            {
                _dataManager.Settings.DeletedTags.Add(tag);
            }

            _dataManager.Settings.CustomTags.RemoveAll(t => SearchNormalizer.Normalize(t) == norm);

            foreach (var item in _allFiles)
            {
                if (item.NormalizedTags.Contains(norm))
                {
                    item.Tags = item.Tags.Where(t => SearchNormalizer.Normalize(t) != norm).ToList();
                    item.SearchNormalized = SearchNormalizer.Normalize($"{item.FileName} {item.FolderName} {item.TagsDisplay}");
                    _dataManager.Settings.Tags[item.FilePath] = item.Tags;
                    _dataManager.Settings.Tags[item.FilePath.Replace('/', '\\')] = item.Tags;
                }
            }

            _selectedFilterTags.Remove(tag);

            await _dataManager.SaveSettingsAsync();
            BuildTagChips();
            RefreshModalTagList();
            ApplyFilter();
        }

        private async void SyncTagsBtn_Click(object sender, RoutedEventArgs e)
        {
            SyncTagsBtn.IsEnabled = false;
            if (SyncTagsText != null) SyncTagsText.Text = "Đang đồng bộ...";

            try
            {
                bool ok = await _dataManager.SyncWithDropboxMasterAsync(_allFiles);
                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");

                BuildTagChips();
                BuildFolderTree();
                ApplyFilter();

                if (ok)
                {
                    ZDialog.Show("Đã đồng bộ Tags và Thư mục thành công với Dropbox (Ổ F)!", "Đồng bộ thành công", ZDialogKind.Success, owner: this);
                }
                else
                {
                    ZDialog.Show("Không tìm thấy file đồng bộ Dropbox hoặc không thể đọc file sfx-settings-sync.json.", "Không thể đồng bộ", ZDialogKind.Warning, owner: this);
                }
            }
            finally
            {
                SyncTagsBtn.IsEnabled = true;
                if (SyncTagsText != null) SyncTagsText.Text = "Đồng bộ";
            }
        }

        private void FolderTreeView_ItemExpanded(object sender, RoutedEventArgs e)
        {
            var tvi = (e.OriginalSource as TreeViewItem) ?? (e.Source as TreeViewItem);
            if (tvi?.DataContext is VirtualFolderNode node)
            {
                node.IsExpanded = true;
                _dataManager.SetVirtualFolderOpenState(node.Id, true);
            }
            e.Handled = true;
        }

        private void FolderTreeView_ItemCollapsed(object sender, RoutedEventArgs e)
        {
            var tvi = (e.OriginalSource as TreeViewItem) ?? (e.Source as TreeViewItem);
            if (tvi?.DataContext is VirtualFolderNode node)
            {
                node.IsExpanded = false;
                _dataManager.SetVirtualFolderOpenState(node.Id, false);
            }
            e.Handled = true;
        }

        private void BuildFolderTree()
        {
            InvalidateFilterCache();
            string? prevId = _selectedVirtualFolder?.Id;

            // Preserve current expanded states before rebuilding
            var expandedStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            void SaveExpanded(IEnumerable<VirtualFolderNode> nodes)
            {
                foreach (var n in nodes)
                {
                    expandedStates[n.Id] = n.IsExpanded;
                    if (n.Children != null && n.Children.Count > 0)
                    {
                        SaveExpanded(n.Children);
                    }
                }
            }
            if (_rootFolders != null)
            {
                SaveExpanded(_rootFolders);
            }

            bool includeSubs = IncludeSubfoldersCheck?.IsChecked == true;
            _rootFolders = _dataManager.BuildVirtualFolderHierarchy(includeSubs);

            void RestoreExpanded(IEnumerable<VirtualFolderNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (expandedStates.TryGetValue(n.Id, out bool wasExpanded))
                    {
                        n.IsExpanded = wasExpanded;
                    }
                    if (n.Children != null && n.Children.Count > 0)
                    {
                        RestoreExpanded(n.Children);
                    }
                }
            }
            if (expandedStates.Count > 0)
            {
                RestoreExpanded(_rootFolders);
            }

            _suppressTreeSelectionEvent = true;
            try
            {
                FolderTreeView.ItemsSource = _rootFolders;

                if (prevId != null)
                {
                    _selectedVirtualFolder = FindNodeInTree(_rootFolders, prevId);
                    if (_selectedVirtualFolder != null)
                    {
                        _selectedVirtualFolder.IsSelected = true;
                    }
                }
            }
            finally
            {
                _suppressTreeSelectionEvent = false;
            }

            UpdateActiveFolderUI();
        }

        private bool _suppressTreeSelectionEvent = false;

        private void ClearAllTreeSelection(IEnumerable<VirtualFolderNode>? nodes)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                if (n.IsSelected) n.IsSelected = false;
                if (n.Children != null && n.Children.Count > 0)
                {
                    ClearAllTreeSelection(n.Children);
                }
            }
        }

        private List<VirtualFolderNode> GetAncestorChain(VirtualFolderNode target)
        {
            var chain = new List<VirtualFolderNode>();
            VirtualFolderNode? curr = target;
            int guard = 0;
            while (curr != null && guard++ < 32)
            {
                chain.Add(curr);
                if (string.IsNullOrEmpty(curr.ParentId)) break;
                curr = FindNodeInTree(_rootFolders, curr.ParentId);
            }
            chain.Reverse();
            return chain;
        }

        private void NavigateToVirtualFolder(VirtualFolderNode? node)
        {
            _suppressTreeSelectionEvent = true;
            try
            {
                ClearAllTreeSelection(_rootFolders);
                _selectedVirtualFolder = node;
                _filterUntaggedOnly = false;

                if (node != null)
                {
                    var chain = GetAncestorChain(node);
                    foreach (var anc in chain)
                    {
                        if (anc.Id != node.Id && !anc.IsExpanded)
                        {
                            anc.IsExpanded = true;
                            _dataManager.SetVirtualFolderOpenState(anc.Id, true);
                        }
                    }

                    if (node.HasChildren && !node.IsExpanded)
                    {
                        node.IsExpanded = true;
                        _dataManager.SetVirtualFolderOpenState(node.Id, true);
                    }

                    node.IsSelected = true;
                }
            }
            finally
            {
                _suppressTreeSelectionEvent = false;
            }

            UpdateActiveFolderUI();
            ApplyFilter(autoSwitchViewMode: true);
        }

        private bool _suppressNextSubfolderClick = false;
        private VirtualFolderNode? _lastClickedSubfolderCard = null;

        private List<VirtualFolderNode> GetSelectedSubfolderCards()
        {
            if (_currentVisibleSubfolders == null || _currentVisibleSubfolders.Count == 0)
                return new List<VirtualFolderNode>();
            return _currentVisibleSubfolders.Where(x => x.IsCardSelected).ToList();
        }

        private void ClearSubfolderCardSelection()
        {
            if (_currentVisibleSubfolders != null)
            {
                foreach (var n in _currentVisibleSubfolders)
                {
                    n.IsCardSelected = false;
                }
            }
            UpdateSelectionActionToolbarUI();
        }

        private void UpdateSelectionActionToolbarUI()
        {
            var selectedFolderCards = GetSelectedSubfolderCards();
            var selectedMedia = GetSelectedSfxList();

            if (AddSelectionToBinBtn != null)
            {
                AddSelectionToBinBtn.Visibility = selectedMedia.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RemoveSelectionFromBinBtn != null)
            {
                if (selectedFolderCards.Count > 0)
                {
                    RemoveSelectionFromBinBtn.Visibility = Visibility.Visible;
                    if (RemoveSelectionFromBinText != null)
                        RemoveSelectionFromBinText.Text = $"Xóa {selectedFolderCards.Count:N0} thư mục";
                }
                else if (selectedMedia.Count > 0)
                {
                    RemoveSelectionFromBinBtn.Visibility = Visibility.Visible;
                    if (RemoveSelectionFromBinText != null)
                    {
                        RemoveSelectionFromBinText.Text = _selectedVirtualFolder != null
                            ? $"Xóa {selectedMedia.Count:N0} file khỏi Bin"
                            : $"Xóa {selectedMedia.Count:N0} file";
                    }
                }
                else
                {
                    RemoveSelectionFromBinBtn.Visibility = Visibility.Collapsed;
                }
            }

            if (SelectAllBtn != null)
            {
                int totalSubfolders = _currentVisibleSubfolders?.Count ?? 0;
                int totalMedia = _visibleFiles?.Count ?? 0;

                if (totalSubfolders == 0 && totalMedia == 0)
                {
                    SelectAllBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SelectAllBtn.Visibility = Visibility.Visible;
                    bool allFoldersSelected = totalSubfolders > 0 && selectedFolderCards.Count == totalSubfolders;
                    int maxMediaSelectable = _isThumbnailViewMode && SfxThumbnailListBox?.ItemsSource is System.Collections.ICollection col
                        ? col.Count
                        : totalMedia;
                    bool allMediaSelected = maxMediaSelectable > 0 && selectedMedia.Count >= maxMediaSelectable;

                    bool isAllSelected = (totalMedia == 0 && allFoldersSelected) ||
                                         (totalMedia > 0 && (allMediaSelected || allFoldersSelected));

                    if (SelectAllBtnText != null)
                    {
                        SelectAllBtnText.Text = isAllSelected ? "Bỏ chọn tất cả" : "Chọn tất cả";
                    }
                    if (SelectAllCheckMark != null)
                    {
                        SelectAllCheckMark.Visibility = isAllSelected ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (SelectAllCheckBoxBorder != null)
                    {
                        SelectAllCheckBoxBorder.Background = isAllSelected
                            ? new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48))
                            : new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14));
                        SelectAllCheckBoxBorder.BorderBrush = isAllSelected
                            ? new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85))
                            : new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x46));
                    }
                    SelectAllBtn.Background = isAllSelected
                        ? new SolidColorBrush(Color.FromRgb(0x22, 0x14, 0x19))
                        : new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1D));
                    SelectAllBtn.BorderBrush = isAllSelected
                        ? new SolidColorBrush(Color.FromRgb(0x6B, 0x1D, 0x2F))
                        : new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x32));
                    SelectAllBtn.Foreground = isAllSelected
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8));
                }
            }

            UpdateMiddleToolbarResponsiveLayout();
        }

        private bool _isMiddleToolbarInWideMode = true;

        private void MiddleToolbarBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMiddleToolbarResponsiveLayout(e.NewSize.Width);
        }

        private void UpdateMiddleToolbarResponsiveLayout(double availableWidth = 0)
        {
            if (MiddleToolbarGrid == null || WideActionsHost == null || NarrowActionsHost == null || ToolbarActionsPanel == null)
                return;

            if (availableWidth <= 0)
                availableWidth = MiddleToolbarBorder?.ActualWidth ?? 0;

            if (availableWidth <= 0) return;

            // Estimate required width for single-line display:
            // 1. Result count pill & filter badge
            double leftStatusWidth = 190;
            if (ActiveFolderPill != null && ActiveFolderPill.Visibility == Visibility.Visible)
            {
                leftStatusWidth += 140;
            }

            // 2. View mode switcher and thumbnail slider
            double rightControlsWidth = 235;
            if (ThumbnailSizeSliderPanel != null && ThumbnailSizeSliderPanel.Visibility == Visibility.Visible)
            {
                rightControlsWidth += 175;
            }

            // 3. Actions panel (Auto preview + Select all + Selection buttons)
            double actionsWidth = 240;
            if (AddSelectionToBinBtn != null && AddSelectionToBinBtn.Visibility == Visibility.Visible)
            {
                actionsWidth += 125;
            }
            if (RemoveSelectionFromBinBtn != null && RemoveSelectionFromBinBtn.Visibility == Visibility.Visible)
            {
                actionsWidth += 140;
            }

            double requiredWidth = leftStatusWidth + rightControlsWidth + actionsWidth + 25;

            // Apply hysteresis to prevent toggling flicker at boundary
            bool shouldBeWide = _isMiddleToolbarInWideMode 
                ? availableWidth >= requiredWidth 
                : availableWidth >= (requiredWidth + 15);

            if (shouldBeWide)
            {
                _isMiddleToolbarInWideMode = true;
                if (NarrowActionsHost.Content != null)
                {
                    NarrowActionsHost.Content = null;
                }
                NarrowActionsHost.Visibility = Visibility.Collapsed;
                if (NarrowActionsRow != null)
                {
                    NarrowActionsRow.Height = new GridLength(0);
                }

                if (WideActionsHost.Content != ToolbarActionsPanel)
                {
                    ToolbarActionsPanel.Margin = new Thickness(0);
                    if (ToolbarDivider != null) ToolbarDivider.Visibility = Visibility.Visible;
                    WideActionsHost.Content = ToolbarActionsPanel;
                }
                WideActionsHost.Visibility = Visibility.Visible;
            }
            else
            {
                _isMiddleToolbarInWideMode = false;
                if (WideActionsHost.Content != null)
                {
                    WideActionsHost.Content = null;
                }
                WideActionsHost.Visibility = Visibility.Collapsed;

                if (NarrowActionsHost.Content != ToolbarActionsPanel)
                {
                    ToolbarActionsPanel.Margin = new Thickness(0, 4, 0, 0);
                    if (ToolbarDivider != null) ToolbarDivider.Visibility = Visibility.Collapsed;
                    NarrowActionsHost.Content = ToolbarActionsPanel;
                }
                NarrowActionsHost.Visibility = Visibility.Visible;
                if (NarrowActionsRow != null)
                {
                    NarrowActionsRow.Height = GridLength.Auto;
                }
            }
        }

        private void SubfolderCard_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressNextSubfolderClick)
            {
                _suppressNextSubfolderClick = false;
                e.Handled = true;
                return;
            }

            if (sender is FrameworkElement fe && fe.DataContext is VirtualFolderNode node)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                if (shift && _currentVisibleSubfolders.Count > 0)
                {
                    int targetIdx = _currentVisibleSubfolders.IndexOf(node);
                    int anchorIdx = _lastClickedSubfolderCard != null
                        ? _currentVisibleSubfolders.IndexOf(_lastClickedSubfolderCard)
                        : 0;
                    if (anchorIdx < 0) anchorIdx = 0;
                    if (targetIdx >= 0)
                    {
                        int min = Math.Min(anchorIdx, targetIdx);
                        int max = Math.Max(anchorIdx, targetIdx);
                        if (!ctrl)
                        {
                            foreach (var c in _currentVisibleSubfolders) c.IsCardSelected = false;
                        }
                        for (int i = min; i <= max; i++)
                        {
                            _currentVisibleSubfolders[i].IsCardSelected = true;
                        }
                        UpdateSelectionActionToolbarUI();
                        return;
                    }
                }
                else if (ctrl)
                {
                    node.IsCardSelected = !node.IsCardSelected;
                    _lastClickedSubfolderCard = node;
                    UpdateSelectionActionToolbarUI();
                    return;
                }

                ClearSubfolderCardSelection();
                NavigateToVirtualFolder(node);
            }
        }

        private void FolderBackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedVirtualFolder == null) return;

            if (!string.IsNullOrEmpty(_selectedVirtualFolder.ParentId))
            {
                var parentNode = FindNodeInTree(_rootFolders, _selectedVirtualFolder.ParentId);
                NavigateToVirtualFolder(parentNode);
            }
            else
            {
                NavigateToVirtualFolder(null);
            }
        }

        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressTreeSelectionEvent) return;

            if (FolderTreeView.SelectedItem is VirtualFolderNode node)
            {
                _selectedVirtualFolder = node;
                if (node.HasChildren && !node.IsExpanded)
                {
                    node.IsExpanded = true;
                    _dataManager.SetVirtualFolderOpenState(node.Id, true);
                }
                UpdateActiveFolderUI();
                ApplyFilter(autoSwitchViewMode: true);
            }
        }

        private void IncludeSubfoldersCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool includeSubs = IncludeSubfoldersCheck.IsChecked == true;
            void UpdateNodes(IEnumerable<VirtualFolderNode> nodes)
            {
                foreach (var n in nodes)
                {
                    n.IncludeSubfoldersInCount = includeSubs;
                    if (n.Children != null && n.Children.Count > 0)
                    {
                        UpdateNodes(n.Children);
                    }
                }
            }
            if (_rootFolders != null)
            {
                UpdateNodes(_rootFolders);
            }
            UpdateActiveFolderUI();
            ApplyFilter(autoSwitchViewMode: true);
        }

        private void ClearActiveFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            NavigateToVirtualFolder(null);
        }

        private void UpdateActiveFolderUI()
        {
            if (_selectedVirtualFolder != null)
            {
                bool includeSubs = IncludeSubfoldersCheck?.IsChecked == true;
                int folderCount = (includeSubs || _selectedVirtualFolder.DirectCount == 0)
                    ? _selectedVirtualFolder.TotalCount
                    : _selectedVirtualFolder.DirectCount;
                ActiveFolderPill.Visibility = Visibility.Visible;
                ActiveFolderNameText.Text = $"{_selectedVirtualFolder.Name} ({folderCount:N0})";
                AllSfxFolderBtn.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1C));
                AllSfxFolderBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x32));
            }
            else
            {
                ActiveFolderPill.Visibility = Visibility.Collapsed;
                AllSfxFolderBtn.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x15, 0x19));
                AllSfxFolderBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x13, 0x37));
            }

            UpdateSelectionActionToolbarUI();
        }

        private void AllSfxFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            NavigateToVirtualFolder(null);
        }

        private void UntaggedFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            _suppressTreeSelectionEvent = true;
            ClearAllTreeSelection(_rootFolders);
            _suppressTreeSelectionEvent = false;

            _filterUntaggedOnly = true;
            _selectedVirtualFolder = null;
            _selectedFilterTags.Clear();
            UpdateTagChipsSelection();
            UpdateActiveFolderUI();
            ApplyFilter(autoSwitchViewMode: true);
        }

        #region DaVinci Resolve Style Bin Operations & Inline Rename

        private async void CreateBinBtn_Click(object sender, RoutedEventArgs e)
        {
            // Calculate next default bin name: Bin 1, Bin 2, ...
            int counter = 1;
            var existingNames = new HashSet<string>(_dataManager.Settings.VirtualFolders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            while (existingNames.Contains($"Bin {counter}"))
            {
                counter++;
            }
            string newBinName = $"Bin {counter}";

            // Parent is currently selected bin (if any), otherwise root
            string? parentId = _selectedVirtualFolder?.Id;

            var newVf = await _dataManager.AddVirtualFolderAsync(newBinName, parentId);
            BuildFolderTree();

            if (!string.IsNullOrEmpty(parentId))
            {
                var parentNode = FindNodeInTree(_rootFolders, parentId);
                if (parentNode != null) parentNode.IsExpanded = true;
            }

            var newNode = FindNodeInTree(_rootFolders, newVf.Id);
            if (newNode != null)
            {
                newNode.IsSelected = true;
                _selectedVirtualFolder = newNode;
                UpdateActiveFolderUI();
                ApplyFilter();

                // Focus inline edit immediately
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StartInlineEdit(newNode);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private VirtualFolderNode? GetContextMenuFolderNode(object sender)
        {
            if (sender is FrameworkElement fe && fe.DataContext is VirtualFolderNode n1)
                return n1;

            if (sender is MenuItem mi && mi.Parent is ContextMenu cm)
            {
                if (cm.DataContext is VirtualFolderNode n2)
                    return n2;
                if (cm.PlacementTarget is FrameworkElement pt)
                {
                    if (pt.DataContext is VirtualFolderNode n3)
                        return n3;
                    if (pt.Tag is VirtualFolderNode n4)
                        return n4;
                }
            }

            return FolderTreeView.SelectedItem as VirtualFolderNode ?? _selectedVirtualFolder;
        }

        private async void CreateSubBinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node == null) return;

            int counter = 1;
            var existingNames = new HashSet<string>(_dataManager.Settings.VirtualFolders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            while (existingNames.Contains($"Bin {counter}"))
            {
                counter++;
            }
            string newBinName = $"Bin {counter}";

            var newVf = await _dataManager.AddVirtualFolderAsync(newBinName, node.Id);
            BuildFolderTree();

            var parentNode = FindNodeInTree(_rootFolders, node.Id);
            if (parentNode != null) parentNode.IsExpanded = true;

            var newNode = FindNodeInTree(_rootFolders, newVf.Id);
            if (newNode != null)
            {
                newNode.IsSelected = true;
                _selectedVirtualFolder = newNode;
                UpdateActiveFolderUI();
                ApplyFilter();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StartInlineEdit(newNode);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void RenameBinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node != null)
            {
                StartInlineEdit(node);
            }
        }

        private void OpenFolderLocationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node == null) return;

            string? targetPath = node.LinkedPath;
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                string? sampleFilePathNorm = node.DirectFiles?.FirstOrDefault() ?? node.AllDescendantFiles?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sampleFilePathNorm))
                {
                    var sampleFile = _allFiles.FirstOrDefault(f => string.Equals(f.FilePathNorm, sampleFilePathNorm, StringComparison.OrdinalIgnoreCase));
                    if (sampleFile != null && !string.IsNullOrWhiteSpace(sampleFile.FilePath))
                    {
                        targetPath = Path.GetDirectoryName(sampleFile.FilePath);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath))
            {
                OpenItemLocation(targetPath);
            }
            else
            {
                ZDialog.Show($"Thư mục Bin \"{node.Name}\" là thư mục ảo và chưa liên kết với thư mục ổ đĩa vật lý nào.", "Thông báo", ZDialogKind.Info, owner: this);
            }
        }

        private void LinkBinFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node != null)
            {
                OpenBinLinkModal(node);
            }
        }

        private async void SyncBinFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node != null)
            {
                await TriggerSingleBinSyncAsync(node, silent: false);
            }
        }

        private async void UnlinkBinFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node == null) return;

            int linkedCount = _dataManager.CountLinkedFoldersInSubtree(node.Id);
            if (linkedCount == 0)
            {
                ZDialog.Show($"Thư mục Bin \"{node.Name}\" hiện chưa có liên kết thư mục ổ đĩa nào.", "Hủy liên kết", ZDialogKind.Info, owner: this);
                return;
            }

            string confirmMsg = linkedCount > 1
                ? $"Bạn có muốn hủy liên kết thư mục ổ đĩa (Auto-Update) cho \"{node.Name}\" (bao gồm {linkedCount:N0} thư mục liên kết bên trong) không?\n\nCác thư mục Bin và toàn bộ file media vẫn được giữ nguyên trong thư viện."
                : $"Bạn có muốn hủy liên kết thư mục ổ đĩa (Auto-Update) cho Bin \"{node.Name}\" không?\n\nThư mục Bin và toàn bộ file media vẫn được giữ nguyên trong thư viện.";

            if (ZDialog.Confirm(confirmMsg, "Hủy liên kết ổ đĩa", ZDialogKind.Warning, "Hủy liên kết", "Đóng", this))
            {
                await _dataManager.UnlinkVirtualFolderAsync(node.Id, includeDescendants: true);
                BuildFolderTree();
                ApplyFilter();
                SetupLinkedFolderWatchers();
            }
        }

        private void BinRowLinkBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is VirtualFolderNode node)
            {
                OpenBinLinkModal(node);
            }
        }

        private async void BinRowSyncBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is VirtualFolderNode node)
            {
                await TriggerSingleBinSyncAsync(node, silent: false);
            }
        }

        private void OpenBinLinkModal(VirtualFolderNode node)
        {
            _editingLinkFolderId = node.Id;
            var vf = _dataManager.Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));

            BinLinkModalNameInput.Text = vf?.Name ?? node.Name;
            BinLinkModalPathInput.Text = vf?.LinkedPath ?? node.LinkedPath ?? string.Empty;
            BinLinkModalAutoSyncCheck.IsChecked = vf?.AutoSync ?? node.AutoSync;
            UpdateBinLinkModalPathUI();

            BinLinkModal.Visibility = Visibility.Visible;
        }

        private void UpdateBinLinkModalPathUI()
        {
            bool hasPath = !string.IsNullOrWhiteSpace(BinLinkModalPathInput.Text);
            bool hasAnySubtreeLink = !string.IsNullOrEmpty(_editingLinkFolderId) && _dataManager.CountLinkedFoldersInSubtree(_editingLinkFolderId) > 0;
            if (BinLinkModalPathPlaceholder != null)
            {
                BinLinkModalPathPlaceholder.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ClearBinLinkPathBtn != null)
            {
                ClearBinLinkPathBtn.Visibility = (hasPath || hasAnySubtreeLink) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (SyncNowFromModalBtn != null)
            {
                SyncNowFromModalBtn.IsEnabled = hasPath;
                SyncNowFromModalBtn.Opacity = hasPath ? 1.0 : 0.5;
            }
        }

        private void BinLinkModalPathInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateBinLinkModalPathUI();
        }

        private async void ClearBinLinkPathBtn_Click(object sender, RoutedEventArgs e)
        {
            BinLinkModalPathInput.Text = string.Empty;
            BinLinkModalAutoSyncCheck.IsChecked = false;
            if (!string.IsNullOrEmpty(_editingLinkFolderId))
            {
                string folderId = _editingLinkFolderId;
                string newName = BinLinkModalNameInput.Text.Trim();
                await _dataManager.UpdateVirtualFolderLinkAsync(folderId, newName, null, false);
                await _dataManager.UnlinkVirtualFolderAsync(folderId, includeDescendants: true);
                BuildFolderTree();
                ApplyFilter();
                SetupLinkedFolderWatchers();
                BinLinkModal.Visibility = Visibility.Collapsed;
                _editingLinkFolderId = null;
                return;
            }
            UpdateBinLinkModalPathUI();
        }

        private void BrowseBinLinkPathBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Chọn thư mục trên ổ đĩa để liên kết tự động cập nhật (Auto-Sync)",
                Multiselect = false
            };

            string currentPath = BinLinkModalPathInput.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                BinLinkModalPathInput.Text = dialog.FolderName;
                BinLinkModalAutoSyncCheck.IsChecked = true;
                UpdateBinLinkModalPathUI();
            }
        }

        private void CloseBinLinkModal_Click(object sender, RoutedEventArgs e)
        {
            BinLinkModal.Visibility = Visibility.Collapsed;
            _editingLinkFolderId = null;
        }

        private async void SyncNowFromModalBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_editingLinkFolderId)) return;

            string newName = BinLinkModalNameInput.Text.Trim();
            string linkedPath = BinLinkModalPathInput.Text.Trim();
            bool autoSync = BinLinkModalAutoSyncCheck.IsChecked == true;

            if (string.IsNullOrWhiteSpace(linkedPath) || !Directory.Exists(linkedPath))
            {
                ZDialog.Show("Thư mục ổ đĩa chưa được chọn hoặc không tồn tại trên máy!", "Không tìm thấy thư mục", ZDialogKind.Warning, owner: this);
                return;
            }

            string folderId = _editingLinkFolderId;
            await _dataManager.UpdateVirtualFolderLinkAsync(folderId, newName, linkedPath, autoSync);
            var res = await _dataManager.SyncVirtualFolderAsync(folderId, _allFiles);

            AllCountBadge.Text = _allFiles.Count.ToString("N0");
            UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
            BuildTagChips();
            BuildFolderTree();
            ApplyFilter();
            SetupLinkedFolderWatchers();

            BinLinkModal.Visibility = Visibility.Collapsed;
            _editingLinkFolderId = null;

            ZDialog.Show(
                res.AddedFiles > 0 || res.NewSubBins > 0
                    ? $"Đã đồng bộ thành công!\n• Thêm mới: {res.AddedFiles:N0} file media\n• Tạo mới: {res.NewSubBins:N0} Bin con"
                    : $"Đã kiểm tra thư mục liên kết:\nTất cả file trong \"{Path.GetFileName(linkedPath)}\" đều đã được cập nhật đầy đủ!",
                "Đồng bộ Thư mục Liên kết",
                ZDialogKind.Success,
                owner: this);
        }

        private async void SaveBinLinkModalBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_editingLinkFolderId)) return;

            string folderId = _editingLinkFolderId;
            string newName = BinLinkModalNameInput.Text.Trim();
            string linkedPath = BinLinkModalPathInput.Text.Trim();
            bool autoSync = BinLinkModalAutoSyncCheck.IsChecked == true;

            if (!string.IsNullOrWhiteSpace(linkedPath) && !Directory.Exists(linkedPath))
            {
                ZDialog.Show("Đường dẫn thư mục ổ đĩa không tồn tại! Vui lòng kiểm tra lại hoặc bấm 'Chọn thư mục...'.", "Đường dẫn không hợp lệ", ZDialogKind.Warning, owner: this);
                return;
            }

            await _dataManager.UpdateVirtualFolderLinkAsync(folderId, newName, linkedPath, autoSync);

            if (string.IsNullOrWhiteSpace(linkedPath))
            {
                await _dataManager.UnlinkVirtualFolderAsync(folderId, includeDescendants: true);
            }
            else if (Directory.Exists(linkedPath))
            {
                await _dataManager.SyncVirtualFolderAsync(folderId, _allFiles);
            }

            AllCountBadge.Text = _allFiles.Count.ToString("N0");
            UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
            BuildTagChips();
            BuildFolderTree();
            ApplyFilter();
            SetupLinkedFolderWatchers();

            BinLinkModal.Visibility = Visibility.Collapsed;
            _editingLinkFolderId = null;
        }

        private async Task TriggerSingleBinSyncAsync(VirtualFolderNode node, bool silent)
        {
            if (string.IsNullOrWhiteSpace(node.LinkedPath))
            {
                if (!silent)
                {
                    OpenBinLinkModal(node);
                }
                return;
            }

            if (!Directory.Exists(node.LinkedPath))
            {
                if (!silent)
                {
                    ZDialog.Show($"Thư mục liên kết không tồn tại trên ổ đĩa:\n{node.LinkedPath}\n\nBạn có thể chọn lại đường dẫn thư mục.", "Không tìm thấy thư mục", ZDialogKind.Warning, owner: this);
                    OpenBinLinkModal(node);
                }
                return;
            }

            var res = await _dataManager.SyncVirtualFolderAsync(node.Id, _allFiles);
            AllCountBadge.Text = _allFiles.Count.ToString("N0");
            UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
            BuildTagChips();
            BuildFolderTree();
            ApplyFilter();

            if (!silent)
            {
                string msg = (res.AddedFiles > 0 || res.NewSubBins > 0 || res.RemovedFiles > 0)
                    ? $"Đã cập nhật Bin \"{node.Name}\":\n• File mới thêm vào: {res.AddedFiles:N0}\n• Bin con mới: {res.NewSubBins:N0}\n• File đã dọn dẹp: {res.RemovedFiles:N0}"
                    : $"Bin \"{node.Name}\" đã đồng bộ đầy đủ với ổ đĩa ({node.CountDisplay} media). Không có file mới phát sinh.";
                ZDialog.Show(msg, "Đồng bộ Thư mục Liên kết", ZDialogKind.Success, owner: this);
            }
        }

        private System.Windows.Threading.DispatcherTimer? _tagSyncFileDebounceTimer;

        private void SetupLinkedFolderWatchers()
        {
            DisposeFolderWatchers();

            // Only watch Dropbox master sync file (sfx-settings-sync.json).
            // Background physical disk auto-scanning is disabled per user request:
            // "không cần cái auto synce quét lại ổ đĩa làm chi, chỉ sync khi nào tôi import folder vào thôi"

            // Also watch sfx-settings-sync.json so changes from another PC via Dropbox auto-sync in real time!
            try
            {
                string syncFile = DataManager.DropboxSyncFile;
                string? syncDir = Path.GetDirectoryName(syncFile);
                string syncName = Path.GetFileName(syncFile);
                if (!string.IsNullOrWhiteSpace(syncDir) && Directory.Exists(syncDir) && !string.IsNullOrWhiteSpace(syncName))
                {
                    var tagWatcher = new FileSystemWatcher(syncDir, syncName)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                    };
                    tagWatcher.Changed += OnTagSyncFileChangedFromDropbox;
                    tagWatcher.Created += OnTagSyncFileChangedFromDropbox;
                    tagWatcher.Renamed += OnTagSyncFileChangedFromDropbox;
                    tagWatcher.EnableRaisingEvents = true;
                    _folderWatchers.Add(tagWatcher);
                }
            }
            catch { }
        }

        private void OnTagSyncFileChangedFromDropbox(object sender, FileSystemEventArgs e)
        {
            // Ignore self-writes within 3.5 seconds of local SaveSettingsAsync
            if ((DateTime.UtcNow - _dataManager.LastLocalSaveUtc).TotalSeconds < 3.5)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_tagSyncFileDebounceTimer == null)
                {
                    _tagSyncFileDebounceTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1200)
                    };
                    _tagSyncFileDebounceTimer.Tick += async (_, _) =>
                    {
                        _tagSyncFileDebounceTimer.Stop();
                        if ((DateTime.UtcNow - _dataManager.LastLocalSaveUtc).TotalSeconds < 3.5)
                            return;

                        try
                        {
                            bool ok = await _dataManager.SyncWithDropboxMasterAsync(_allFiles);
                            if (ok)
                            {
                                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                                BuildTagChips();
                                BuildQuickAvailableTags();
                                BuildFolderTree();
                                if (TagManageModal != null && TagManageModal.Visibility == Visibility.Visible)
                                {
                                    RefreshModalTagList();
                                }
                                ApplyFilter();
                            }
                        }
                        catch { }
                    };
                }

                _tagSyncFileDebounceTimer.Stop();
                _tagSyncFileDebounceTimer.Start();
            }));
        }

        private void DisposeFolderWatchers()
        {
            foreach (var w in _folderWatchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            _folderWatchers.Clear();
        }



        private async void DeleteBinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var node = GetContextMenuFolderNode(sender);
            if (node == null) return;

            var selectedCards = GetSelectedSubfolderCards();
            if (node.IsCardSelected && selectedCards.Count > 1)
            {
                await DeleteFolderNodesWithConfirmAsync(selectedCards);
            }
            else
            {
                await DeleteFolderNodeWithConfirmAsync(node);
            }
        }

        private Task DeleteFolderNodeWithConfirmAsync(VirtualFolderNode node)
        {
            return DeleteFolderNodesWithConfirmAsync(new[] { node });
        }

        private async Task DeleteFolderNodesWithConfirmAsync(IReadOnlyList<VirtualFolderNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;

            string confirmMsg = nodes.Count == 1
                ? $"Bạn có chắc chắn muốn xóa vĩnh viễn thư mục Bin \"{nodes[0].Name}\" khỏi phần mềm không?\n\n(Lưu ý: Bin này sẽ bị xóa hoàn toàn khỏi thư viện Z-Station và không tự khôi phục lại. File gốc trên ổ cứng vẫn an toàn.)"
                : $"Bạn có chắc chắn muốn xóa vĩnh viễn {nodes.Count:N0} thư mục Bin đang chọn khỏi phần mềm không?\n\nDanh sách: {string.Join(", ", nodes.Take(6).Select(n => $"\"{n.Name}\""))}{(nodes.Count > 6 ? "..." : "")}\n\n(Lưu ý: Các Bin này sẽ bị xóa hoàn toàn khỏi thư viện Z-Station và không tự khôi phục lại. File gốc trên ổ cứng vẫn an toàn.)";

            string confirmTitle = nodes.Count == 1 ? "Xóa vĩnh viễn Bin" : $"Xóa vĩnh viễn {nodes.Count:N0} Bin";

            if (ZDialog.Confirm(confirmMsg, confirmTitle, ZDialogKind.Danger, "Xóa vĩnh viễn", "Hủy", this))
            {
                string? fallbackParentId = nodes[0].ParentId;
                bool wasSelectedOrInside = _selectedVirtualFolder != null &&
                    nodes.Any(node =>
                        _selectedVirtualFolder.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase) ||
                        _dataManager.IsDescendantOf(_selectedVirtualFolder.Id, node.Id));

                await _dataManager.DeleteVirtualFoldersAsync(nodes.Select(n => n.Id), _allFiles);
                _ = _dataManager.SaveLibraryIndexCacheAsync(_allFiles);

                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                BuildTagChips();
                BuildFolderTree();

                if (wasSelectedOrInside)
                {
                    _selectedVirtualFolder = !string.IsNullOrEmpty(fallbackParentId)
                        ? FindNodeInTree(_rootFolders, fallbackParentId)
                        : null;
                    if (_selectedVirtualFolder != null)
                    {
                        _selectedVirtualFolder.IsSelected = true;
                    }
                    UpdateActiveFolderUI();
                }

                ApplyFilter();
                SetupLinkedFolderWatchers();
            }
        }

        private async Task DeleteCurrentSelectionWithConfirmAsync()
        {
            var selectedCards = GetSelectedSubfolderCards();
            var selectedMedia = GetSelectedSfxList();

            if (selectedCards.Count > 0)
            {
                await DeleteFolderNodesWithConfirmAsync(selectedCards);
                return;
            }

            if (selectedMedia.Count > 0)
            {
                string targetScopeText = _selectedVirtualFolder != null
                    ? $"khỏi Bin \"{_selectedVirtualFolder.Name}\" (và thư viện Z-Station)"
                    : "khỏi thư viện Z-Station";

                string msg = selectedMedia.Count == 1
                    ? $"Bạn có chắc chắn muốn xóa media \"{selectedMedia[0].FileName}\" {targetScopeText} không?\n\n(Lưu ý: File gốc trên ổ cứng vẫn an toàn.)"
                    : $"Bạn có chắc chắn muốn xóa {selectedMedia.Count:N0} media đang chọn {targetScopeText} không?\n\n(Lưu ý: File gốc trên ổ cứng vẫn an toàn.)";

                if (ZDialog.Confirm(msg, "Xóa Media đã chọn", ZDialogKind.Danger, "Xóa (Delete)", "Hủy", this))
                {
                    var pathsToDelete = selectedMedia.Select(x => x.FilePath).ToList();
                    await _dataManager.DeleteMediaFilesAsync(pathsToDelete, _allFiles, _selectedVirtualFolder?.Id);
                    _ = _dataManager.SaveLibraryIndexCacheAsync(_allFiles);

                    if (_selectedItem != null && pathsToDelete.Contains(_selectedItem.FilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        _audioPlayer.Stop();
                    }

                    AllCountBadge.Text = _allFiles.Count.ToString("N0");
                    UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                    BuildTagChips();
                    BuildFolderTree();
                    ApplyFilter();
                }
            }
        }

        private void StartInlineEdit(VirtualFolderNode node)
        {
            ClearAllEditing(_rootFolders);
            node.IsEditing = true;
        }

        private void ClearAllEditing(IEnumerable<VirtualFolderNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.IsEditing = false;
                ClearAllEditing(n.Children);
            }
        }

        private async void FolderTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            if (FolderTreeView.SelectedItem is VirtualFolderNode node && !node.IsEditing)
            {
                if (e.Key == Key.F2)
                {
                    e.Handled = true;
                    StartInlineEdit(node);
                }
                else if (e.Key == Key.Delete)
                {
                    e.Handled = true;
                    await DeleteFolderNodeWithConfirmAsync(node);
                }
            }
        }

        private void BinInlineTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.Visibility == Visibility.Visible)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    tb.Focus();
                    tb.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private async void BinInlineTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is VirtualFolderNode node)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await CommitBinRenameAsync(node, tb.Text);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    node.IsEditing = false;
                }
            }
        }

        private async void BinInlineTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is VirtualFolderNode node && node.IsEditing)
            {
                await CommitBinRenameAsync(node, tb.Text);
            }
        }

        private async Task CommitBinRenameAsync(VirtualFolderNode node, string newName)
        {
            node.IsEditing = false;
            newName = newName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
            {
                return;
            }

            node.Name = newName;
            await _dataManager.RenameVirtualFolderAsync(node.Id, newName);
            if (_selectedVirtualFolder?.Id == node.Id)
            {
                UpdateActiveFolderUI();
            }
        }

        #endregion

        private async void ImportFileBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn file Media (Âm thanh, Video, Hình ảnh, Thiết kế) để import vào thư viện",
                Multiselect = true,
                Filter = "Tất cả Media hỗ trợ (*.wav;*.mp3;*.mp4;*.png;*.psd;...)|*.wav;*.mp3;*.flac;*.aiff;*.aif;*.m4a;*.aac;*.ogg;*.wma;*.mid;*.midi;*.mp4;*.mov;*.webm;*.mkv;*.avi;*.m4v;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tiff;*.tif;*.ico;*.svg;*.psd;*.psb;*.ai;*.eps;*.pdf;*.aep;*.prproj;*.mogrt;*.drp;*.dra;*.setting;*.comp;*.blend;*.c4d;*.obj;*.fbx;*.otf;*.ttf|Audio Files (*.wav;*.mp3;*.flac;*.m4a)|*.wav;*.mp3;*.flac;*.aiff;*.aif;*.m4a;*.aac;*.ogg;*.wma;*.mid;*.midi|Video Overlay Files (*.mp4;*.mov;*.webm;*.mkv)|*.mp4;*.mov;*.webm;*.mkv;*.avi;*.m4v;*.gif|Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tiff;*.tif;*.ico;*.svg|Design & Project Files (*.psd;*.ai;*.eps;*.pdf;*.aep;*.prproj)|*.psd;*.psb;*.ai;*.eps;*.pdf;*.aep;*.prproj;*.mogrt;*.drp;*.dra;*.setting;*.comp;*.blend;*.c4d;*.obj;*.fbx;*.otf;*.ttf|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            {
                string? targetBinId = _selectedVirtualFolder?.Id;
                var added = await _dataManager.ImportAudioFilesAsync(dialog.FileNames, _allFiles, targetBinId);
                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                BuildTagChips();
                BuildFolderTree();
                ApplyFilter();

                string binMsg = _selectedVirtualFolder != null ? $" và đã cho vào Bin \"{_selectedVirtualFolder.Name}\"" : "";
                ZDialog.Show($"Đã import thành công {dialog.FileNames.Length} file media vào thư viện{binMsg}!", "Import File", ZDialogKind.Success, owner: this);
            }
        }

        private async void ImportFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Chọn thư mục âm thanh để import thành Bin mới",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true && !string.IsNullOrEmpty(dialog.FolderName))
            {
                string folderPath = dialog.FolderName;

                string? parentBinId = _selectedVirtualFolder?.Id;
                var newBin = await _dataManager.ImportFolderAsBinHierarchyAsync(folderPath, _allFiles, parentBinId);

                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                BuildTagChips();
                BuildFolderTree();

                if (newBin != null)
                {
                    VirtualFolderNode? FindNode(IEnumerable<VirtualFolderNode> list, string id)
                    {
                        foreach (var n in list)
                        {
                            if (n.Id == id) return n;
                            var f = FindNode(n.Children, id);
                            if (f != null) return f;
                        }
                        return null;
                    }
                    _selectedVirtualFolder = FindNode(_rootFolders, newBin.Id);
                }
                UpdateActiveFolderUI();
                ApplyFilter();
                SetupLinkedFolderWatchers();

                ZDialog.Show($"Đã import thư mục \"{Path.GetFileName(folderPath)}\" thành công thành Bin mới (đã bật Auto-Sync)!", "Import Thư Mục", ZDialogKind.Success, owner: this);
            }
        }

        private bool _isInternalFileDrag = false;

        private void ShowExternalDropOverlay(string targetDescription)
        {
            if (_isInternalFileDrag) return;
            if (ExternalDropOverlayBorder != null)
            {
                ExternalDropOverlayBorder.Visibility = Visibility.Visible;
            }
            if (ExternalDropOverlayTargetText != null)
            {
                ExternalDropOverlayTargetText.Text = targetDescription;
            }
        }

        private void HideExternalDropOverlay()
        {
            if (ExternalDropOverlayBorder != null)
            {
                ExternalDropOverlayBorder.Visibility = Visibility.Collapsed;
            }
        }

        private async Task ExecuteExternalDropImportAsync(string[]? dropped, string? targetParentBinId)
        {
            if (dropped == null || dropped.Length == 0) return;

            var filesToImport = new List<string>();
            var dirsToImport = new List<string>();

            foreach (var p in dropped)
            {
                if (Directory.Exists(p)) dirsToImport.Add(p);
                else if (File.Exists(p)) filesToImport.Add(p);
            }

            if (dirsToImport.Count == 0 && filesToImport.Count == 0) return;

            VirtualFolder? lastImportedBin = null;
            foreach (var dir in dirsToImport)
            {
                var created = await _dataManager.ImportFolderAsBinHierarchyAsync(dir, _allFiles, targetParentBinId);
                if (created != null) lastImportedBin = created;
            }

            if (filesToImport.Count > 0)
            {
                await _dataManager.ImportAudioFilesAsync(filesToImport, _allFiles, targetParentBinId);
                if (!string.IsNullOrEmpty(targetParentBinId))
                {
                    await _dataManager.AddFilesToVirtualFolderAsync(targetParentBinId, filesToImport);
                }
            }

            AllCountBadge.Text = _allFiles.Count.ToString("N0");
            UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
            BuildTagChips();
            BuildFolderTree();

            if (!string.IsNullOrEmpty(targetParentBinId))
            {
                var parentNode = FindNodeInTree(_rootFolders, targetParentBinId);
                if (parentNode != null) parentNode.IsExpanded = true;
            }

            if (lastImportedBin != null)
            {
                var newNode = FindNodeInTree(_rootFolders, lastImportedBin.Id);
                if (newNode != null)
                {
                    newNode.IsExpanded = true;
                    newNode.IsSelected = true;
                    _selectedVirtualFolder = newNode;
                }
            }
            else if (!string.IsNullOrEmpty(targetParentBinId))
            {
                var targetNode = FindNodeInTree(_rootFolders, targetParentBinId);
                if (targetNode != null)
                {
                    targetNode.IsSelected = true;
                    _selectedVirtualFolder = targetNode;
                }
            }

            UpdateActiveFolderUI();
            ApplyFilter();
            SetupLinkedFolderWatchers();

            if (_visibleFiles.Count > 0)
            {
                if (_visibleFiles.All(x => x.IsVideo) && !_isThumbnailViewMode)
                {
                    SetViewMode(true);
                }
                else if (_visibleFiles.All(x => !x.IsVideo) && _isThumbnailViewMode)
                {
                    SetViewMode(false);
                }
            }
        }

        private void MainWindow_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (!_isInternalFileDrag)
                {
                    string targetName = _selectedVirtualFolder != null
                        ? $"Bin \"{_selectedVirtualFolder.Name}\""
                        : "Thư viện gốc (Root Bin)";
                    ShowExternalDropOverlay($"Giữ nguyên 100% cấu trúc thư mục con • Đích: {targetName}");
                }
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void MainWindow_DragLeave(object sender, DragEventArgs e)
        {
            HideExternalDropOverlay();
        }

        private async void MainWindow_Drop(object sender, DragEventArgs e)
        {
            HideExternalDropOverlay();
            ClearAllDropTargets(_rootFolders);

            if (_isInternalFileDrag) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Handled = true;
            string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            await ExecuteExternalDropImportAsync(dropped, _selectedVirtualFolder?.Id);
        }

        #region TreeView Drag-and-Drop Reparenting & File Drop

        private void FolderTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _potentialDragNode = null;
            _mouseDownNodeWasAlreadySelected = false;

            if (e.OriginalSource is DependencyObject orig)
            {
                var parentTb = FindVisualParent<TextBox>(orig);
                if (parentTb != null) return;

                var parentBtn = FindVisualParent<Button>(orig);
                if (parentBtn != null) return;

                var parentToggle = FindVisualParent<System.Windows.Controls.Primitives.ToggleButton>(orig);
                if (parentToggle != null) return;
            }

            Point p = e.GetPosition(FolderTreeView);
            var node = GetVirtualFolderNodeUnderPoint(p);
            if (node != null && !node.IsEditing)
            {
                _treeDragStartPoint = p;
                _potentialDragNode = node;
                _mouseDownNodeWasAlreadySelected = (_selectedVirtualFolder == node);
            }

            if (e.ClickCount > 1)
            {
                e.Handled = true; // Prevent default TreeViewItem double-click from interfering
            }
        }

        private void FolderTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _potentialDragNode != null && !_potentialDragNode.IsEditing)
            {
                Point p = e.GetPosition(FolderTreeView);
                Vector diff = _treeDragStartPoint - p;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var nodeToDrag = _potentialDragNode;
                    _potentialDragNode = null;

                    var data = new DataObject("ZStation_VirtualFolder", nodeToDrag.Id);
                    DragDrop.DoDragDrop(FolderTreeView, data, DragDropEffects.Move);
                    ClearAllDropTargets(_rootFolders);
                }
            }
        }

        private void FolderTreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_potentialDragNode != null)
            {
                var clickedNode = _potentialDragNode;
                _potentialDragNode = null;

                if (e.OriginalSource is DependencyObject orig)
                {
                    if (FindVisualParent<TextBox>(orig) != null ||
                        FindVisualParent<Button>(orig) != null ||
                        FindVisualParent<System.Windows.Controls.Primitives.ToggleButton>(orig) != null)
                        return;
                }

                Point pUp = e.GetPosition(FolderTreeView);
                Vector diff = _treeDragStartPoint - pUp;
                if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
                {
                    var upNode = GetVirtualFolderNodeUnderPoint(pUp);
                    if (upNode == clickedNode && _mouseDownNodeWasAlreadySelected && clickedNode.HasChildren)
                    {
                        clickedNode.IsExpanded = !clickedNode.IsExpanded;
                        _dataManager.SetVirtualFolderOpenState(clickedNode.Id, clickedNode.IsExpanded);
                    }
                }
            }
        }

        private void FolderTreeView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("ZStation_VirtualFolder"))
            {
                string draggedId = (string)e.Data.GetData("ZStation_VirtualFolder");
                Point p = e.GetPosition(FolderTreeView);
                var targetNode = GetVirtualFolderNodeUnderPoint(p);

                ClearAllDropTargets(_rootFolders);

                if (targetNode == null)
                {
                    var vf = _dataManager.Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(draggedId, StringComparison.OrdinalIgnoreCase));
                    if (vf != null && !string.IsNullOrEmpty(vf.ParentId))
                    {
                        e.Effects = DragDropEffects.Move;
                    }
                    else
                    {
                        e.Effects = DragDropEffects.None;
                    }
                }
                else
                {
                    var vf = _dataManager.Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(draggedId, StringComparison.OrdinalIgnoreCase));
                    if (targetNode.Id.Equals(draggedId, StringComparison.OrdinalIgnoreCase) ||
                        (vf != null && string.Equals(vf.ParentId, targetNode.Id, StringComparison.OrdinalIgnoreCase)) ||
                        _dataManager.IsDescendantOf(targetNode.Id, draggedId))
                    {
                        e.Effects = DragDropEffects.None;
                    }
                    else
                    {
                        e.Effects = DragDropEffects.Move;
                        targetNode.IsDropTarget = true;
                    }
                }
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (_isInternalFileDrag)
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                Point p = e.GetPosition(FolderTreeView);
                var targetNode = GetVirtualFolderNodeUnderPoint(p);

                ClearAllDropTargets(_rootFolders);
                if (targetNode != null)
                {
                    targetNode.IsDropTarget = true;
                    ShowExternalDropOverlay($"Thả để Import giữ nguyên cấu trúc vào Bin: \"{targetNode.Name}\"");
                }
                else
                {
                    ShowExternalDropOverlay("Thả vào cây thư mục để tạo Bin gốc mới (giữ nguyên cấu trúc thư mục con)");
                }

                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void FolderTreeView_DragLeave(object sender, DragEventArgs e)
        {
            ClearAllDropTargets(_rootFolders);
            HideExternalDropOverlay();
        }

        private async void FolderTreeView_Drop(object sender, DragEventArgs e)
        {
            ClearAllDropTargets(_rootFolders);
            HideExternalDropOverlay();

            if (e.Data.GetDataPresent("ZStation_VirtualFolder"))
            {
                string draggedId = (string)e.Data.GetData("ZStation_VirtualFolder");
                Point p = e.GetPosition(FolderTreeView);
                var targetNode = GetVirtualFolderNodeUnderPoint(p);

                string? targetParentId = targetNode?.Id;

                bool moved = await _dataManager.MoveVirtualFolderAsync(draggedId, targetParentId);
                if (moved)
                {
                    BuildFolderTree();
                    if (!string.IsNullOrEmpty(targetParentId))
                    {
                        var pNode = FindNodeInTree(_rootFolders, targetParentId);
                        if (pNode != null) pNode.IsExpanded = true;
                    }
                    var movedNode = FindNodeInTree(_rootFolders, draggedId);
                    if (movedNode != null)
                    {
                        movedNode.IsSelected = true;
                        _selectedVirtualFolder = movedNode;
                    }
                    UpdateActiveFolderUI();
                    ApplyFilter();
                }
                e.Handled = true;
                return;
            }

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0) return;

            e.Handled = true;
            Point pDrop = e.GetPosition(FolderTreeView);
            var fileTarget = GetVirtualFolderNodeUnderPoint(pDrop);

            // If dropped onto a specific Bin node -> import inside that Bin.
            // If dropped onto empty space in the left Bin tree -> import as a Root Bin (parentFolderId = null)!
            await ExecuteExternalDropImportAsync(dropped, fileTarget?.Id);
        }

        private void AllSfxFolderBtn_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("ZStation_VirtualFolder"))
            {
                string draggedId = (string)e.Data.GetData("ZStation_VirtualFolder");
                var vf = _dataManager.Settings.VirtualFolders.FirstOrDefault(f => f.Id.Equals(draggedId, StringComparison.OrdinalIgnoreCase));
                if (vf != null && !string.IsNullOrEmpty(vf.ParentId))
                {
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (_isInternalFileDrag)
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                ClearAllDropTargets(_rootFolders);
                ShowExternalDropOverlay("Thả vào đây để tạo Bin gốc mới (giữ nguyên cấu trúc thư mục con)");
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private async void AllSfxFolderBtn_Drop(object sender, DragEventArgs e)
        {
            HideExternalDropOverlay();
            ClearAllDropTargets(_rootFolders);

            if (e.Data.GetDataPresent("ZStation_VirtualFolder"))
            {
                string draggedId = (string)e.Data.GetData("ZStation_VirtualFolder");
                bool moved = await _dataManager.MoveVirtualFolderAsync(draggedId, null);
                if (moved)
                {
                    BuildFolderTree();
                    var movedNode = FindNodeInTree(_rootFolders, draggedId);
                    if (movedNode != null)
                    {
                        movedNode.IsSelected = true;
                        _selectedVirtualFolder = movedNode;
                    }
                    UpdateActiveFolderUI();
                    ApplyFilter();
                }
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
                await ExecuteExternalDropImportAsync(dropped, null);
            }
        }

        private void ClearAllDropTargets(IEnumerable<VirtualFolderNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.IsDropTarget = false;
                ClearAllDropTargets(n.Children);
            }
        }

        private VirtualFolderNode? FindNodeInTree(IEnumerable<VirtualFolderNode> list, string id)
        {
            foreach (var n in list)
            {
                if (n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return n;
                var f = FindNodeInTree(n.Children, id);
                if (f != null) return f;
            }
            return null;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private VirtualFolderNode? GetVirtualFolderNodeUnderPoint(Point p)
        {
            var hit = FolderTreeView.InputHitTest(p) as DependencyObject;
            while (hit != null && hit != FolderTreeView)
            {
                if (hit is FrameworkElement fe && fe.DataContext is VirtualFolderNode node)
                {
                    return node;
                }
                hit = VisualTreeHelper.GetParent(hit);
            }
            return null;
        }

        #endregion

        private void AddSelectionToBinBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedSfxList();
            if (selected.Count == 0) return;

            PickBinModalDesc.Text = $"Chọn một thư mục Bin để gán {selected.Count:N0} SFX đang chọn vào:";
            PickBinListPanel.Children.Clear();

            if (_dataManager.Settings.VirtualFolders.Count == 0)
            {
                PickBinListPanel.Children.Add(new TextBlock
                {
                    Text = "Chưa có thư mục Bin nào. Hãy bấm '+ Tạo Bin mới' ở dưới để tạo.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    FontSize = 13,
                    Margin = new Thickness(0, 10, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            else
            {
                foreach (var vf in _dataManager.Settings.VirtualFolders.OrderBy(f => f.Name))
                {
                    var btn = new Button
                    {
                        Margin = new Thickness(0, 0, 0, 6),
                        Padding = new Thickness(12, 8, 12, 8),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)),
                        Foreground = Brushes.White,
                        FontSize = 13.5,
                        Tag = vf.Id
                    };
                    var btnSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    btnSp.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconFolder"),
                        Stroke = new SolidColorBrush(Color.FromRgb(0xC5, 0x9B, 0x27)),
                        StrokeThickness = 1.8,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Fill = Brushes.Transparent,
                        Width = 13,
                        Height = 13,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    btnSp.Children.Add(new TextBlock
                    {
                        Text = $"{vf.Name} ({vf.Files.Count} SFX)",
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    btn.Content = btnSp;
                    btn.Click += async (s, args) =>
                    {
                        string targetId = vf.Id;
                        await _dataManager.AddFilesToVirtualFolderAsync(targetId, selected.Select(x => x.FilePath));
                        BuildFolderTree();
                        ApplyFilter();
                        PickBinModal.Visibility = Visibility.Collapsed;
                    };
                    PickBinListPanel.Children.Add(btn);
                }
            }

            PickBinModal.Visibility = Visibility.Visible;
        }

        private void ClosePickBinModal_Click(object sender, RoutedEventArgs e)
        {
            PickBinModal.Visibility = Visibility.Collapsed;
        }

        private void PickBinModalCreateNew_Click(object sender, RoutedEventArgs e)
        {
            PickBinModal.Visibility = Visibility.Collapsed;
            CreateBinBtn_Click(sender, e);
        }

        private async void RemoveSelectionFromBinBtn_Click(object sender, RoutedEventArgs e)
        {
            await DeleteCurrentSelectionWithConfirmAsync();
        }

        private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectAllVisibleItems();
        }

        private void ToggleSelectAllVisibleItems()
        {
            if (_currentVisibleSubfolders != null && _currentVisibleSubfolders.Count > 0)
            {
                bool allCardsSelected = _currentVisibleSubfolders.All(f => f.IsCardSelected);
                foreach (var node in _currentVisibleSubfolders)
                {
                    node.IsCardSelected = !allCardsSelected;
                }
                _lastClickedSubfolderCard = !allCardsSelected ? _currentVisibleSubfolders.LastOrDefault() : null;
                UpdateSelectionActionToolbarUI();
                return;
            }

            if (_visibleFiles != null && _visibleFiles.Count > 0)
            {
                var selectedMedia = GetSelectedSfxList();
                bool allMediaSelected = selectedMedia.Count == _visibleFiles.Count;

                _suppressSelectionAutoPlay = true;
                try
                {
                    if (_isThumbnailViewMode)
                    {
                        if (allMediaSelected) SfxThumbnailListBox?.UnselectAll();
                        else SfxThumbnailListBox?.SelectAll();
                    }
                    else
                    {
                        if (allMediaSelected) SfxListView?.UnselectAll();
                        else SfxListView?.SelectAll();
                    }
                }
                finally
                {
                    _suppressSelectionAutoPlay = false;
                }
                UpdateSelectionActionToolbarUI();
            }
        }

        #endregion

        #region Search & Filtering

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            string query = SearchBox.Text;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilter();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                SearchBox.Text = string.Empty;
                e.Handled = true;
            }
        }

        private void ClearSearchBtn_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        }

        private List<VirtualFolderNode> _currentVisibleSubfolders = new();

        private void ApplyFilter(bool autoSwitchViewMode = false)
        {
            if (!IsLoaded || SearchBox == null || SfxListView == null) return;

            string rawQuery = SearchBox.Text.Trim();
            string[] queryTerms = string.IsNullOrEmpty(rawQuery)
                ? Array.Empty<string>()
                : SearchNormalizer.Normalize(rawQuery).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var wantedNormTags = _selectedFilterTags.Select(SearchNormalizer.Normalize).OrderBy(x => x, StringComparer.Ordinal).ToList();
            bool includeSubs = IncludeSubfoldersCheck.IsChecked == true;
            bool hasSearchOrTagFilter = queryTerms.Length > 0 || wantedNormTags.Count > 0 || _filterUntaggedOnly;

            string cacheKey = $"{_selectedVirtualFolder?.Id ?? "__ROOT__"}|{includeSubs}|{_filterUntaggedOnly}|{_filterLogicAnd}|{_sortColumn}|{_isSortAscending}|{string.Join(",", wantedNormTags)}|{string.Join(" ", queryTerms)}";

            List<SfxItem> filtered;
            List<VirtualFolderNode> visibleSubfolders;

            if (_filterResultCache.TryGetValue(cacheKey, out var cachedFilter))
            {
                filtered = cachedFilter.Filtered;
                visibleSubfolders = cachedFilter.Subfolders;
            }
            else
            {
                HashSet<string>? allowedFiles = null;
                visibleSubfolders = new List<VirtualFolderNode>();

                if (_selectedVirtualFolder != null)
                {
                    allowedFiles = includeSubs ? _selectedVirtualFolder.AllDescendantFiles : _selectedVirtualFolder.DirectFiles;

                    if (!includeSubs && _selectedVirtualFolder.Children != null && _selectedVirtualFolder.Children.Count > 0)
                    {
                        visibleSubfolders = queryTerms.Length > 0
                            ? _selectedVirtualFolder.Children.Where(c => SearchNormalizer.MatchesAllTerms(SearchNormalizer.Normalize(c.Name), queryTerms)).ToList()
                            : _selectedVirtualFolder.Children.ToList();
                    }
                }
                else if (!includeSubs && !hasSearchOrTagFilter && _rootFolders != null && _rootFolders.Count > 0)
                {
                    // Ở Thư viện gốc (thư mục tổng) khi tắt "Gồm thư mục con": hiện các thư mục gốc (Sound FX, Kho Nhạc, Video Overlay...)
                    // và chỉ hiện các file lẻ nằm trực tiếp ngoài cùng (nếu có)
                    visibleSubfolders = _rootFolders.ToList();

                    var allFolderFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rf in _rootFolders)
                    {
                        allFolderFiles.UnionWith(rf.AllDescendantFiles);
                    }
                    allowedFiles = new HashSet<string>(
                        _allFiles.Select(x => x.FilePathNorm).Where(p => !allFolderFiles.Contains(p)),
                        StringComparer.OrdinalIgnoreCase);
                }

                filtered = _allFiles.Where(item =>
                {
                    // Virtual Folder filter
                    if (allowedFiles != null && !allowedFiles.Contains(item.FilePathNorm))
                        return false;

                    // Untagged filter
                    if (_filterUntaggedOnly && item.Tags.Count > 0)
                        return false;

                    // Multi-tag filter
                    if (wantedNormTags.Count > 0)
                    {
                        var itemNormTags = item.NormalizedTags;
                        if (_filterLogicAnd)
                        {
                            // VÀ: Khớp tất cả các tag đã chọn
                            if (!wantedNormTags.All(wt => itemNormTags.Contains(wt)))
                                return false;
                        }
                        else
                        {
                            // HOẶC: Khớp bất kỳ tag nào trong số tag đã chọn
                            if (!wantedNormTags.Any(wt => itemNormTags.Contains(wt)))
                                return false;
                        }
                    }

                    // Query text filter (Normalized Vietnamese matching)
                    if (queryTerms.Length > 0)
                    {
                        if (!SearchNormalizer.MatchesAllTerms(item.SearchNormalized, queryTerms))
                            return false;
                    }

                    return true;
                }).ToList();

                // Column Sorting (Media Pool style)
                switch (_sortColumn)
                {
                    case "Name":
                        filtered = _isSortAscending
                            ? filtered.OrderBy(x => x.FileName).ToList()
                            : filtered.OrderByDescending(x => x.FileName).ToList();
                        break;
                    case "Folder":
                        filtered = _isSortAscending
                            ? filtered.OrderBy(x => x.FolderName).ThenBy(x => x.FileName).ToList()
                            : filtered.OrderByDescending(x => x.FolderName).ThenBy(x => x.FileName).ToList();
                        break;
                    case "Duration":
                        filtered = _isSortAscending
                            ? filtered.OrderBy(x => x.Duration).ToList()
                            : filtered.OrderByDescending(x => x.Duration).ToList();
                        break;
                    case "Extension":
                        filtered = _isSortAscending
                            ? filtered.OrderBy(x => x.Extension).ThenBy(x => x.FileName).ToList()
                            : filtered.OrderByDescending(x => x.Extension).ThenBy(x => x.FileName).ToList();
                        break;
                    case "Size":
                        filtered = _isSortAscending
                            ? filtered.OrderBy(x => x.FileSize).ToList()
                            : filtered.OrderByDescending(x => x.FileSize).ToList();
                        break;
                }

                // Cap RAM filter cache at 60 recent filter/folder combinations
                if (_filterResultCache.Count > 60)
                {
                    _filterResultCache.Clear();
                }
                _filterResultCache[cacheKey] = (filtered, visibleSubfolders);
            }

            int visualCount = filtered.Count(x => x.IsVideo || x.IsImage || x.IsDesign);
            if (filtered.Count > 0 && autoSwitchViewMode)
            {
                _isThumbnailViewMode = visualCount > (filtered.Count / 2);
            }

            // Preserve user's scroll position and selected item across filter refreshes
            ScrollViewer? scrollViewer = SfxListView != null
                ? FindVisualChildren<ScrollViewer>(SfxListView).FirstOrDefault()
                : null;
            double prevScrollOffset = scrollViewer?.VerticalOffset ?? 0;
            string? prevSelectedPath = (_selectedItem ?? SfxListView?.SelectedItem as SfxItem)?.FilePathNorm;
            int prevSelectedIndex = SfxListView?.SelectedIndex ?? -1;

            _visibleFiles = new ObservableCollection<SfxItem>(filtered);
            if (SfxListView != null)
            {
                SfxListView.ItemsSource = _visibleFiles;

                if (!string.IsNullOrEmpty(prevSelectedPath))
                {
                    var match = _visibleFiles.FirstOrDefault(x => x.FilePathNorm == prevSelectedPath);
                    if (match != null)
                    {
                        SfxListView.SelectedItem = match;
                        _selectedItem = match;
                    }
                    else if (_visibleFiles.Count > 0 && prevSelectedIndex >= 0)
                    {
                        int newIdx = Math.Min(prevSelectedIndex, _visibleFiles.Count - 1);
                        SfxListView.SelectedIndex = newIdx;
                        _selectedItem = _visibleFiles[newIdx];
                    }
                }
            }

            if (scrollViewer != null && prevScrollOffset > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { scrollViewer.ScrollToVerticalOffset(prevScrollOffset); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            ScrollViewer? thumbScrollViewer = (_isThumbnailViewMode && SfxThumbnailListBox != null)
                ? FindVisualChildren<ScrollViewer>(SfxThumbnailListBox).FirstOrDefault()
                : null;
            double prevThumbScrollOffset = thumbScrollViewer?.VerticalOffset ?? 0;

            if (SfxThumbnailListBox != null)
            {
                if (_isThumbnailViewMode)
                {
                    SfxThumbnailListBox.ItemsSource = _visibleFiles.Count > 300
                        ? _visibleFiles.Take(300).ToList()
                        : _visibleFiles;

                    if (thumbScrollViewer != null && prevThumbScrollOffset > 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { thumbScrollViewer.ScrollToVerticalOffset(prevThumbScrollOffset); } catch { }
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                }
                else
                {
                    SfxThumbnailListBox.ItemsSource = null;
                }
            }

            _currentVisibleSubfolders = visibleSubfolders;
            UpdateSubfolderBrowserUI(visibleSubfolders);
            UpdateViewModeToolbarUI();

            // Queue background thumbnail + 16-frame scrub sprite extraction only when Thumbnail View is active
            if (_isThumbnailViewMode && visualCount > 0)
            {
                var visibleVisuals = _visibleFiles.Where(x => x.IsVideo || x.IsImage || x.IsDesign).Take(300).ToList();
                if (visibleVisuals.Count > 0)
                {
                    _videoPreviewService.QueueVisibleItems(visibleVisuals);
                }
            }

            if (visibleSubfolders.Count > 0 && _visibleFiles.Count == 0)
            {
                int totalMedia = _selectedVirtualFolder != null ? _selectedVirtualFolder.TotalCount : _allFiles.Count;
                ResultCountText.Text = $"{visibleSubfolders.Count:N0} Thư mục ({totalMedia:N0} Media)";
            }
            else if (visibleSubfolders.Count > 0 && _visibleFiles.Count > 0)
            {
                ResultCountText.Text = $"{visibleSubfolders.Count:N0} Thư mục • {_visibleFiles.Count:N0} Media";
            }
            else
            {
                ResultCountText.Text = $"{_visibleFiles.Count:N0} / {_allFiles.Count:N0} Media";
            }

            // Update badge text
            var badges = new List<string>();
            if (_selectedFilterTags.Count > 0)
            {
                string logicText = _selectedFilterTags.Count > 1 ? (_filterLogicAnd ? " [VÀ]" : " [HOẶC]") : "";
                badges.Add($"Tag ({_selectedFilterTags.Count}): {string.Join(", ", _selectedFilterTags)}{logicText}");
            }
            if (_filterUntaggedOnly) badges.Add("Chưa tag");
            ActiveFilterBadge.Text = badges.Count > 0 ? $"({string.Join(" • ", badges)})" : string.Empty;
            UpdateSelectionActionToolbarUI();
        }

        private void UpdateSubfolderBrowserUI(List<VirtualFolderNode> visibleSubfolders)
        {
            if (SubfoldersBrowserBorder == null || FolderBreadcrumbPanel == null || SubfoldersListItemsControl == null || SubfoldersThumbnailItemsControl == null)
                return;

            bool showBrowser = _selectedVirtualFolder != null || visibleSubfolders.Count > 0;
            SubfoldersBrowserBorder.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;

            if (showBrowser)
            {
                FolderBackBtn.Visibility = _selectedVirtualFolder != null ? Visibility.Visible : Visibility.Collapsed;

                // Build Breadcrumb Trail
                FolderBreadcrumbPanel.Children.Clear();

                var rootBtn = new Button
                {
                    Content = "📁 Thư viện gốc",
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 13,
                    FontWeight = _selectedVirtualFolder == null ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = _selectedVirtualFolder == null
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)),
                    Cursor = _selectedVirtualFolder == null ? Cursors.Arrow : Cursors.Hand,
                    ToolTip = "Về Thư viện gốc"
                };
                rootBtn.Click += (_, _) =>
                {
                    if (_selectedVirtualFolder != null)
                        NavigateToVirtualFolder(null);
                };
                FolderBreadcrumbPanel.Children.Add(rootBtn);

                if (_selectedVirtualFolder != null)
                {
                    var chain = GetAncestorChain(_selectedVirtualFolder);
                    for (int i = 0; i < chain.Count; i++)
                    {
                        var node = chain[i];
                        bool isLast = (i == chain.Count - 1);

                        FolderBreadcrumbPanel.Children.Add(new TextBlock
                        {
                            Text = "›",
                            FontSize = 14,
                            FontWeight = FontWeights.Bold,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                            Margin = new Thickness(4, 0, 4, 1),
                            VerticalAlignment = VerticalAlignment.Center
                        });

                        var crumbBtn = new Button
                        {
                            Content = node.Name,
                            Padding = new Thickness(6, 2, 6, 2),
                            Background = isLast
                                ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26))
                                : Brushes.Transparent,
                            BorderBrush = isLast
                                ? new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38))
                                : Brushes.Transparent,
                            BorderThickness = new Thickness(isLast ? 1 : 0),
                            FontSize = 13,
                            FontWeight = isLast ? FontWeights.Bold : FontWeights.SemiBold,
                            Foreground = isLast
                                ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
                                : new SolidColorBrush(Color.FromRgb(0xC5, 0x9B, 0x27)),
                            Cursor = isLast ? Cursors.Arrow : Cursors.Hand,
                            ToolTip = node.Name
                        };
                        var targetNode = node;
                        crumbBtn.Click += (_, _) =>
                        {
                            if (!isLast)
                                NavigateToVirtualFolder(targetNode);
                        };
                        FolderBreadcrumbPanel.Children.Add(crumbBtn);
                    }
                }

                if (visibleSubfolders.Count > 0)
                {
                    SubfolderCountBadgeBorder.Visibility = Visibility.Visible;
                    SubfolderCountBadgeText.Text = _selectedVirtualFolder == null
                        ? $"{visibleSubfolders.Count} thư mục gốc"
                        : $"{visibleSubfolders.Count} thư mục con";

                    bool foldersOnly = _visibleFiles.Count == 0;
                    if (_isThumbnailViewMode)
                    {
                        SubfoldersThumbnailItemsControl.ItemsSource = visibleSubfolders;
                        SubfoldersThumbnailItemsControl.Visibility = Visibility.Visible;
                        SubfoldersListItemsControl.ItemsSource = null;
                        SubfoldersListItemsControl.Visibility = Visibility.Collapsed;
                        if (FolderListHeaderBorder != null) FolderListHeaderBorder.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        SubfoldersListItemsControl.ItemsSource = visibleSubfolders;
                        SubfoldersListItemsControl.Visibility = Visibility.Visible;
                        SubfoldersThumbnailItemsControl.ItemsSource = null;
                        SubfoldersThumbnailItemsControl.Visibility = Visibility.Collapsed;
                        if (FolderListHeaderBorder != null) FolderListHeaderBorder.Visibility = foldersOnly ? Visibility.Visible : Visibility.Collapsed;
                    }
                    SubfoldersScrollViewer.Visibility = Visibility.Visible;
                }
                else
                {
                    SubfolderCountBadgeBorder.Visibility = Visibility.Collapsed;
                    SubfoldersThumbnailItemsControl.ItemsSource = null;
                    SubfoldersThumbnailItemsControl.Visibility = Visibility.Collapsed;
                    SubfoldersListItemsControl.ItemsSource = null;
                    SubfoldersListItemsControl.Visibility = Visibility.Collapsed;
                    if (FolderListHeaderBorder != null) FolderListHeaderBorder.Visibility = Visibility.Collapsed;
                    SubfoldersScrollViewer.Visibility = Visibility.Collapsed;
                }
            }

            bool foldersOnlyView = _visibleFiles.Count == 0 && visibleSubfolders.Count > 0;
            Grid.SetRowSpan(SubfoldersBrowserBorder, foldersOnlyView ? 3 : 1);
            SubfoldersScrollViewer.MaxHeight = foldersOnlyView ? double.PositiveInfinity : 215;

            if (foldersOnlyView)
            {
                if (ListColumnHeaderBorder != null) ListColumnHeaderBorder.Visibility = Visibility.Collapsed;
                if (SfxListView != null) SfxListView.Visibility = Visibility.Collapsed;
                if (SfxThumbnailListBox != null) SfxThumbnailListBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (ListColumnHeaderBorder != null)
                    ListColumnHeaderBorder.Visibility = _isThumbnailViewMode ? Visibility.Collapsed : Visibility.Visible;
                if (SfxListView != null)
                    SfxListView.Visibility = _isThumbnailViewMode ? Visibility.Collapsed : Visibility.Visible;
                if (SfxThumbnailListBox != null)
                    SfxThumbnailListBox.Visibility = _isThumbnailViewMode ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        #endregion

        #region View Mode Switcher & DaVinci Resolve Live Hover Scrubbing

        private void ListViewModeBtn_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(false);
        }

        private void GridViewModeBtn_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(true);
        }

        private void UpdateViewModeToolbarUI()
        {
            bool thumbnailMode = _isThumbnailViewMode;
            if (ThumbnailSizeSliderPanel != null)
            {
                ThumbnailSizeSliderPanel.Visibility = thumbnailMode ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ListViewModeBtn != null && GridViewModeBtn != null)
            {
                ListViewModeBtn.Background = thumbnailMode
                    ? Brushes.Transparent
                    : new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
                ListViewModeBtn.BorderBrush = thumbnailMode
                    ? Brushes.Transparent
                    : new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
                ListViewModeBtn.Foreground = thumbnailMode
                    ? new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA))
                    : Brushes.White;

                GridViewModeBtn.Background = thumbnailMode
                    ? new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48))
                    : Brushes.Transparent;
                GridViewModeBtn.BorderBrush = thumbnailMode
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85))
                    : Brushes.Transparent;
                GridViewModeBtn.Foreground = thumbnailMode
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
            }

            UpdateMiddleToolbarResponsiveLayout();
        }

        private void SetViewMode(bool thumbnailMode)
        {
            _isThumbnailViewMode = thumbnailMode;

            bool foldersOnlyView = _visibleFiles.Count == 0 && _currentVisibleSubfolders.Count > 0;

            if (SfxThumbnailListBox != null)
            {
                if (thumbnailMode)
                {
                    SfxThumbnailListBox.ItemsSource = _visibleFiles.Count > 300
                        ? _visibleFiles.Take(300).ToList()
                        : _visibleFiles;
                }
                else
                {
                    SfxThumbnailListBox.ItemsSource = null;
                }
            }

            if (SfxListView != null && SfxThumbnailListBox != null)
            {
                if (foldersOnlyView)
                {
                    SfxListView.Visibility = Visibility.Collapsed;
                    SfxThumbnailListBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SfxListView.Visibility = thumbnailMode ? Visibility.Collapsed : Visibility.Visible;
                    SfxThumbnailListBox.Visibility = thumbnailMode ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            if (ListColumnHeaderBorder != null)
            {
                ListColumnHeaderBorder.Visibility = (thumbnailMode || foldersOnlyView) ? Visibility.Collapsed : Visibility.Visible;
            }

            // Update Subfolder views dynamically
            if (SubfoldersThumbnailItemsControl != null && SubfoldersListItemsControl != null && _currentVisibleSubfolders != null && _currentVisibleSubfolders.Count > 0)
            {
                if (thumbnailMode)
                {
                    SubfoldersThumbnailItemsControl.ItemsSource = _currentVisibleSubfolders;
                    SubfoldersThumbnailItemsControl.Visibility = Visibility.Visible;
                    SubfoldersListItemsControl.ItemsSource = null;
                    SubfoldersListItemsControl.Visibility = Visibility.Collapsed;
                    if (FolderListHeaderBorder != null) FolderListHeaderBorder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SubfoldersListItemsControl.ItemsSource = _currentVisibleSubfolders;
                    SubfoldersListItemsControl.Visibility = Visibility.Visible;
                    SubfoldersThumbnailItemsControl.ItemsSource = null;
                    SubfoldersThumbnailItemsControl.Visibility = Visibility.Collapsed;
                    if (FolderListHeaderBorder != null) FolderListHeaderBorder.Visibility = foldersOnlyView ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            UpdateViewModeToolbarUI();

            if (thumbnailMode && _visibleFiles.Count > 0)
            {
                var visibleVideos = _visibleFiles.Where(x => x.IsVideo).Take(300).ToList();
                if (visibleVideos.Count > 0)
                {
                    _videoPreviewService.QueueVisibleItems(visibleVideos);
                }
            }
        }

        private void ThumbnailSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            double width = Math.Clamp(e.NewValue, 160, 360);
            double imageHeight = Math.Round((width - 2) * 9.0 / 16.0);
            Resources["ThumbnailCardWidth"] = width;
            Resources["ThumbnailCardImageHeight"] = imageHeight;
        }

        private void ThumbnailCard_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement card && card.DataContext is SfxItem item)
            {
                if (item.IsVideo && (item.ThumbnailSource == null || item.ScrubFrames == null))
                {
                    _ = _videoPreviewService.EnsureVideoPreviewAsync(item, allowCloudHydrate: false);
                }
                UpdateCardHoverScrub(card, item, e);
            }
        }

        private void ThumbnailCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement card && card.DataContext is SfxItem item)
            {
                UpdateCardHoverScrub(card, item, e);
            }
        }

        private void ThumbnailCard_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement card && card.DataContext is SfxItem item)
            {
                item.StopScrub();
            }
        }

        private void UpdateCardHoverScrub(FrameworkElement card, SfxItem item, MouseEventArgs e)
        {
            double width = card.ActualWidth;
            if (width <= 2) return;

            Point pos = e.GetPosition(card);
            double ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
            item.UpdateScrub(ratio, width - 2);

            // Live mirror in bottom video monitor if this is the active or hovered video item
            if (item.IsVideo && BottomVideoMonitorBorder != null && BottomVideoMonitorImage != null)
            {
                if (_selectedItem == null || _selectedItem.FilePath == item.FilePath)
                {
                    BottomVideoMonitorBorder.Visibility = Visibility.Visible;
                    BottomVideoMonitorImage.Source = item.DisplayImageSource;
                }
            }
        }

        private void UpdateVideoMonitorFrameForTime(double currentSec, double totalSec)
        {
            if (_selectedItem == null || !_selectedItem.IsVideo) return;
            if (BottomVideoMonitorImage == null || BottomVideoMonitorBorder == null) return;

            BottomVideoMonitorBorder.Visibility = Visibility.Visible;

            var frames = _selectedItem.ScrubFrames;
            if (frames != null && frames.Length > 0 && totalSec > 0)
            {
                double ratio = Math.Clamp(currentSec / totalSec, 0.0, 1.0);
                int idx = Math.Clamp((int)Math.Floor(ratio * frames.Length), 0, frames.Length - 1);
                BottomVideoMonitorImage.Source = frames[idx];
            }
            else
            {
                BottomVideoMonitorImage.Source = _selectedItem.DisplayImageSource;
            }
        }

        #endregion

        #region Audio Playback & Waveform

        private void AutoPreviewCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _dataManager.Settings.AutoPreview = AutoPreviewCheck.IsChecked == true;
            _ = _dataManager.SaveSettingsAsync();
        }

        private async void SfxListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingSelection) return;
            _isSyncingSelection = true;
            try
            {
                if (SfxThumbnailListBox != null && SfxListView.SelectedItem is SfxItem sel)
                {
                    SfxThumbnailListBox.SelectedItem = sel;
                }
            }
            finally
            {
                _isSyncingSelection = false;
            }

            await HandleSelectionChangedCoreAsync();
        }

        private async void SfxThumbnailListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingSelection) return;
            _isSyncingSelection = true;
            try
            {
                if (SfxListView != null && SfxThumbnailListBox.SelectedItem is SfxItem sel)
                {
                    SfxListView.SelectedItem = sel;
                }
            }
            finally
            {
                _isSyncingSelection = false;
            }

            await HandleSelectionChangedCoreAsync();
        }

        private async Task HandleSelectionChangedCoreAsync(bool forceAutoPlay = false)
        {
            var selectedList = GetSelectedSfxList();
            if (selectedList.Count > 1)
            {
                _selectedItem = selectedList.Last();
                NowPlayingTitle.Text = $"{selectedList.Count:N0} Media đã chọn (Multi-select)";
                NowPlayingMeta.Text = "Bấm tag ở trên để gán hoặc gỡ tag cho các file đang chọn";
                DragModeBadge.Text = $"({selectedList.Count} File)";

                UpdateTagChipsSelection();
                UpdateQuickAvailableTagsUI();
            }
            else if (selectedList.Count == 1)
            {
                var item = selectedList[0];
                if (!string.IsNullOrWhiteSpace(item.FilePath))
                {
                    string? root = Path.GetPathRoot(item.FilePath);
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && !File.Exists(item.FilePath))
                    {
                        // File was deleted on disk!
                        _allFiles.Remove(item);
                        _visibleFiles.Remove(item);
                        foreach (var vf in _dataManager.Settings.VirtualFolders)
                        {
                            vf.Files.RemoveAll(f => f.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
                        }
                        _ = _dataManager.SaveSettingsAsync();
                        _ = _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                        AllCountBadge.Text = _allFiles.Count.ToString("N0");
                        UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                        InvalidateFilterCache();
                        ApplyFilter();
                        return;
                    }
                }

                _selectedItem = item;
                NowPlayingTitle.Text = item.FileName;
                string resExtra = !string.IsNullOrEmpty(item.ResolutionBadge) ? $" • {item.ResolutionBadge}" : "";
                NowPlayingMeta.Text = $"{item.FolderName} • {item.Extension}{resExtra} • {item.FileSizeText}";
                DragModeBadge.Text = "(Toàn bộ file)";

                if (BottomVideoMonitorBorder != null && BottomVideoMonitorImage != null)
                {
                    if (item.IsVideo || item.IsImage || item.IsDesign)
                    {
                        BottomVideoMonitorBorder.Visibility = Visibility.Visible;
                        BottomVideoMonitorImage.Source = item.DisplayImageSource;
                        _ = Task.Run(async () =>
                        {
                            if (item.IsVideo)
                            {
                                await _videoPreviewService.EnsureVideoPreviewAsync(item, allowCloudHydrate: true);
                            }
                            else
                            {
                                await _videoPreviewService.EnsureThumbnailAsync(item, allowCloudHydrate: true);
                            }
                            _ = Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_selectedItem != null && _selectedItem.FilePath == item.FilePath)
                                {
                                    BottomVideoMonitorImage.Source = item.DisplayImageSource;
                                    if (item.Duration > 0)
                                    {
                                        WaveformView.Duration = item.Duration;
                                        _audioPlayer.UpdateVirtualDuration(item.Duration);
                                    }
                                }
                            }));
                        });
                    }
                    else
                    {
                        BottomVideoMonitorBorder.Visibility = Visibility.Collapsed;
                        BottomVideoMonitorImage.Source = null;
                    }
                }

                // Reset waveform
                WaveformView.Duration = item.Duration > 0 ? item.Duration : 1.0;
                WaveformView.CurrentTime = 0;
                WaveformView.InPosition = 0;
                WaveformView.OutPosition = 0;
                WaveformView.Peaks = null;

                UpdateTrimInfoUI();
                UpdateTagChipsSelection();
                UpdateQuickAvailableTagsUI();

                if (item.IsAudio || item.IsVideo)
                {
                    bool autoPlay = forceAutoPlay || (AutoPreviewCheck?.IsChecked == true);
                    if (!_suppressSelectionAutoPlay)
                    {
                        await _audioPlayer.LoadFileAsync(item.FilePath, autoPlay: autoPlay, fallbackDuration: item.Duration);
                    }
                }
                else
                {
                    _audioPlayer.Stop();
                }
                UpdatePlayButtonUI();
            }
            else
            {
                _selectedItem = null;
                NowPlayingTitle.Text = "Chưa phát SFX";
                NowPlayingMeta.Text = "Chọn file để xem và nghe thử";
                DragModeBadge.Text = "";
                UpdateTagChipsSelection();
                UpdateQuickAvailableTagsUI();
            }

            if (selectedList.Count > 0 && !_isMarqueeSelecting)
            {
                ClearSubfolderCardSelection();
            }
            UpdateSelectionActionToolbarUI();
        }

        private bool _suppressSelectionAutoPlay = false;

        private async Task NavigateAndPlayByOffsetAsync(int delta)
        {
            if (_visibleFiles == null || _visibleFiles.Count == 0) return;

            int curIndex = -1;
            if (_selectedItem != null)
            {
                curIndex = _visibleFiles.IndexOf(_selectedItem);
            }
            if (curIndex < 0)
            {
                curIndex = _isThumbnailViewMode
                    ? (SfxThumbnailListBox?.SelectedIndex ?? -1)
                    : (SfxListView?.SelectedIndex ?? -1);
            }

            int targetIndex = curIndex < 0
                ? 0
                : Math.Clamp(curIndex + delta, 0, _visibleFiles.Count - 1);

            var targetItem = _visibleFiles[targetIndex];
            bool alreadyLoaded = (_selectedItem != null &&
                                  _selectedItem.FilePath == targetItem.FilePath &&
                                  _audioPlayer.CurrentFilePath == targetItem.FilePath);

            _isSyncingSelection = true;
            try
            {
                if (SfxListView != null)
                {
                    SfxListView.SelectedItems.Clear();
                    SfxListView.SelectedItem = targetItem;
                    SfxListView.ScrollIntoView(targetItem);
                }
                if (SfxThumbnailListBox != null)
                {
                    SfxThumbnailListBox.SelectedItems.Clear();
                    SfxThumbnailListBox.SelectedItem = targetItem;
                    SfxThumbnailListBox.ScrollIntoView(targetItem);
                }
                _selectedItem = targetItem;
            }
            finally
            {
                _isSyncingSelection = false;
            }

            if (alreadyLoaded)
            {
                if (!_audioPlayer.IsPlaying)
                {
                    _audioPlayer.Play();
                    UpdatePlayButtonUI();
                }
                return;
            }

            await HandleSelectionChangedCoreAsync(forceAutoPlay: true);
        }

        private void OpenItemLocation(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            Task.Run(() =>
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{filePath}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        string? dir = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{dir}\"",
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ZDialog.Show($"Không tìm thấy file hoặc thư mục trên ổ đĩa:\n{filePath}", "Thông báo", ZDialogKind.Warning, owner: this);
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ZDialog.Show($"Không thể mở vị trí file: {ex.Message}", "Lỗi", ZDialogKind.Warning, owner: this);
                    }));
                }
            });
        }

        private Point _rightClickStartPoint;
        private bool _isRightClickCandidate;
        private SfxItem? _rightClickedCandidateItem;

        private void SfxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRightClickCandidate = false;
            _rightClickedCandidateItem = null;

            if (e.OriginalSource is DependencyObject orig)
            {
                if (FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(orig) != null ||
                    FindVisualParent<GridViewColumnHeader>(orig) != null)
                {
                    return;
                }

                var itemContainer = FindVisualParent<ListBoxItem>(orig);
                if (itemContainer != null && itemContainer.DataContext is SfxItem clickedSfx)
                {
                    _isRightClickCandidate = true;
                    _rightClickedCandidateItem = clickedSfx;
                    _rightClickStartPoint = e.GetPosition(this);
                }
            }
        }

        private void SfxItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRightClickCandidate || _rightClickedCandidateItem == null)
            {
                _isRightClickCandidate = false;
                _rightClickedCandidateItem = null;
                return;
            }

            var clickedSfx = _rightClickedCandidateItem;
            _isRightClickCandidate = false;
            _rightClickedCandidateItem = null;

            Point currentPoint = e.GetPosition(this);
            Vector diff = currentPoint - _rightClickStartPoint;
            if (Math.Abs(diff.X) > 8 || Math.Abs(diff.Y) > 8)
            {
                return; // User dragged the mouse, ignore
            }

            e.Handled = true;

            // Ensure clicked item is selected
            if (_isThumbnailViewMode && SfxThumbnailListBox != null)
            {
                if (!SfxThumbnailListBox.SelectedItems.Contains(clickedSfx))
                {
                    SfxThumbnailListBox.SelectedItem = clickedSfx;
                }
            }
            else if (SfxListView != null)
            {
                if (!SfxListView.SelectedItems.Contains(clickedSfx))
                {
                    SfxListView.SelectedItem = clickedSfx;
                }
            }

            OpenItemLocation(clickedSfx.FilePath);
        }

        private void NowPlaying_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_selectedItem != null && !string.IsNullOrWhiteSpace(_selectedItem.FilePath))
            {
                e.Handled = true;
                OpenItemLocation(_selectedItem.FilePath);
            }
        }

        private async void SfxListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = (_isThumbnailViewMode ? SfxThumbnailListBox?.SelectedItem : SfxListView?.SelectedItem) as SfxItem
                       ?? _selectedItem;
            if (item != null)
            {
                if (item.IsImage || item.IsDesign)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = item.FilePath,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        OpenItemLocation(item.FilePath);
                    }
                    return;
                }

                if (_audioPlayer.CurrentFilePath == item.FilePath)
                {
                    if (!_audioPlayer.IsPlaying)
                    {
                        _audioPlayer.Play();
                    }
                }
                else
                {
                    await _audioPlayer.LoadFileAsync(item.FilePath, autoPlay: true, fallbackDuration: item.Duration);
                }
                UpdatePlayButtonUI();
            }
        }

        private async void RowPlayButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement el && el.DataContext is SfxItem item)
            {
                if (item.IsImage || item.IsDesign)
                {
                    _isSyncingSelection = true;
                    try
                    {
                        if (SfxListView != null)
                        {
                            SfxListView.SelectedItems.Clear();
                            SfxListView.SelectedItem = item;
                        }
                        if (SfxThumbnailListBox != null)
                        {
                            SfxThumbnailListBox.SelectedItems.Clear();
                            SfxThumbnailListBox.SelectedItem = item;
                        }
                        _selectedItem = item;
                    }
                    finally
                    {
                        _isSyncingSelection = false;
                    }

                    await HandleSelectionChangedCoreAsync(forceAutoPlay: false);
                    return;
                }

                if (_selectedItem != null && _selectedItem.FilePath == item.FilePath && _audioPlayer.CurrentFilePath == item.FilePath)
                {
                    _audioPlayer.TogglePlay();
                    UpdatePlayButtonUI();
                    return;
                }

                _isSyncingSelection = true;
                try
                {
                    if (SfxListView != null)
                    {
                        SfxListView.SelectedItems.Clear();
                        SfxListView.SelectedItem = item;
                    }
                    if (SfxThumbnailListBox != null)
                    {
                        SfxThumbnailListBox.SelectedItems.Clear();
                        SfxThumbnailListBox.SelectedItem = item;
                    }
                    _selectedItem = item;
                }
                finally
                {
                    _isSyncingSelection = false;
                }

                await HandleSelectionChangedCoreAsync(forceAutoPlay: true);
            }
        }

        private async void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            await TogglePlayPauseAsync();
        }

        private async Task TogglePlayPauseAsync()
        {
            if (_selectedItem == null && SfxListView.SelectedItem is SfxItem current)
            {
                _selectedItem = current;
            }

            if (_selectedItem == null && _visibleFiles.Count > 0)
            {
                SfxListView.SelectedIndex = 0;
                _selectedItem = SfxListView.SelectedItem as SfxItem;
            }

            if (_selectedItem == null) return;

            if (_audioPlayer.CurrentFilePath != _selectedItem.FilePath)
            {
                await _audioPlayer.LoadFileAsync(_selectedItem.FilePath, autoPlay: true, fallbackDuration: _selectedItem.Duration);
            }
            else
            {
                _audioPlayer.TogglePlay();
            }
            UpdatePlayButtonUI();
        }

        private void LoopBtn_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayer.IsLooping = !_audioPlayer.IsLooping;
            UpdateLoopButtonUI();
        }

        private SfxItem? _lastPlayingItem;

        private void UpdatePlayButtonUI()
        {
            bool isPlaying = _audioPlayer.IsPlaying;
            if (PlayIconPath != null && PlayBtnText != null)
            {
                PlayIconPath.Data = isPlaying ? (Geometry)FindResource("IconPause") : (Geometry)FindResource("IconPlay");
                PlayBtnText.Text = isPlaying ? "Tạm dừng (Space)" : "Phát (Space)";
            }
            else
            {
                PlayBtn.Content = isPlaying ? "Tạm dừng (Space)" : "Phát (Space)";
            }

            var currentPlaying = (isPlaying && _selectedItem != null) ? _selectedItem : null;
            if (_lastPlayingItem != null && !ReferenceEquals(_lastPlayingItem, currentPlaying))
            {
                _lastPlayingItem.IsPlaying = false;
            }
            if (currentPlaying != null)
            {
                currentPlaying.IsPlaying = true;
            }
            _lastPlayingItem = currentPlaying;
        }

        private void UpdateLoopButtonUI()
        {
            if (_audioPlayer.IsLooping)
            {
                LoopBtn.Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
                LoopBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14));
                LoopBtn.Foreground = Brushes.White;
            }
            else
            {
                LoopBtn.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
                LoopBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
                LoopBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
            }
        }

        private int _positionUpdatePending;
        private double _pendingCurrentSec;
        private double _pendingTotalSec;

        private void OnAudioPositionChanged(double currentSec, double totalSec)
        {
            System.Threading.Volatile.Write(ref _pendingCurrentSec, currentSec);
            System.Threading.Volatile.Write(ref _pendingTotalSec, totalSec);

            if (System.Threading.Interlocked.Exchange(ref _positionUpdatePending, 1) == 1)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _positionUpdatePending, 0);
                double cur = System.Threading.Volatile.Read(ref _pendingCurrentSec);
                double tot = System.Threading.Volatile.Read(ref _pendingTotalSec);

                WaveformView.Duration = tot;
                WaveformView.CurrentTime = cur;

                var curTs = TimeSpan.FromSeconds(cur);
                var totTs = TimeSpan.FromSeconds(tot);
                TimeDisplay.Text = $"{curTs:mm\\:ss\\.f} / {totTs:mm\\:ss\\.f}";

                UpdateVideoMonitorFrameForTime(cur, tot);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void OnAudioPlaybackStopped()
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdatePlayButtonUI();
            }));
        }

        private void OnAudioWaveformReady(string filePath, float[] peaks, double duration)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_selectedItem != null && _selectedItem.FilePath == filePath)
                {
                    WaveformView.Peaks = peaks;
                    WaveformView.Duration = duration;
                    if (_selectedItem.Duration <= 0)
                    {
                        _selectedItem.Duration = duration;
                    }
                }
            }));
        }

        private void OnWaveformRangeSelected(double inSec, double outSec)
        {
            _audioPlayer.InPosition = inSec;
            _audioPlayer.OutPosition = outSec;
            UpdateTrimInfoUI();
        }

        private void SetInBtn_Click(object sender, RoutedEventArgs e)
        {
            double cur = _audioPlayer.CurrentTime;
            _audioPlayer.InPosition = cur;
            if (_audioPlayer.OutPosition <= cur)
            {
                _audioPlayer.OutPosition = _audioPlayer.TotalDuration;
            }
            WaveformView.InPosition = _audioPlayer.InPosition;
            WaveformView.OutPosition = _audioPlayer.OutPosition;
            UpdateTrimInfoUI();
        }

        private void SetOutBtn_Click(object sender, RoutedEventArgs e)
        {
            double cur = _audioPlayer.CurrentTime;
            if (cur > _audioPlayer.InPosition)
            {
                _audioPlayer.OutPosition = cur;
            }
            else
            {
                _audioPlayer.OutPosition = _audioPlayer.TotalDuration;
            }
            WaveformView.InPosition = _audioPlayer.InPosition;
            WaveformView.OutPosition = _audioPlayer.OutPosition;
            UpdateTrimInfoUI();
        }

        private void ClearInOutBtn_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayer.InPosition = 0;
            _audioPlayer.OutPosition = 0;
            WaveformView.InPosition = 0;
            WaveformView.OutPosition = 0;
            UpdateTrimInfoUI();
        }

        private void UpdateTrimInfoUI()
        {
            if (_audioPlayer.HasSelection)
            {
                double len = _audioPlayer.OutPosition - _audioPlayer.InPosition;
                TrimInfoText.Text = $"In: {_audioPlayer.InPosition:0.00}s | Out: {_audioPlayer.OutPosition:0.00}s (Đoạn: {len:0.00}s)";
                DragModeBadge.Text = "(Đoạn đã chọn)";
            }
            else
            {
                TrimInfoText.Text = string.Empty;
                DragModeBadge.Text = "(Toàn bộ file)";
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioPlayer != null)
            {
                _audioPlayer.Volume = e.NewValue;
            }
        }

        private void MuteBtn_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayer.IsMuted = !_audioPlayer.IsMuted;
            if (MuteIconPath != null)
            {
                MuteIconPath.Data = _audioPlayer.IsMuted ? (Geometry)FindResource("IconSpeakerMute") : (Geometry)FindResource("IconSpeaker");
                MuteIconPath.Stroke = _audioPlayer.IsMuted ? new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)) : new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
            }
        }

        #endregion

        #region Native Drag & Drop & Marquee Box Selection

        private bool _canStartFileDrag = false;
        private string[]? _dragCandidatePaths = null;
        private SfxItem? _deferredSingleSelectOnMouseUp = null;
        private bool _isMarqueeCandidate = false;
        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartCanvasPoint;
        private HashSet<string> _marqueeInitialFolderIds = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _marqueeInitialMediaPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _clickedEmptySpaceInSubfolders = false;
        private bool _clickedEmptySpaceInMediaList = false;

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }
                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private void SubfoldersScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject orig)
            {
                if (FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(orig) != null)
                {
                    return;
                }

                var clickedBtn = FindVisualParent<Button>(orig);
                _clickedEmptySpaceInSubfolders = (clickedBtn == null);
            }
            else
            {
                _clickedEmptySpaceInSubfolders = true;
            }

            _clickedEmptySpaceInMediaList = false;
            _canStartFileDrag = false;
            _dragCandidatePaths = null;
            _deferredSingleSelectOnMouseUp = null;

            if (_clickedEmptySpaceInSubfolders)
            {
                PrepareMarqueeCandidate(e);
            }
            else
            {
                _isMarqueeCandidate = false;
                _isMarqueeSelecting = false;
            }
        }

        private void SfxListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _clickedEmptySpaceInSubfolders = false;
            _clickedEmptySpaceInMediaList = false;
            _dragCandidatePaths = null;
            _deferredSingleSelectOnMouseUp = null;

            if (e.OriginalSource is DependencyObject orig)
            {
                if (FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(orig) != null ||
                    FindVisualParent<GridViewColumnHeader>(orig) != null)
                {
                    _canStartFileDrag = false;
                    _isMarqueeCandidate = false;
                    return;
                }

                var clickedListBoxItem = FindVisualParent<ListBoxItem>(orig);
                if (clickedListBoxItem != null && clickedListBoxItem.DataContext is SfxItem clickedSfx)
                {
                    // USER CLICKED DIRECTLY ON A MEDIA FILE ROW OR THUMBNAIL CARD!
                    // This must NEVER trigger Marquee Selection!
                    _isMarqueeCandidate = false;
                    _isMarqueeSelecting = false;
                    _clickedEmptySpaceInMediaList = false;

                    // Configure File Drag candidates
                    _canStartFileDrag = true;
                    _dragStartPoint = e.GetPosition(null);

                    bool hasCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                    bool hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    bool hasModifier = hasCtrl || hasShift;

                    var currentSelectedList = GetSelectedSfxList();

                    if (clickedListBoxItem.IsSelected)
                    {
                        // Item was already selected:
                        if (!hasModifier && currentSelectedList.Count > 1)
                        {
                            // Keep multi-selection intact for dragging!
                            // If user releases mouse without dragging, narrow to single item on MouseUp.
                            _deferredSingleSelectOnMouseUp = clickedSfx;
                            e.Handled = true; // Prevent WPF from instantly collapsing the multi-selection on MouseDown
                        }
                        else
                        {
                            _deferredSingleSelectOnMouseUp = null;
                        }

                        _dragCandidatePaths = currentSelectedList
                            .Where(x => File.Exists(x.FilePath))
                            .Select(x => x.FilePath)
                            .ToArray();
                    }
                    else
                    {
                        // Item was NOT selected yet:
                        _deferredSingleSelectOnMouseUp = null;

                        if (!hasModifier)
                        {
                            // Select this single item immediately
                            if (_isThumbnailViewMode && SfxThumbnailListBox != null)
                            {
                                SfxThumbnailListBox.SelectedItem = clickedSfx;
                            }
                            else if (SfxListView != null)
                            {
                                SfxListView.SelectedItem = clickedSfx;
                            }

                            _dragCandidatePaths = File.Exists(clickedSfx.FilePath)
                                ? new[] { clickedSfx.FilePath }
                                : Array.Empty<string>();
                        }
                        else
                        {
                            // Let WPF handle Ctrl/Shift multi-selection
                            var paths = currentSelectedList
                                .Where(x => File.Exists(x.FilePath))
                                .Select(x => x.FilePath)
                                .ToList();
                            if (File.Exists(clickedSfx.FilePath) && !paths.Contains(clickedSfx.FilePath, StringComparer.OrdinalIgnoreCase))
                            {
                                paths.Add(clickedSfx.FilePath);
                            }
                            _dragCandidatePaths = paths.ToArray();
                        }
                    }

                    return;
                }

                // If not clicking on a ListBoxItem, user clicked on empty space in media list
                _clickedEmptySpaceInMediaList = true;
            }
            else
            {
                _clickedEmptySpaceInMediaList = true;
            }

            // USER CLICKED ON EMPTY SPACE -> Can start Marquee Selection!
            _canStartFileDrag = false;
            PrepareMarqueeCandidate(e);
        }

        private void PrepareMarqueeCandidate(MouseButtonEventArgs e)
        {
            if (MarqueeSelectionCanvas == null) return;

            _isMarqueeCandidate = true;
            _isMarqueeSelecting = false;
            _dragStartPoint = e.GetPosition(null);
            _marqueeStartCanvasPoint = e.GetPosition(MarqueeSelectionCanvas);

            bool keepExisting = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
            _marqueeInitialFolderIds.Clear();
            _marqueeInitialMediaPaths.Clear();

            if (keepExisting)
            {
                foreach (var node in GetSelectedSubfolderCards())
                {
                    _marqueeInitialFolderIds.Add(node.Id);
                }
                foreach (var media in GetSelectedSfxList())
                {
                    _marqueeInitialMediaPaths.Add(media.FilePath);
                }
            }
        }

        private bool TryStartFileDrag(Point current)
        {
            if (!_canStartFileDrag || _isMarqueeSelecting) return false;

            Vector diff = _dragStartPoint - current;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _canStartFileDrag = false;
                _isMarqueeCandidate = false;
                _deferredSingleSelectOnMouseUp = null;

                string[]? paths = _dragCandidatePaths;
                if (paths == null || paths.Length == 0)
                {
                    var selectedList = GetSelectedSfxList();
                    paths = selectedList.Where(x => File.Exists(x.FilePath)).Select(x => x.FilePath).ToArray();
                }

                if (paths != null && paths.Length > 0)
                {
                    StartFilesDrag(paths);
                    return true;
                }
            }
            return false;
        }

        private void SfxListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _canStartFileDrag && !_isMarqueeSelecting)
            {
                TryStartFileDrag(e.GetPosition(null));
            }
        }

        private void CenterContentGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_isMarqueeSelecting)
                {
                    EndMarqueeSelection();
                }
                _isMarqueeCandidate = false;
                _canStartFileDrag = false;
                _deferredSingleSelectOnMouseUp = null;
                return;
            }

            // 1. If user initiated a file drag from an item, handle file drag first!
            if (_canStartFileDrag && !_isMarqueeSelecting)
            {
                if (TryStartFileDrag(e.GetPosition(null)))
                {
                    return;
                }
                return; // User clicked on an item and is within drag threshold; NEVER run marquee!
            }

            // 2. Marquee selection only if user started from empty background space!
            if (!_isMarqueeCandidate && !_isMarqueeSelecting)
                return;

            if (MarqueeSelectionCanvas == null || MarqueeSelectionBorder == null)
                return;

            Point currentCanvasPoint = e.GetPosition(MarqueeSelectionCanvas);
            Vector diff = currentCanvasPoint - _marqueeStartCanvasPoint;

            if (!_isMarqueeSelecting)
            {
                if (Math.Abs(diff.X) < 6 && Math.Abs(diff.Y) < 6)
                    return;

                _isMarqueeSelecting = true;
                _canStartFileDrag = false;
                _suppressNextSubfolderClick = true;
                MarqueeSelectionCanvas.Visibility = Visibility.Visible;
                MarqueeSelectionBorder.Visibility = Visibility.Visible;
                MarqueeSelectionCanvas.CaptureMouse();
            }

            double canvasW = Math.Max(1, MarqueeSelectionCanvas.ActualWidth);
            double canvasH = Math.Max(1, MarqueeSelectionCanvas.ActualHeight);

            double x1 = Math.Clamp(_marqueeStartCanvasPoint.X, 0, canvasW);
            double y1 = Math.Clamp(_marqueeStartCanvasPoint.Y, 0, canvasH);
            double x2 = Math.Clamp(currentCanvasPoint.X, 0, canvasW);
            double y2 = Math.Clamp(currentCanvasPoint.Y, 0, canvasH);

            double left = Math.Min(x1, x2);
            double top = Math.Min(y1, y2);
            double width = Math.Max(1, Math.Abs(x2 - x1));
            double height = Math.Max(1, Math.Abs(y2 - y1));

            Canvas.SetLeft(MarqueeSelectionBorder, left);
            Canvas.SetTop(MarqueeSelectionBorder, top);
            MarqueeSelectionBorder.Width = width;
            MarqueeSelectionBorder.Height = height;

            UpdateMarqueeSelectionItems(new Rect(left, top, width, height));
            e.Handled = true;
        }

        private void UpdateMarqueeSelectionItems(Rect marqueeRect)
        {
            if (MarqueeSelectionCanvas == null) return;

            // 1. Update Subfolder Cards selection if visible
            if (SubfoldersBrowserBorder != null &&
                SubfoldersBrowserBorder.Visibility == Visibility.Visible &&
                _currentVisibleSubfolders != null &&
                _currentVisibleSubfolders.Count > 0)
            {
                var hitFolderIds = new HashSet<string>(_marqueeInitialFolderIds, StringComparer.OrdinalIgnoreCase);
                var activeFolderControl = _isThumbnailViewMode ? (ItemsControl)SubfoldersThumbnailItemsControl : (ItemsControl)SubfoldersListItemsControl;

                if (activeFolderControl != null)
                {
                    foreach (var btn in FindVisualChildren<Button>(activeFolderControl))
                    {
                        if (btn.DataContext is VirtualFolderNode node && btn.ActualWidth > 0 && btn.ActualHeight > 0)
                        {
                            try
                            {
                                Rect itemBounds = btn.TransformToVisual(MarqueeSelectionCanvas)
                                    .TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
                                if (marqueeRect.IntersectsWith(itemBounds))
                                {
                                    hitFolderIds.Add(node.Id);
                                }
                            }
                            catch { }
                        }
                    }
                }

                foreach (var node in _currentVisibleSubfolders)
                {
                    node.IsCardSelected = hitFolderIds.Contains(node.Id);
                }
            }

            // 2. Update Media Items selection if visible
            ListBox? activeMediaControl = _isThumbnailViewMode
                ? SfxThumbnailListBox
                : SfxListView;

            if (activeMediaControl != null &&
                activeMediaControl.Visibility == Visibility.Visible &&
                _visibleFiles != null &&
                _visibleFiles.Count > 0)
            {
                _suppressSelectionAutoPlay = true;
                try
                {
                    foreach (var listBoxItem in FindVisualChildren<ListBoxItem>(activeMediaControl))
                    {
                        if (listBoxItem.DataContext is SfxItem mediaItem &&
                            listBoxItem.ActualWidth > 0 &&
                            listBoxItem.ActualHeight > 0)
                        {
                            try
                            {
                                Rect itemBounds = listBoxItem.TransformToVisual(MarqueeSelectionCanvas)
                                    .TransformBounds(new Rect(0, 0, listBoxItem.ActualWidth, listBoxItem.ActualHeight));
                                bool shouldSelect = _marqueeInitialMediaPaths.Contains(mediaItem.FilePath) ||
                                                    marqueeRect.IntersectsWith(itemBounds);
                                if (listBoxItem.IsSelected != shouldSelect)
                                {
                                    listBoxItem.IsSelected = shouldSelect;
                                }
                            }
                            catch { }
                        }
                    }
                }
                finally
                {
                    _suppressSelectionAutoPlay = false;
                }
            }

            UpdateSelectionActionToolbarUI();
        }

        private void CenterContentGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isMarqueeSelecting)
            {
                EndMarqueeSelection();
                e.Handled = true;
                // Reset suppress flag on background dispatcher priority after click events settle
                Dispatcher.BeginInvoke(new Action(() => _suppressNextSubfolderClick = false),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            _isMarqueeCandidate = false;
            _canStartFileDrag = false;
            _dragCandidatePaths = null;

            // If user clicked on an already-selected item among multiple items but DID NOT drag:
            if (_deferredSingleSelectOnMouseUp != null)
            {
                var targetItem = _deferredSingleSelectOnMouseUp;
                _deferredSingleSelectOnMouseUp = null;

                bool hasMod = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
                if (!hasMod)
                {
                    if (_isThumbnailViewMode && SfxThumbnailListBox != null)
                    {
                        SfxThumbnailListBox.SelectedItem = targetItem;
                    }
                    else if (SfxListView != null)
                    {
                        SfxListView.SelectedItem = targetItem;
                    }
                }
            }

            bool hasModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
            if (!hasModifier && (_clickedEmptySpaceInSubfolders || _clickedEmptySpaceInMediaList))
            {
                ClearSubfolderCardSelection();
                if (_clickedEmptySpaceInMediaList)
                {
                    SfxListView?.UnselectAll();
                    SfxThumbnailListBox?.UnselectAll();
                }
                UpdateSelectionActionToolbarUI();
            }

            _clickedEmptySpaceInSubfolders = false;
            _clickedEmptySpaceInMediaList = false;
        }

        private void EndMarqueeSelection()
        {
            _isMarqueeSelecting = false;
            _isMarqueeCandidate = false;
            if (MarqueeSelectionCanvas != null)
            {
                if (MarqueeSelectionCanvas.IsMouseCaptured)
                {
                    MarqueeSelectionCanvas.ReleaseMouseCapture();
                }
                MarqueeSelectionCanvas.Visibility = Visibility.Collapsed;
            }
            if (MarqueeSelectionBorder != null)
            {
                MarqueeSelectionBorder.Visibility = Visibility.Collapsed;
                MarqueeSelectionBorder.Width = 0;
                MarqueeSelectionBorder.Height = 0;
            }
            UpdateSelectionActionToolbarUI();
        }

        private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var selectedList = GetSelectedSfxList();
            if (selectedList.Count > 1)
            {
                var paths = selectedList.Where(x => File.Exists(x.FilePath)).Select(x => x.FilePath).ToArray();
                if (paths.Length > 0) StartFilesDrag(paths);
            }
            else if (_selectedItem != null && File.Exists(_selectedItem.FilePath))
            {
                // If user selected In/Out, export trimmed WAV snippet first!
                string fileToDrag = _selectedItem.FilePath;
                if (_audioPlayer.HasSelection)
                {
                    string? trimmed = _audioPlayer.ExportTrimmedWav(_selectedItem.FilePath, _audioPlayer.InPosition, _audioPlayer.OutPosition);
                    if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed))
                    {
                        fileToDrag = trimmed;
                    }
                }

                StartFilesDrag(new[] { fileToDrag });
            }
        }

        private void StartFilesDrag(string[] filePaths)
        {
            try
            {
                _isInternalFileDrag = true;
                _deferredSingleSelectOnMouseUp = null;
                var compatiblePaths = filePaths.Select(p => _audioPlayer.EnsureDragCompatibleFile(p)).ToArray();
                var data = new DataObject(DataFormats.FileDrop, compatiblePaths);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DragDrop error: {ex.Message}");
            }
            finally
            {
                _isInternalFileDrag = false;
                _deferredSingleSelectOnMouseUp = null;
                HideExternalDropOverlay();
                ClearAllDropTargets(_rootFolders);
            }
        }

        #endregion

        #region Actions: Rescan & Refresh

        private async void RescanBtn_Click(object sender, RoutedEventArgs e)
        {
            RescanBtn.IsEnabled = false;
            try
            {
                await RefreshCurrentViewAsync();
                SetupLinkedFolderWatchers();
            }
            finally
            {
                RescanBtn.IsEnabled = true;
            }
        }

        private async void RefreshCurrentViewBtn_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCurrentViewAsync();
        }

        private async Task RefreshCurrentViewAsync()
        {
            try
            {
                if (RefreshCurrentViewBtn != null) RefreshCurrentViewBtn.IsEnabled = false;

                string? currentBinId = _selectedVirtualFolder?.Id;
                string? prevSelectedFilePath = _selectedItem?.FilePath;

                VirtualFolderNode? currentNode = currentBinId != null ? FindNodeInTree(_rootFolders, currentBinId) : null;
                bool hasLinkedFolder = currentNode != null && !string.IsNullOrWhiteSpace(currentNode.LinkedPath) && Directory.Exists(currentNode.LinkedPath);

                // 1. Sync linked folders:
                if (hasLinkedFolder && currentNode != null)
                {
                    await _dataManager.SyncVirtualFolderAsync(currentNode.Id, _allFiles);
                }
                else
                {
                    await _dataManager.SyncAllLinkedFoldersAsync(_allFiles, autoOnly: false);
                }

                // 2. High-speed parallel disk verification (takes ~30ms for 11k+ files)
                var allPathsToCheck = _allFiles
                    .Select(f => f.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var deadBag = new System.Collections.Concurrent.ConcurrentBag<string>();
                await Task.Run(() =>
                {
                    Parallel.ForEach(allPathsToCheck, new ParallelOptions { MaxDegreeOfParallelism = 32 }, p =>
                    {
                        try
                        {
                            string? root = Path.GetPathRoot(p);
                            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                            {
                                if (!File.Exists(p))
                                {
                                    deadBag.Add(p);
                                }
                            }
                        }
                        catch { }
                    });
                });

                if (deadBag.Count > 0)
                {
                    var deadSet = new HashSet<string>(deadBag, StringComparer.OrdinalIgnoreCase);
                    lock (_allFiles)
                    {
                        _allFiles.RemoveAll(f => deadSet.Contains(f.FilePath) || deadSet.Contains(f.FilePathNorm));
                    }
                    foreach (var vf in _dataManager.Settings.VirtualFolders)
                    {
                        vf.Files.RemoveAll(f => deadSet.Contains(f));
                    }
                    foreach (var dead in deadSet)
                    {
                        _dataManager.Settings.Tags.Remove(dead);
                        _dataManager.Settings.Tags.Remove(dead.Replace('/', '\\'));
                    }
                    _ = _dataManager.SaveSettingsAsync();
                    _ = _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                }

                // 3. Stop audio if selected item was deleted
                if (!string.IsNullOrEmpty(prevSelectedFilePath) && !File.Exists(prevSelectedFilePath))
                {
                    _selectedItem = null;
                    _audioPlayer.Stop();
                }

                // 4. Update UI counts & tag chips
                AllCountBadge.Text = _allFiles.Count.ToString("N0");
                UntaggedCountBadge.Text = _allFiles.Count(f => f.Tags.Count == 0).ToString("N0");
                BuildTagChips();
                BuildFolderTree();

                // 5. Restore current folder selection
                if (currentBinId != null)
                {
                    _selectedVirtualFolder = FindNodeInTree(_rootFolders, currentBinId);
                    if (_selectedVirtualFolder != null)
                    {
                        _selectedVirtualFolder.IsSelected = true;
                    }
                }

                // 6. Invalidate filter cache & re-apply current filter
                InvalidateFilterCache();
                ApplyFilter();
                UpdateHeaderCacheBadge();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshCurrentViewAsync error: {ex.Message}");
            }
            finally
            {
                if (RefreshCurrentViewBtn != null) RefreshCurrentViewBtn.IsEnabled = true;
            }
        }

        #endregion

        #region Cache Manager Modal

        private static string FormatCacheBytes(long bytes)
        {
            if (bytes <= 0) return "0 KB";
            double kb = bytes / 1024.0;
            if (kb < 1024.0) return $"{kb:0.0} KB";
            double mb = kb / 1024.0;
            if (mb < 1024.0) return $"{mb:0.00} MB";
            double gb = mb / 1024.0;
            return $"{gb:0.00} GB";
        }

        private void UpdateHeaderCacheBadge()
        {
            try
            {
                if (HeaderCacheSizeText == null) return;
                var stats = _dataManager.GetCacheStats(_allFiles.Count);
                HeaderCacheSizeText.Text = FormatCacheBytes(stats.TotalBytes);
            }
            catch { }
        }

        private void RefreshCacheManageModalUI()
        {
            var stats = _dataManager.GetCacheStats(_allFiles.Count);
            if (HeaderCacheSizeText != null)
            {
                HeaderCacheSizeText.Text = FormatCacheBytes(stats.TotalBytes);
            }

            if (CacheDirPathInput != null)
            {
                CacheDirPathInput.Text = stats.ActiveCacheDirectory;
            }

            if (CacheLocationBadgeText != null)
            {
                CacheLocationBadgeText.Text = stats.IsCustomDirectory ? "Tùy chỉnh" : "Mặc định (AppData)";
            }

            if (ResetDefaultCacheDirBtn != null)
            {
                ResetDefaultCacheDirBtn.Visibility = stats.IsCustomDirectory ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CacheTotalSizeText != null)
            {
                CacheTotalSizeText.Text = FormatCacheBytes(stats.TotalBytes);
            }

            // Update 3-segment proportional storage bar
            if (CacheWaveBarCol != null && CacheIndexBarCol != null && CacheVideoBarCol != null && CacheEmptyBarCol != null)
            {
                if (stats.TotalBytes <= 0)
                {
                    CacheWaveBarCol.Width = new GridLength(0, GridUnitType.Star);
                    CacheIndexBarCol.Width = new GridLength(0, GridUnitType.Star);
                    CacheVideoBarCol.Width = new GridLength(0, GridUnitType.Star);
                    CacheEmptyBarCol.Width = new GridLength(1, GridUnitType.Star);
                }
                else
                {
                    double total = Math.Max(1.0, stats.TotalBytes);
                    double wfRatio = stats.WaveformBytes > 0 ? Math.Max(0.04, stats.WaveformBytes / total) : 0;
                    double idxRatio = stats.IndexBytes > 0 ? Math.Max(0.04, stats.IndexBytes / total) : 0;
                    double vidRatio = stats.VideoBytes > 0 ? Math.Max(0.04, stats.VideoBytes / total) : 0;

                    CacheWaveBarCol.Width = new GridLength(wfRatio, GridUnitType.Star);
                    CacheIndexBarCol.Width = new GridLength(idxRatio, GridUnitType.Star);
                    CacheVideoBarCol.Width = new GridLength(vidRatio, GridUnitType.Star);
                    CacheEmptyBarCol.Width = new GridLength(0, GridUnitType.Star);
                }
            }

            int totalAudioCount = _allFiles.Count(x => !x.IsVideo);
            if (CacheWaveformStatsText != null)
            {
                CacheWaveformStatsText.Text = $"{stats.WaveformFileCount:N0} / {totalAudioCount:N0} bài đã lưu sóng âm • {FormatCacheBytes(stats.WaveformBytes)}";
            }

            if (CacheIndexStatsText != null)
            {
                CacheIndexStatsText.Text = $"{stats.IndexEntryCount:N0} mục âm thanh & video • {FormatCacheBytes(stats.IndexBytes)}";
            }

            if (CacheVideoStatsText != null)
            {
                CacheVideoStatsText.Text = $"{stats.VideoFileCount:N0} ảnh khung hình xem trước • {FormatCacheBytes(stats.VideoBytes)}";
            }

            if (CacheRamStatsText != null)
            {
                CacheRamStatsText.Text = $"Đang lưu {_filterResultCache.Count} bộ lọc trong RAM • Chuyển Bin & Tag 0ms";
            }
        }

        private void CacheManageBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshCacheManageModalUI();
            if (CacheManageModal != null)
            {
                CacheManageModal.Visibility = Visibility.Visible;
            }
        }

        private void CloseCacheManageModal_Click(object sender, RoutedEventArgs e)
        {
            if (CacheManageModal != null)
            {
                CacheManageModal.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenCacheDirBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = _dataManager.GetActiveCacheRootDirectory();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ZDialog.Show($"Không thể mở thư mục Cache: {ex.Message}", "Lỗi mở thư mục", ZDialogKind.Error, owner: this);
            }
        }

        private async void BrowseCacheDirBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Chọn thư mục muốn lưu Cache (Sóng âm, Chỉ mục & Khung hình Video)",
                Multiselect = false
            };

            string currentDir = _dataManager.GetActiveCacheRootDirectory();
            if (Directory.Exists(currentDir))
            {
                dialog.InitialDirectory = currentDir;
            }

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                string chosenDir = dialog.FolderName;
                var (ok, msg) = await _dataManager.ChangeCacheDirectoryAsync(chosenDir, moveExistingFiles: true);
                if (ok)
                {
                    ApplyConfiguredCacheDirectories();
                    await _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                    RefreshCacheManageModalUI();
                    ZDialog.Show($"{msg}\n\nToàn bộ dữ liệu Cache hiện có đã được chuyển sang địa chỉ mới thành công!", "Đổi địa chỉ lưu Cache", ZDialogKind.Success, owner: this);
                }
                else
                {
                    ZDialog.Show(msg, "Không thể đổi thư mục Cache", ZDialogKind.Error, owner: this);
                }
            }
        }

        private async void ResetDefaultCacheDirBtn_Click(object sender, RoutedEventArgs e)
        {
            string defaultDir = _dataManager.GetDefaultCacheRootDirectory();
            if (!ZDialog.Confirm($"Đặt lại địa chỉ lưu Cache về thư mục mặc định?\n\n{defaultDir}\n\nToàn bộ dữ liệu Cache hiện có sẽ tự động được chuyển về lại thư mục mặc định.", "Đặt lại mặc định", ZDialogKind.Info, "Đặt về mặc định", "Hủy", this))
            {
                return;
            }

            var (ok, msg) = await _dataManager.ChangeCacheDirectoryAsync(null, moveExistingFiles: true);
            if (ok)
            {
                ApplyConfiguredCacheDirectories();
                await _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                RefreshCacheManageModalUI();
            }
            else
            {
                ZDialog.Show(msg, "Lỗi", ZDialogKind.Error, owner: this);
            }
        }

        private async void PrebuildWaveformBtn_Click(object sender, RoutedEventArgs e)
        {
            var audioItems = _allFiles.Where(x => !x.IsVideo && File.Exists(x.FilePath)).ToList();
            if (audioItems.Count == 0)
            {
                ZDialog.Show("Không có file âm thanh nào trong thư viện để tạo Cache sóng âm.", "Thông báo", ZDialogKind.Info, owner: this);
                return;
            }

            _prebuildWaveformCts?.Cancel();
            _prebuildWaveformCts = new System.Threading.CancellationTokenSource();
            var ct = _prebuildWaveformCts.Token;

            PrebuildWaveformBtn.IsEnabled = false;
            CachePrebuildProgressPanel.Visibility = Visibility.Visible;
            CachePrebuildProgressBar.Maximum = audioItems.Count;
            CachePrebuildProgressBar.Value = 0;

            int completed = 0;
            bool anyDurationUpdated = false;

            try
            {
                foreach (var item in audioItems)
                {
                    if (ct.IsCancellationRequested) break;

                    completed++;
                    if (completed == 1 || completed % 15 == 0 || completed == audioItems.Count)
                    {
                        CachePrebuildProgressBar.Value = completed;
                        CachePrebuildStatusText.Text = $"Đang tạo sóng âm ({completed:N0}/{audioItems.Count:N0}): {item.FileName}";
                    }

                    if (_audioPlayer.HasCachedWaveformOnDisk(item.FilePath))
                    {
                        continue;
                    }

                    var (success, dur) = await _audioPlayer.PrebuildWaveformForFileAsync(item.FilePath, ct);
                    if (success && dur > 0 && item.Duration <= 0)
                    {
                        item.Duration = dur;
                        anyDurationUpdated = true;
                    }

                    if (completed % 50 == 0)
                    {
                        RefreshCacheManageModalUI();
                    }
                }

                if (anyDurationUpdated)
                {
                    await _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                PrebuildWaveformBtn.IsEnabled = true;
                CachePrebuildProgressPanel.Visibility = Visibility.Collapsed;
                RefreshCacheManageModalUI();
            }
        }

        private void CancelPrebuildWaveformBtn_Click(object sender, RoutedEventArgs e)
        {
            _prebuildWaveformCts?.Cancel();
        }

        private async void ClearWaveformCacheBtn_Click(object sender, RoutedEventArgs e)
        {
            _prebuildWaveformCts?.Cancel();
            await _dataManager.ClearCacheAsync(clearWaveform: true, clearVideo: false, clearIndex: false);
            _audioPlayer.ClearMemoryCache();
            RefreshCacheManageModalUI();
        }

        private async void RebuildIndexCacheBtn_Click(object sender, RoutedEventArgs e)
        {
            await _dataManager.SaveLibraryIndexCacheAsync(_allFiles);
            InvalidateFilterCache();
            RefreshCacheManageModalUI();
            ZDialog.Show($"Đã cập nhật lại Chỉ mục Thư viện ({_allFiles.Count:N0} mục) thành công!", "Cập nhật Chỉ mục", ZDialogKind.Success, owner: this);
        }

        private async void ClearVideoCacheBtn_Click(object sender, RoutedEventArgs e)
        {
            await _dataManager.ClearCacheAsync(clearWaveform: false, clearVideo: true, clearIndex: false);
            _videoPreviewService.ClearMemoryCache();
            RefreshCacheManageModalUI();
        }

        private async void ClearAllCacheBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!ZDialog.Confirm("Bạn có chắc muốn xóa toàn bộ bộ nhớ đệm Cache (Sóng âm, Khung hình Video & Chỉ mục)?\n\nLưu ý: Các file nhạc/video gốc của bạn hoàn toàn không bị ảnh hưởng.", "Xóa toàn bộ Cache", ZDialogKind.Warning, "Xóa Cache", "Hủy", this))
            {
                return;
            }

            _prebuildWaveformCts?.Cancel();
            await _dataManager.ClearCacheAsync(clearWaveform: true, clearVideo: true, clearIndex: true);
            _audioPlayer.ClearMemoryCache();
            _videoPreviewService.ClearMemoryCache();
            InvalidateFilterCache();
            RefreshCacheManageModalUI();
        }

        #endregion

        #region In-App OTA Update & Admin Publish

        private void RefreshHeaderUpdateButtonUI()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshHeaderUpdateButtonUI));
                return;
            }

            if (HeaderVersionText == null || UpdateBtnText == null || UpdateManageBtn == null) return;

            string curVer = _updateService.GetCurrentVersion();
            if (_lastUpdateCheckResult != null && _lastUpdateCheckResult.IsUpdateAvailable && _lastUpdateCheckResult.LatestManifest != null)
            {
                string newVer = _lastUpdateCheckResult.LatestManifest.Version;
                UpdateBtnText.Text = "✨ Có bản cập nhật mới!";
                UpdateBtnText.Foreground = new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69));
                HeaderVersionText.Text = $"v{curVer} ➔ v{newVer}";
                HeaderVersionText.Foreground = Brushes.White;
                HeaderVersionBadge.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x38, 0x24));
                HeaderVersionBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4E));
                UpdateManageBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4E));
            }
            else
            {
                UpdateBtnText.Text = "Kiểm tra cập nhật";
                UpdateBtnText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                HeaderVersionText.Text = $"v{curVer}";
                HeaderVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                HeaderVersionBadge.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));
                HeaderVersionBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                UpdateManageBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
            }
        }

        private async Task CheckForAppUpdateSilentlyOnStartupAsync()
        {
            try
            {
                await Task.Delay(2500);
                var res = await _updateService.CheckForUpdateAsync();
                if (res.IsSuccess)
                {
                    _lastUpdateCheckResult = res;
                    Dispatcher.BeginInvoke(new Action(RefreshHeaderUpdateButtonUI));
                }
            }
            catch { }
        }

        private async void UpdateManageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateManageModal == null) return;

            string curVer = _updateService.GetCurrentVersion();
            UpdateCurrentVersionText.Text = $"v{curVer}";

            var cfg = _updateService.LoadSourceConfig();
            PublishVersionInput.Text = curVer;
            PublishManifestUrlInput.Text = cfg.ManifestUrl;
            PublishPackageUrlInput.Text = cfg.PackageUrl;

            var localManifest = _updateService.GetLocalManifest();
            if (localManifest != null && !string.IsNullOrWhiteSpace(localManifest.ReleaseNotes))
            {
                PublishNotesInput.Text = localManifest.ReleaseNotes;
            }

            // Show Admin Publish section prominently if running on Admin/Creator workspace
            bool isAdminMachine = !DataManager.IsStandaloneInstall ||
                                  !string.IsNullOrEmpty(_updateService.ResolveLocalUpdatesDirectory(cfg.SharedUpdatesFolder));
            AdminPublishExpander.Visibility = isAdminMachine ? Visibility.Visible : Visibility.Collapsed;

            UpdateManageModal.Visibility = Visibility.Visible;
            await PerformUpdateCheckAndRefreshModalAsync();
        }

        private void CloseUpdateManageModal_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateManageModal != null)
            {
                UpdateManageModal.Visibility = Visibility.Collapsed;
            }
        }

        private async void CheckUpdateNowBtn_Click(object sender, RoutedEventArgs e)
        {
            await PerformUpdateCheckAndRefreshModalAsync();
        }

        private async Task PerformUpdateCheckAndRefreshModalAsync()
        {
            CheckUpdateNowBtn.IsEnabled = false;
            UpdateLatestVersionText.Text = "Đang kiểm tra...";
            UpdateLatestVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            UpdateStatusHeadlineText.Text = "Đang kết nối kiểm tra phiên bản mới...";
            UpdateStatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            ApplyUpdateNowBtn.Visibility = Visibility.Collapsed;

            try
            {
                string curVer = _updateService.GetCurrentVersion();
                UpdateCurrentVersionText.Text = $"v{curVer}";

                var res = await _updateService.CheckForUpdateAsync();
                _lastUpdateCheckResult = res;
                RefreshHeaderUpdateButtonUI();

                var localManifest = _updateService.GetLocalManifest();

                if (!res.IsSuccess || res.LatestManifest == null)
                {
                    UpdateLatestVersionText.Text = $"v{curVer}";
                    UpdateLatestVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                    UpdateStatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1D, 0x16));
                    UpdateStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x20));
                    UpdateStatusHeadlineText.Text = "Chưa có gói cập nhật nào được phát hành";
                    UpdateStatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    UpdatePublishedTimeText.Visibility = Visibility.Collapsed;
                    UpdateReleaseNotesText.Text = res.ErrorMessage ?? "Bạn đang ở phiên bản hiện hành.";
                    if (localManifest != null && !string.IsNullOrWhiteSpace(localManifest.ReleaseNotes))
                    {
                        PublishNotesInput.Text = localManifest.ReleaseNotes;
                    }
                    return;
                }

                var manifest = res.LatestManifest;
                UpdateLatestVersionText.Text = $"v{manifest.Version}";
                UpdateSourceFooterText.Text = $"Nguồn cập nhật: {res.SourceDescription ?? "Z-Station Studio"}";

                if (!string.IsNullOrWhiteSpace(manifest.PublishedAt))
                {
                    UpdatePublishedTimeText.Text = $"Phát hành lúc: {manifest.PublishedAt}" +
                        (manifest.PackageSizeBytes > 0 ? $" • Dung lượng: {manifest.PackageSizeBytes / (1024.0 * 1024.0):0.0} MB" : "");
                    UpdatePublishedTimeText.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdatePublishedTimeText.Visibility = Visibility.Collapsed;
                }

                UpdateReleaseNotesText.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
                    ? "Bản cập nhật cải thiện hiệu năng và bổ sung tính năng mới."
                    : manifest.ReleaseNotes;

                // Sync release notes into PublishNotesInput so Admin sees exactly what is published
                if (localManifest != null && !string.IsNullOrWhiteSpace(localManifest.ReleaseNotes))
                {
                    PublishNotesInput.Text = localManifest.ReleaseNotes;
                }
                else if (!string.IsNullOrWhiteSpace(manifest.ReleaseNotes))
                {
                    PublishNotesInput.Text = manifest.ReleaseNotes;
                }

                if (res.IsUpdateAvailable)
                {
                    UpdateLatestVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69));
                    UpdateStatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x29, 0x1E));
                    UpdateStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x5E, 0x3E));
                    UpdateStatusHeadlineText.Text = $"✨ Đã có bản cập nhật mới v{manifest.Version}! Nhấn nút bên dưới để cập nhật ngay.";
                    UpdateStatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69));

                    ApplyUpdateNowBtnText.Text = $"CẬP NHẬT LÊN v{manifest.Version} & TỰ ĐỘNG KHỞI ĐỘNG LẠI";
                    ApplyUpdateNowBtn.Visibility = Visibility.Visible;
                }
                else if (res.HasUnpublishedLocalBuild)
                {
                    PublishVersionInput.Text = curVer;
                    UpdateLatestVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    UpdateStatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1D, 0x16));
                    UpdateStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x20));
                    UpdateStatusHeadlineText.Text = $"⚡ Máy bạn có bản build mới (v{curVer}). Bấm 'Đóng gói & Phát hành' bên dưới để đẩy lên GitHub cho khách!";
                    UpdateStatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    ApplyUpdateNowBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    PublishVersionInput.Text = UpdateService.IncrementPatchVersion(curVer);
                    UpdateLatestVersionText.Foreground = Brushes.White;
                    UpdateStatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
                    UpdateStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
                    UpdateStatusHeadlineText.Text = $"✓ Bạn đang sử dụng phiên bản mới nhất (v{curVer}).";
                    UpdateStatusHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(0x46, 0xD3, 0x69));
                    ApplyUpdateNowBtn.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                CheckUpdateNowBtn.IsEnabled = true;
            }
        }

        private async void ApplyUpdateNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastUpdateCheckResult == null || _lastUpdateCheckResult.LatestManifest == null)
                return;

            ApplyUpdateNowBtn.IsEnabled = false;
            CheckUpdateNowBtn.IsEnabled = false;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = 5;
            UpdateProgressStatusText.Text = "Đang chuẩn bị tải bản cập nhật...";

            var (success, message) = await _updateService.DownloadAndApplyUpdateAsync(
                _lastUpdateCheckResult,
                (pct, statusText) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateProgressBar.Value = pct;
                        UpdateProgressStatusText.Text = statusText;
                    }));
                });

            if (success)
            {
                await Task.Delay(500);
                Application.Current.Shutdown();
            }
            else
            {
                ApplyUpdateNowBtn.IsEnabled = true;
                CheckUpdateNowBtn.IsEnabled = true;
                UpdateProgressPanel.Visibility = Visibility.Collapsed;
                ZDialog.Show(message, "Không thể cập nhật", ZDialogKind.Warning, owner: this);
            }
        }

        private void OpenUpdatesFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _updateService.LoadSourceConfig();
            string dir = _updateService.ResolveLocalUpdatesDirectory(cfg.SharedUpdatesFolder)
                ?? Path.Combine(_updateService.BaseDir, "Updates");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }

        private async void PublishUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            string targetVer = PublishVersionInput.Text.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(targetVer))
            {
                ZDialog.Show("Vui lòng nhập số phiên bản mới (ví dụ: 1.2.1).", "Thiếu số phiên bản", ZDialogKind.Warning, owner: this);
                return;
            }

            PublishUpdateBtn.IsEnabled = false;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = 10;
            UpdateProgressStatusText.Text = "Đang đóng gói phiên bản mới...";

            try
            {
                var (success, msg, publishedVer) = await _updateService.PublishCurrentBuildAsUpdateAsync(
                    targetVer,
                    PublishNotesInput.Text,
                    PublishManifestUrlInput.Text,
                    PublishPackageUrlInput.Text,
                    (pct, status) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            UpdateProgressBar.Value = pct;
                            UpdateProgressStatusText.Text = status;
                        }));
                    });

                UpdateProgressPanel.Visibility = Visibility.Collapsed;

                if (success)
                {
                    RefreshHeaderUpdateButtonUI();
                    await PerformUpdateCheckAndRefreshModalAsync();
                    PublishVersionInput.Text = UpdateService.IncrementPatchVersion(publishedVer);
                    ZDialog.Show(msg + "\n\nNhững máy khác khi bấm 'Kiểm tra cập nhật' sẽ nhận được bản v" + publishedVer + " ngay!", "Phát hành thành công", ZDialogKind.Success, owner: this);
                }
                else
                {
                    ZDialog.Show(msg, "Lỗi phát hành", ZDialogKind.Danger, owner: this);
                }
            }
            finally
            {
                PublishUpdateBtn.IsEnabled = true;
            }
        }

        #endregion

        #region Global Keyboard Shortcuts

        private async void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Do not intercept if any modal dialog is open
            if ((TagManageModal != null && TagManageModal.Visibility == Visibility.Visible) ||
                (BinLinkModal != null && BinLinkModal.Visibility == Visibility.Visible) ||
                (PickBinModal != null && PickBinModal.Visibility == Visibility.Visible) ||
                (CacheManageModal != null && CacheManageModal.Visibility == Visibility.Visible) ||
                (UpdateManageModal != null && UpdateManageModal.Visibility == Visibility.Visible))
            {
                return;
            }

            // Ctrl + N: Create new virtual folder Bin anywhere in the main window
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.N)
            {
                e.Handled = true;
                CreateBinBtn_Click(sender, e);
                return;
            }

            // Ctrl + T: Toggle Right Sidebar (Tag Nhanh)
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.T)
            {
                e.Handled = true;
                ToggleRightSidebarBtn_Click(sender, e);
                return;
            }

            // Ctrl + R or F5: Refresh Current View & Files on disk
            if (((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.R) || e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshCurrentViewAsync();
                return;
            }

            // Do not intercept if typing in SearchBox or any input textboxes (except Down/Up in SearchBox to jump to list & play)
            if (Keyboard.FocusedElement is TextBox)
            {
                if (SearchBox.IsFocused && (e.Key == Key.Down || e.Key == Key.Up) && _visibleFiles.Count > 0)
                {
                    e.Handled = true;
                    if (_isThumbnailViewMode) SfxThumbnailListBox?.Focus();
                    else SfxListView?.Focus();
                    await NavigateAndPlayByOffsetAsync(e.Key == Key.Down ? 1 : -1);
                }
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (GetSelectedSubfolderCards().Count > 0 || GetSelectedSfxList().Count > 0)
                {
                    e.Handled = true;
                    await DeleteCurrentSelectionWithConfirmAsync();
                    return;
                }
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.A)
            {
                e.Handled = true;
                ToggleSelectAllVisibleItems();
                return;
            }
            else if (e.Key == Key.Escape)
            {
                if (_isMarqueeSelecting || GetSelectedSubfolderCards().Count > 0 || GetSelectedSfxList().Count > 1)
                {
                    EndMarqueeSelection();
                    ClearSubfolderCardSelection();
                    SfxListView?.UnselectAll();
                    SfxThumbnailListBox?.UnselectAll();
                    UpdateSelectionActionToolbarUI();
                    e.Handled = true;
                    return;
                }
            }

            if ((e.Key == Key.Down || e.Key == Key.Up) && _visibleFiles != null && _visibleFiles.Count > 0)
            {
                e.Handled = true;
                await NavigateAndPlayByOffsetAsync(e.Key == Key.Down ? 1 : -1);
            }
            else if (e.Key == Key.Space)
            {
                e.Handled = true;
                await TogglePlayPauseAsync();
            }
            else if (e.Key == Key.I)
            {
                SetInBtn_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.O)
            {
                SetOutBtn_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                LoopBtn_Click(sender, e);
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        #endregion

    }
}
