using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZStationStudio.Models
{
    public class VirtualFolderNode : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? ParentId { get; set; }

        private string? _linkedPath;
        public string? LinkedPath
        {
            get => _linkedPath;
            set
            {
                if (_linkedPath != value)
                {
                    _linkedPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLinkedPath));
                    OnPropertyChanged(nameof(LinkedTooltip));
                }
            }
        }

        private bool _autoSync = true;
        public bool AutoSync
        {
            get => _autoSync;
            set
            {
                if (_autoSync != value)
                {
                    _autoSync = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LinkedTooltip));
                }
            }
        }

        public bool HasLinkedPath => !string.IsNullOrWhiteSpace(LinkedPath);

        public string LinkedTooltip => HasLinkedPath
            ? $"🔗 Đã liên kết ổ đĩa: {LinkedPath}\n{(AutoSync ? "✓ Tự động cập nhật file mới (Auto-Sync: BẬT)" : "⏸ Tự động cập nhật đang tắt")}\n(Bấm để chỉnh sửa hoặc đổi thư mục liên kết)"
            : "Chưa liên kết thư mục ổ đĩa (Bấm 🔗 để liên kết thư mục tự động cập nhật file mới)";

        public HashSet<string> DirectFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllDescendantFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<VirtualFolderNode> Children { get; set; } = new();

        public int DirectCount => DirectFiles.Count;
        public int TotalCount => AllDescendantFiles.Count;

        private bool _includeSubfoldersInCount;
        public bool IncludeSubfoldersInCount
        {
            get => _includeSubfoldersInCount;
            set
            {
                if (_includeSubfoldersInCount != value)
                {
                    _includeSubfoldersInCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CountDisplay));
                }
            }
        }

        public string CountDisplay => (_includeSubfoldersInCount || DirectCount == 0)
            ? TotalCount.ToString("N0")
            : DirectCount.ToString("N0");

        public string SubfolderSummaryText
        {
            get
            {
                if (Children.Count > 0 && DirectCount > 0)
                    return $"{Children.Count} thư mục • {TotalCount:N0} media";
                if (Children.Count > 0)
                    return $"{Children.Count} thư mục con • {TotalCount:N0} media";
                return $"{TotalCount:N0} media";
            }
        }

        public bool HasChildren => Children.Count > 0;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FolderIcon));
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing != value)
                {
                    _isEditing = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDropTarget;
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget != value)
                {
                    _isDropTarget = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isCardSelected;
        public bool IsCardSelected
        {
            get => _isCardSelected;
            set
            {
                if (_isCardSelected != value)
                {
                    _isCardSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FolderIcon => (IsExpanded && HasChildren) ? "📂" : "📁";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
