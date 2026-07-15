using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AuroraSuite
{
    public enum SyncStatus
    {
        Pending,
        NotMatchedOnConsole,
        Syncing,
        Synced,
        SyncedWithSkips,
        Failed,
    }

    /// <summary>
    /// One row of the Sync tab's library grid: a single local Title ID folder, which
    /// asset files it actually has, whether the person wants it included in this sync
    /// run, and how that sync attempt went. Mirrors XenonArchivist's GameMatchRow
    /// pattern - INotifyPropertyChanged so the grid updates live while syncing runs on
    /// a background thread, no manual refresh needed.
    /// </summary>
    public class LibraryRow : INotifyPropertyChanged
    {
        public string TitleId { get; }
        public string FolderPath { get; }
        public bool HasIcon { get; }
        public bool HasBoxart { get; }
        public bool HasBackground { get; }
        public bool HasScreenshots { get; }

        public string AssetsSummary
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (HasIcon) parts.Add("GL");
                if (HasBoxart) parts.Add("GC");
                if (HasBackground) parts.Add("BK");
                if (HasScreenshots) parts.Add("SS");
                return parts.Count > 0 ? string.Join(" ", parts) : "(none found)";
            }
        }

        public LibraryRow(string titleId, string folderPath, bool hasIcon, bool hasBoxart, bool hasBackground, bool hasScreenshots)
        {
            TitleId = titleId;
            FolderPath = folderPath;
            HasIcon = hasIcon;
            HasBoxart = hasBoxart;
            HasBackground = hasBackground;
            HasScreenshots = hasScreenshots;
        }

        private bool _include;
        /// <summary>Whether this row is queued for the next Sync Selected run. Defaults
        /// to false - with a library that can run into the thousands of rows, a
        /// default of true would mean everything not scrolled into view and manually
        /// unchecked still gets synced, which is exactly the "it synced my whole
        /// library, not just what I ticked" surprise this avoids.</summary>
        public bool Include
        {
            get => _include;
            set { _include = value; OnPropertyChanged(); }
        }

        private SyncStatus _status = SyncStatus.Pending;
        public SyncStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusText => Status switch
        {
            SyncStatus.Pending => "Pending",
            SyncStatus.NotMatchedOnConsole => "No match on console",
            SyncStatus.Syncing => "Syncing...",
            SyncStatus.Synced => "Synced",
            SyncStatus.SyncedWithSkips => "Synced (some skipped)",
            SyncStatus.Failed => "Failed",
            _ => Status.ToString(),
        };

        private string _detail = "";
        /// <summary>Per-row error/skip reporting - what actually happened to this
        /// title's files on the last sync attempt.</summary>
        public string Detail
        {
            get => _detail;
            set { _detail = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
