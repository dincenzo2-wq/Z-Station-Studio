using System.Collections.Generic;

namespace ZStationStudio.Models
{
    public class VirtualFolder
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public string? LinkedPath { get; set; }
        public bool AutoSync { get; set; } = true;
        public List<string> Files { get; set; } = new();
        public bool IsOpen { get; set; } = false;

        public List<VirtualFolder> Children { get; set; } = new();
        public int TotalFileCount { get; set; }
    }

    public class LibraryEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public class AppSettings
    {
        public List<LibraryEntry> Libraries { get; set; } = new();
        public List<string> CustomTags { get; set; } = new();
        public List<string> DeletedTags { get; set; } = new();
        public List<string> DeletedFolderIds { get; set; } = new();
        public List<string> DeletedFolderPaths { get; set; } = new();
        public Dictionary<string, List<string>> Tags { get; set; } = new();
        public List<VirtualFolder> VirtualFolders { get; set; } = new();
        public List<string> Favorites { get; set; } = new();
        public List<string> Recent { get; set; } = new();
        public double Volume { get; set; } = 1.0;
        public bool IsMuted { get; set; } = false;
        public bool IsLooping { get; set; } = false;
        public bool AlwaysOnTop { get; set; } = true;
        public bool AutoPreview { get; set; } = true;
        public string? CacheDirectory { get; set; }
        public string? CustomTagSyncFilePath { get; set; }
    }

    public class CachedMediaEntry
    {
        public string Path { get; set; } = string.Empty;
        public double Duration { get; set; }
        public long Size { get; set; }
        public string RelativePath { get; set; } = string.Empty;
    }
}
