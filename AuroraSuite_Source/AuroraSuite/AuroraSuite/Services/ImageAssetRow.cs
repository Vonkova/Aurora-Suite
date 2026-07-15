using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AuroraSuite
{
    public enum ConvertStatus
    {
        Pending,
        NothingDetected,
        Converting,
        Converted,
        ConvertedWithSkips,
        Failed,
    }

    /// <summary>
    /// One row of the Image Assets tab's grid: a single detected title folder, which
    /// images were found for it (from ImageAssetConverter's detection pass), whether
    /// the person wants it included in this conversion run, and how that attempt
    /// went. Mirrors LibraryRow/GameMatchRow's INotifyPropertyChanged pattern so the
    /// grid updates live while conversion runs on a background thread.
    /// </summary>
    public class ImageAssetRow : INotifyPropertyChanged
    {
        public string TitleId { get; }
        public string FolderPath { get; }
        public string? IconPath { get; }
        public string? BoxartPath { get; }
        public string? BackgroundPath { get; }
        public List<string> ScreenshotPaths { get; }

        public string DetectedSummary
        {
            get
            {
                var parts = new List<string>();
                if (IconPath != null) parts.Add("Icon");
                if (BoxartPath != null) parts.Add("Boxart");
                if (BackgroundPath != null) parts.Add("Background");
                if (ScreenshotPaths.Count > 0) parts.Add($"Screenshots x{ScreenshotPaths.Count}");
                return parts.Count > 0 ? string.Join(", ", parts) : "(nothing detected)";
            }
        }

        public ImageAssetRow(string titleId, string folderPath, string? iconPath, string? boxartPath,
            string? backgroundPath, List<string> screenshotPaths)
        {
            TitleId = titleId;
            FolderPath = folderPath;
            IconPath = iconPath;
            BoxartPath = boxartPath;
            BackgroundPath = backgroundPath;
            ScreenshotPaths = screenshotPaths;
        }

        private bool _include;
        /// <summary>Defaults to false - see LibraryRow.Include for why a default of
        /// true is unsafe once a library runs into the thousands of rows.</summary>
        public bool Include
        {
            get => _include;
            set { _include = value; OnPropertyChanged(); }
        }

        private ConvertStatus _status = ConvertStatus.Pending;
        public ConvertStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusText => Status switch
        {
            ConvertStatus.Pending => "Pending",
            ConvertStatus.NothingDetected => "Nothing detected",
            ConvertStatus.Converting => "Converting...",
            ConvertStatus.Converted => "Converted",
            ConvertStatus.ConvertedWithSkips => "Converted (some skipped)",
            ConvertStatus.Failed => "Failed",
            _ => Status.ToString(),
        };

        private string _detail = "";
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
