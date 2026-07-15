using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace AuroraSuite
{
    public partial class MainWindow : Window
    {
        private Settings _settings = Settings.Load();
        private CancellationTokenSource? _syncCts;
        private CancellationTokenSource? _convertCts;
        private readonly ObservableCollection<LibraryRow> _libraryRows = new();
        private readonly ObservableCollection<ImageAssetRow> _imageAssetRows = new();

        public MainWindow()
        {
            InitializeComponent();
            LibraryGrid.ItemsSource = _libraryRows;
            ImageAssetGrid.ItemsSource = _imageAssetRows;
            LoadSettingsIntoUi();
        }

        // ===================== Settings <-> UI =====================

        private void LoadSettingsIntoUi()
        {
            IpTextBox.Text = _settings.Ip;
            PortTextBox.Text = _settings.Port.ToString();
            UsernameTextBox.Text = _settings.Username;
            PasswordBox.Password = _settings.Password;
            AuroraPathTextBox.Text = _settings.AuroraGameDataPath;
            LibraryPathTextBox.Text = _settings.LibraryPath;
            XbdmTextBox.Text = string.IsNullOrWhiteSpace(_settings.XbdmTarget) ? _settings.Ip : _settings.XbdmTarget;
            XbdmPortTextBox.Text = _settings.XbdmPort.ToString();
            TransportComboBox.SelectedIndex = _settings.Transport == TransportKind.Xbdm ? 1 : 0;
            UpdateTransportUi();
            OnlyOverwriteCheckBox.IsChecked = _settings.OnlyOverwriteExisting;
            IconCheckBox.IsChecked = _settings.AssetPrefixes.Contains("GL");
            BoxartCheckBox.IsChecked = _settings.AssetPrefixes.Contains("GC");
            BackgroundCheckBox.IsChecked = _settings.AssetPrefixes.Contains("BK");
            ScreenshotCheckBox.IsChecked = _settings.AssetPrefixes.Contains("SS");

            ImgSourceTextBox.Text = string.IsNullOrWhiteSpace(_settings.ImageAssetsSourcePath)
                ? Settings.DefaultImageAssetsSourcePath : _settings.ImageAssetsSourcePath;
            ImgOutputTextBox.Text = string.IsNullOrWhiteSpace(_settings.ImageAssetsOutputPath)
                ? Settings.DefaultImageAssetsOutputPath : _settings.ImageAssetsOutputPath;
            ConvertIconCheckBox.IsChecked = _settings.ConvertIcon;
            ConvertBoxartCheckBox.IsChecked = _settings.ConvertBoxart;
            ConvertBackgroundCheckBox.IsChecked = _settings.ConvertBackground;
            ConvertScreenshotsCheckBox.IsChecked = _settings.ConvertScreenshots;
        }

        private void SaveUiIntoSettings()
        {
            _settings.Ip = IpTextBox.Text.Trim();
            _settings.Port = int.TryParse(PortTextBox.Text.Trim(), out var p) ? p : 21;
            _settings.Username = UsernameTextBox.Text;
            _settings.Password = PasswordBox.Password;
            _settings.AuroraGameDataPath = AuroraPathTextBox.Text.Trim();
            _settings.LibraryPath = LibraryPathTextBox.Text.Trim();
            _settings.XbdmTarget = XbdmTextBox.Text.Trim();
            _settings.XbdmPort = int.TryParse(XbdmPortTextBox.Text.Trim(), out var xp) && xp > 0 && xp <= 65535
                ? xp
                : XbdmClient.DefaultPort;
            _settings.Transport = TransportComboBox.SelectedIndex == 1 ? TransportKind.Xbdm : TransportKind.Ftp;
            _settings.OnlyOverwriteExisting = OnlyOverwriteCheckBox.IsChecked == true;

            var prefixes = new System.Collections.Generic.List<string>();
            if (IconCheckBox.IsChecked == true) prefixes.Add("GL");
            if (BoxartCheckBox.IsChecked == true) prefixes.Add("GC");
            if (BackgroundCheckBox.IsChecked == true) prefixes.Add("BK");
            if (ScreenshotCheckBox.IsChecked == true) prefixes.Add("SS");
            _settings.AssetPrefixes = prefixes;

            _settings.ImageAssetsSourcePath = ImgSourceTextBox.Text.Trim();
            _settings.ImageAssetsOutputPath = ImgOutputTextBox.Text.Trim();
            _settings.ConvertIcon = ConvertIconCheckBox.IsChecked == true;
            _settings.ConvertBoxart = ConvertBoxartCheckBox.IsChecked == true;
            _settings.ConvertBackground = ConvertBackgroundCheckBox.IsChecked == true;
            _settings.ConvertScreenshots = ConvertScreenshotsCheckBox.IsChecked == true;
        }

        // ===================== Colored logging =====================
        //
        // Both log panels are RichTextBox rather than plain TextBox so problem lines
        // can stand out - this mirrors the old Python tool's orange terminal summary
        // of folders that need fixing, just done as colored WPF text instead of ANSI
        // codes. Classification is based on the fixed message prefixes/substrings this
        // class and SyncEngine/ImageAssetConverter already use consistently, so no
        // signature changes were needed anywhere else.
        //
        // RichTextBox/FlowDocument has no built-in virtualization in WPF - every
        // appended Paragraph permanently adds to the live visual tree. A run across a
        // multi-thousand-title library can produce thousands of log lines, which is
        // exactly what was making the whole window sluggish to even drag around after
        // a big scan/sync finished. The full, untrimmed text is kept in a separate
        // list (for Save Log to .txt); only the last MaxVisibleLogLines are ever kept
        // as actual Paragraphs in the RichTextBox itself.
        private const int MaxVisibleLogLines = 1500;
        private readonly System.Collections.Generic.List<string> _syncLogLines = new();
        private readonly System.Collections.Generic.List<string> _imgLogLines = new();

        private Brush ClassifyLineColor(string message)
        {
            if (message.StartsWith("  ! ") || message.Contains("FAILED") || message.Contains("BAD IMAGE") ||
                message.Contains("BAD SCREENSHOT") || message.StartsWith("  BAD IMAGE"))
                return (Brush)TryFindResource("Brush.Danger") ?? Brushes.OrangeRed;

            if (message.StartsWith("  - ") || message.StartsWith("  SKIPPED") || message.Contains(", skipping") ||
                message.Contains("skipping folder") || message.Contains("nothing converted") ||
                message.Contains("no recognizable images found") || message.Contains("no usable screenshots") ||
                message.Contains("note:") || message.Contains("DIAGNOSTIC"))
                return (Brush)TryFindResource("Brush.LogOrange") ?? Brushes.Orange;

            if (message.StartsWith("  + ") || message.Contains("bytes)") || message.Contains("encoded"))
                return (Brush)TryFindResource("Brush.Success") ?? Brushes.LightGreen;

            return (Brush)TryFindResource("Brush.Text") ?? Brushes.WhiteSmoke;
        }

        private void AppendLine(System.Windows.Controls.RichTextBox rtb, System.Collections.Generic.List<string> fullLog, string message)
        {
            fullLog.Add(message);

            var para = new Paragraph(new Run(message))
            {
                Margin = new Thickness(0),
                Foreground = ClassifyLineColor(message),
            };
            rtb.Document.Blocks.Add(para);

            while (rtb.Document.Blocks.Count > MaxVisibleLogLines)
                rtb.Document.Blocks.Remove(rtb.Document.Blocks.FirstBlock);

            rtb.ScrollToEnd();
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() => AppendLine(LogTextBox, _syncLogLines, message));
        }

        private void ImgLog(string message)
        {
            Dispatcher.Invoke(() => AppendLine(ImgLogTextBox, _imgLogLines, message));
        }

        // ===================== Custom title bar =====================

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ===================== Sync tab =====================

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            _settings.Save();
            Log("Settings saved.");
        }

        private void BrowseLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog();
            dialog.Description = "Select the Output folder (contains one subfolder per Title ID)";
            if (!string.IsNullOrWhiteSpace(LibraryPathTextBox.Text))
                dialog.SelectedPath = LibraryPathTextBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                LibraryPathTextBox.Text = dialog.SelectedPath;
        }

        /// <summary>
        /// Browses the console's own folder tree over FTP (using whatever IP/port/
        /// credentials are currently in the boxes above) so people whose Aurora
        /// install isn't at the predefined default path can navigate to the right
        /// one instead of typing it by hand.
        /// </summary>
        private void BrowseAuroraPathButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();

            var startPath = string.IsNullOrWhiteSpace(AuroraPathTextBox.Text) ? "/" : AuroraPathTextBox.Text.Trim();
            var dialog = new FtpBrowserWindow(_settings.Ip, _settings.Port, _settings.Username, _settings.Password, startPath)
            {
                Owner = this,
            };

            if (dialog.ShowDialog() == true && dialog.SelectedPath != null)
            {
                AuroraPathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void TransportComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Fires once during InitializeComponent, before the other controls exist.
            if (!IsInitialized) return;
            UpdateTransportUi();
        }

        /// <summary>
        /// Greys out whatever the chosen transport doesn't use. XBDM has no login step at
        /// all, so under XBDM the username/password simply don't apply - which is the whole
        /// reason it's here.
        /// </summary>
        private void UpdateTransportUi()
        {
            bool xbdm = TransportComboBox.SelectedIndex == 1;

            UsernameLabel.IsEnabled = !xbdm;
            UsernameTextBox.IsEnabled = !xbdm;
            PasswordLabel.IsEnabled = !xbdm;
            PasswordBox.IsEnabled = !xbdm;
            FtpPortLabel.IsEnabled = !xbdm;
            PortTextBox.IsEnabled = !xbdm;

            TransportHintText.Text = xbdm
                ? "Uploads go over XBDM on port 730. No username or password - XBDM has no login. Needs xbdm enabled in DashLaunch."
                : "Uploads go over FTP. Username/password are only sent if your FTP server asks for them.";

            CredentialsHintText.Text = xbdm
                ? "Not used by XBDM."
                : "Optional - leave blank for anonymous.";
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            ConnectButton.IsEnabled = false;
            StatusText.Text = "Connecting...";
            StatusText.Foreground = Brushes.Gray;

            try
            {
                await Task.Run(() =>
                {
                    using var ftp = new MiniFtpClient();
                    ftp.Connect(_settings.Ip, _settings.Port, _settings.Username, _settings.Password);
                    ftp.ChangeDirectory(_settings.AuroraGameDataPath);
                    ftp.Quit();
                });

                StatusText.Text = "Connected OK";
                StatusText.Foreground = Brushes.LightGreen;
                Log($"Connected to {_settings.Ip}:{_settings.Port} and verified path {_settings.AuroraGameDataPath}");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Connection failed";
                StatusText.Foreground = Brushes.OrangeRed;
                Log($"Connection failed: {ex.Message}");
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async void XbdmConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            var target = string.IsNullOrWhiteSpace(_settings.XbdmTarget) ? _settings.Ip : _settings.XbdmTarget;
            if (string.IsNullOrWhiteSpace(target))
            {
                XbdmStatusText.Text = "Enter a console name/IP first";
                XbdmStatusText.Foreground = Brushes.OrangeRed;
                return;
            }

            XbdmConnectButton.IsEnabled = false;
            XbdmStatusText.Text = "Connecting...";
            XbdmStatusText.Foreground = Brushes.Gray;

            try
            {
                // Same round trip the sync does: connect (no login), read the drive list,
                // translate the configured path and list it. If this passes, syncing over
                // XBDM will work.
                var summary = await Task.Run(() =>
                {
                    using var xbdm = new XbdmClient();
                    xbdm.Connect(target, _settings.XbdmPort);

                    var drives = xbdm.DriveList();
                    var path = XbdmPath.FromConfigured(_settings.AuroraGameDataPath, drives);
                    var entries = xbdm.DirList(path, out var found);
                    if (!found)
                        throw new System.IO.IOException($"Connected, but '{path}' does not exist on the console.");

                    var folders = entries.Count(x => x.IsDirectory);
                    xbdm.Bye();
                    return (Drives: drives, Path: path, Folders: folders);
                });

                XbdmStatusText.Text = "Connected OK";
                XbdmStatusText.Foreground = Brushes.LightGreen;
                Log($"XBDM connected to {target}:{_settings.XbdmPort} - no login needed.");
                Log($"  Drives: {string.Join(", ", summary.Drives)}");
                Log($"  {summary.Path} -> {summary.Folders} title folder(s).");
            }
            catch (Exception ex)
            {
                XbdmStatusText.Text = "XBDM connection failed";
                XbdmStatusText.Foreground = Brushes.OrangeRed;
                Log($"XBDM connection failed: {ex.Message}");
                Log("  Check that xbdm is enabled in DashLaunch on the console (it listens on port 730).");
            }
            finally
            {
                XbdmConnectButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Scans the local library folder and populates the grid - purely local,
        /// doesn't touch the console. Also called automatically by Sync Selected if
        /// the grid is still empty, so people who forget this step aren't stuck. Runs
        /// the actual directory scan on a background thread - with a library that can
        /// run into the thousands of folders, doing this on the UI thread is what was
        /// freezing the window until it finished.
        /// </summary>
        private async void LoadLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            await LoadLibraryAsync();
        }

        private async Task LoadLibraryAsync()
        {
            var path = LibraryPathTextBox.Text.Trim();

            LoadLibraryButton.IsEnabled = false;
            SyncButton.IsEnabled = false;
            try
            {
                var rows = await Task.Run(() => SyncEngine.LoadLibraryRows(path));

                _libraryRows.Clear();
                foreach (var row in rows)
                    _libraryRows.Add(row);

                LibraryEmptyText.Visibility = _libraryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Log($"Loaded {rows.Count} title folder(s) from '{path}'.");
            }
            finally
            {
                LoadLibraryButton.IsEnabled = true;
                SyncButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Writes the Include checkbox's state directly to its row rather than relying
        /// on the CheckBox's own TwoWay binding to commit it - same fix XenonArchivist's
        /// grid uses for the same underlying WPF DataGrid checkbox-commit quirk.
        /// </summary>
        private void IncludeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is LibraryRow row)
                row.Include = cb.IsChecked == true;
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _libraryRows)
                row.Include = true;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _libraryRows)
                row.Include = false;
        }

        /// <summary>
        /// "Apply All" - ticks every asset type, (re)loads the library, selects every
        /// row, and immediately runs the sync - a one-click "sync everything" shortcut.
        /// </summary>
        private async void ApplyAllButton_Click(object sender, RoutedEventArgs e)
        {
            IconCheckBox.IsChecked = true;
            BoxartCheckBox.IsChecked = true;
            BackgroundCheckBox.IsChecked = true;
            ScreenshotCheckBox.IsChecked = true;

            SaveUiIntoSettings();
            await LoadLibraryAsync();
            foreach (var row in _libraryRows)
                row.Include = true;

            await SyncSelectedAsync();
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncSelectedAsync();

        private async Task SyncSelectedAsync()
        {
            SaveUiIntoSettings();
            _settings.Save();

            if (_libraryRows.Count == 0)
                await LoadLibraryAsync();

            if (_libraryRows.Count == 0)
            {
                SummaryText.Text = "Nothing loaded - check the Local library folder path and click Load Library.";
                SummaryText.Foreground = (Brush)TryFindResource("Brush.LogOrange");
                return;
            }

            SyncButton.IsEnabled = false;
            ApplyAllButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            SummaryText.Text = "";
            SummaryText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            LogTextBox.Document.Blocks.Clear();
            _syncLogLines.Clear();
            _syncCts = new CancellationTokenSource();

            try
            {
                var engine = new SyncEngine(_settings, Log);
                var result = await Task.Run(() => engine.RunSelected(_libraryRows, _syncCts.Token));

                SummaryText.Text = $"Scanned {result.TitlesScannedOnConsole} console title(s), " +
                                    $"matched {result.TitlesMatchedInLibrary} selected title(s), " +
                                    $"uploaded {result.FilesUploaded} file(s), " +
                                    $"skipped {result.FilesSkippedNoRemoteMatch}, " +
                                    $"errors {result.Errors}.";

                SummaryText.Foreground = result.Errors > 0
                    ? (Brush)TryFindResource("Brush.Danger")
                    : result.FilesSkippedNoRemoteMatch > 0
                        ? (Brush)TryFindResource("Brush.LogOrange")
                        : (Brush)TryFindResource("Brush.Success");
            }
            catch (Exception ex)
            {
                Log($"FAILED: {ex.Message}");
                SummaryText.Text = "Sync failed - see log above.";
                SummaryText.Foreground = (Brush)TryFindResource("Brush.Danger");
            }
            finally
            {
                SyncButton.IsEnabled = true;
                ApplyAllButton.IsEnabled = true;
                ConnectButton.IsEnabled = true;
            }
        }

        // ===================== Image Assets tab =====================

        private void BrowseImgSourceButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog();
            dialog.Description = "Select the source folder (one title folder, or a parent of many title folders)";
            if (!string.IsNullOrWhiteSpace(ImgSourceTextBox.Text))
                dialog.SelectedPath = ImgSourceTextBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ImgSourceTextBox.Text = dialog.SelectedPath;
        }

        private void BrowseImgOutputButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog();
            dialog.Description = "Select the output folder for converted .asset files";
            if (!string.IsNullOrWhiteSpace(ImgOutputTextBox.Text))
                dialog.SelectedPath = ImgOutputTextBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ImgOutputTextBox.Text = dialog.SelectedPath;
        }

        /// <summary>
        /// Detection only - populates the grid with what was found, converts nothing.
        /// Also called automatically by Convert Selected if the grid is still empty.
        /// Runs the scan on a background thread - with a source folder that can run
        /// into the thousands of title folders, doing this on the UI thread is what
        /// was freezing the window until it finished.
        /// </summary>
        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            _settings.Save();
            await ScanSourceAsync();
        }

        private async Task ScanSourceAsync()
        {
            var source = ImgSourceTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(source) || !System.IO.Directory.Exists(source))
            {
                ImgSummaryText.Text = "Source folder does not exist.";
                ImgSummaryText.Foreground = (Brush)TryFindResource("Brush.Danger");
                return;
            }

            ScanButton.IsEnabled = false;
            ConvertButton.IsEnabled = false;
            ImgLogTextBox.Document.Blocks.Clear();
            _imgLogLines.Clear();
            var cts = new CancellationTokenSource();

            try
            {
                var rows = await Task.Run(() => ImageAssetConverter.Scan(source, ImgLog, cts.Token));

                _imageAssetRows.Clear();
                foreach (var row in rows)
                    _imageAssetRows.Add(row);

                ImageAssetEmptyText.Visibility = _imageAssetRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ImgLog($"Scan found {rows.Count} title(s) under '{source}'.");
            }
            catch (Exception ex)
            {
                ImgLog($"FAILED to scan: {ex.Message}");
            }
            finally
            {
                ScanButton.IsEnabled = true;
                ConvertButton.IsEnabled = true;
            }
        }

        private void ImgIncludeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is ImageAssetRow row)
                row.Include = cb.IsChecked == true;
        }

        private void ImgSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _imageAssetRows)
                row.Include = true;
        }

        private void ImgDeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _imageAssetRows)
                row.Include = false;
        }

        private async void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUiIntoSettings();
            _settings.Save();

            var output = ImgOutputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                ImgSummaryText.Text = "Set an output folder first.";
                ImgSummaryText.Foreground = (Brush)TryFindResource("Brush.Danger");
                return;
            }

            if (_imageAssetRows.Count == 0)
                await ScanSourceAsync();

            if (_imageAssetRows.Count == 0)
            {
                ImgSummaryText.Text = "Nothing scanned - check the Source folder path and click Scan Source.";
                ImgSummaryText.Foreground = (Brush)TryFindResource("Brush.LogOrange");
                return;
            }

            var options = new ImageAssetOptions
            {
                ConvertIcon = ConvertIconCheckBox.IsChecked == true,
                ConvertBoxart = ConvertBoxartCheckBox.IsChecked == true,
                ConvertBackground = ConvertBackgroundCheckBox.IsChecked == true,
                ConvertScreenshots = ConvertScreenshotsCheckBox.IsChecked == true,
            };

            ConvertButton.IsEnabled = false;
            ScanButton.IsEnabled = false;
            ImgSummaryText.Text = "";
            ImgSummaryText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            ImgLogTextBox.Document.Blocks.Clear();
            _imgLogLines.Clear();
            _convertCts = new CancellationTokenSource();

            try
            {
                var summary = await Task.Run(() =>
                    ImageAssetConverter.ConvertSelected(_imageAssetRows, output, options, ImgLog, _convertCts.Token));

                int failed = _imageAssetRows.Count(r => r.Status == ConvertStatus.Failed);
                int withSkips = _imageAssetRows.Count(r => r.Status == ConvertStatus.ConvertedWithSkips);

                ImgSummaryText.Text = $"Titles converted: {summary.TitlesProcessed}, " +
                                       $"assets written: {summary.AssetsWritten}, " +
                                       $"with skips: {withSkips}, " +
                                       $"failed: {failed}.";

                ImgSummaryText.Foreground = failed > 0
                    ? (Brush)TryFindResource("Brush.Danger")
                    : withSkips > 0
                        ? (Brush)TryFindResource("Brush.LogOrange")
                        : (Brush)TryFindResource("Brush.Success");
            }
            catch (Exception ex)
            {
                ImgLog($"FAILED: {ex.Message}");
                ImgSummaryText.Text = "Conversion failed - see log above.";
                ImgSummaryText.Foreground = (Brush)TryFindResource("Brush.Danger");
            }
            finally
            {
                ConvertButton.IsEnabled = true;
                ScanButton.IsEnabled = true;
            }
        }

        // ===================== Save log to .txt =====================

        private void SaveLogToFile(System.Collections.Generic.List<string> fullLog, string filePrefix, Action<string> log)
        {
            try
            {
                var fileName = $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
                System.IO.File.WriteAllLines(path, fullLog);
                log($"Log saved to '{path}'.");
            }
            catch (Exception ex)
            {
                log($"Failed to save log: {ex.Message}");
            }
        }

        private void SaveSyncLogButton_Click(object sender, RoutedEventArgs e) =>
            SaveLogToFile(_syncLogLines, "AuroraSuite_SyncLog", Log);

        private void SaveImgLogButton_Click(object sender, RoutedEventArgs e) =>
            SaveLogToFile(_imgLogLines, "AuroraSuite_ImageAssetsLog", ImgLog);
    }
}
