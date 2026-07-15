using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace AuroraSuite
{
    /// <summary>
    /// Lets the user click through the console's own folder tree over FTP instead of
    /// typing the Aurora GameData path by hand - useful since not everyone's Aurora
    /// install sits at the same predefined path (different drive letter, different
    /// Aurora version folder name, etc). Reuses the same MiniFtpClient the Sync tab's
    /// actual transfer path already uses, so "what this dialog sees" and "what Sync
    /// will actually connect to" are guaranteed to be the same thing.
    /// </summary>
    public partial class FtpBrowserWindow : Window
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private string _currentPath;

        public string? SelectedPath { get; private set; }

        public FtpBrowserWindow(string ip, int port, string username, string password, string startPath)
        {
            InitializeComponent();
            _ip = ip;
            _port = port;
            _username = username;
            _password = password;
            _currentPath = string.IsNullOrWhiteSpace(startPath) ? "/" : startPath;

            Loaded += async (_, __) => await NavigateAsync(_currentPath);
        }

        private async Task NavigateAsync(string path)
        {
            StatusText.Text = "Connecting...";
            FolderListView.ItemsSource = null;

            try
            {
                var folders = await Task.Run(() =>
                {
                    using var ftp = new MiniFtpClient();
                    ftp.Connect(_ip, _port, _username, _password);
                    var entries = ftp.ListDirectory(path);
                    ftp.Quit();

                    return entries
                        .Where(e => e.IsDirectory && e.Name != "." && e.Name != "..")
                        .Select(e => e.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                });

                _currentPath = path;
                PathText.Text = _currentPath;
                FolderListView.ItemsSource = folders;
                StatusText.Text = $"{folders.Count} folder(s)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to list '{path}': {ex.Message}";
            }
        }

        private async void FolderListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FolderListView.SelectedItem is string name)
            {
                await NavigateAsync(CombinePath(_currentPath, name));
            }
        }

        private async void UpButton_Click(object sender, RoutedEventArgs e)
        {
            await NavigateAsync(GetParentPath(_currentPath));
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedPath = _currentPath;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string CombinePath(string basePath, string child)
        {
            var b = basePath.TrimEnd('/');
            return string.IsNullOrEmpty(b) ? $"/{child}" : $"{b}/{child}";
        }

        private static string GetParentPath(string path)
        {
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx <= 0 ? "/" : trimmed.Substring(0, idx);
        }
    }
}
