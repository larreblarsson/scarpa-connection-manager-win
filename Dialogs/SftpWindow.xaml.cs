using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;

namespace scarpa_connection_manager_win.Dialogs;

public sealed class SplitterContainer : Grid
{
    public SplitterContainer()
    {
        // Grid is not sealed, so we can access the protected cursor!
        this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    }
}

public class FileItem : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly SolidColorBrush FolderIconBrush = new(Microsoft.UI.Colors.Gold);
    private static readonly SolidColorBrush FileIconBrush = new(Microsoft.UI.Colors.LightGray);
    private static readonly SolidColorBrush HighlightBrush = new(Microsoft.UI.ColorHelper.FromArgb(120, 50, 150, 255));
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    private Microsoft.UI.Xaml.Media.ImageSource? _imageIcon;
    public Microsoft.UI.Xaml.Media.ImageSource? ImageIcon
    {
        get => _imageIcon;
        set
        {
            _imageIcon = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageIcon)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FontIconVisibility)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageIconVisibility)));
        }
    }

    // Show the FontIcon if it's a folder OR if the Image hasn't loaded yet
    public Visibility FontIconVisibility => IsDirectory || _imageIcon == null ? Visibility.Visible : Visibility.Collapsed;

    // Show the real Image icon only for files once it is fully loaded
    public Visibility ImageIconVisibility => !IsDirectory && _imageIcon != null ? Visibility.Visible : Visibility.Collapsed;

    private bool _isDropHighlight;
    private bool _isEditing;

    public bool IsNewFolder { get; set; } = false;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string Icon => IsDirectory ? "\uE8D5" : "\uE7C3";

    public Brush IconColor => IsDirectory ? FolderIconBrush : FileIconBrush;

    public bool IsDropHighlight
    {
        get => _isDropHighlight;
        set
        {
            _isDropHighlight = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDropHighlight)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DropBackgroundBrush)));
        }
    }

    public Brush DropBackgroundBrush => _isDropHighlight ? HighlightBrush : TransparentBrush;

    public DateTime? LastModified { get; set; }
    private long _size;
    public long Size
    {
        get => _size;
        set
        {
            _size = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Size)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedSize)));
        }
    }

    public string FormattedDate => LastModified?.ToString("dd/MM/yyyy HH:mm") ?? "";
    public string FormattedSize
    {
        get
        {
            if (Name == "..") return "";
            if (Size == -1) return "Calculating...";
            if (IsDirectory && Size == 0) return "";
            if (Size == 0) return "0 KB";
            long kb = (long)Math.Ceiling(Size / 1024.0);
            return $"{kb:N0} KB";
        }
    }

    public string Type
    {
        get
        {
            if (Name == "..") return "";
            if (IsDirectory) return "File folder";
            string ext = Path.GetExtension(Name).TrimStart('.');
            return string.IsNullOrEmpty(ext) ? "File" : $"{ext.ToUpper()} File";
        }
    }

    private bool _showDate = true;
    public bool ShowDate { get => _showDate; set { _showDate = value; PropertyChanged?.Invoke(this, new(nameof(DateVisibility))); } }
    public Visibility DateVisibility => _showDate ? Visibility.Visible : Visibility.Collapsed;

    private bool _showType = true;
    public bool ShowType { get => _showType; set { _showType = value; PropertyChanged?.Invoke(this, new(nameof(TypeVisibility))); } }
    public Visibility TypeVisibility => _showType ? Visibility.Visible : Visibility.Collapsed;

    private bool _showSize = true;
    public bool ShowSize { get => _showSize; set { _showSize = value; PropertyChanged?.Invoke(this, new(nameof(SizeVisibility))); } }
    public Visibility SizeVisibility => _showSize ? Visibility.Visible : Visibility.Collapsed;


    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEditing)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TextVisibility)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EditVisibility)));
        }
    }

    // --- Added the missing visibility properties back! ---
    public Visibility TextVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class SftpWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private readonly SftpService _sftpService = new();
    private readonly List<ServerConfig> _allServers = new();
    private IDisposable? _activeJumpTunnel;
    private ObservableCollection<FileItem> _localFiles = new();
    private ObservableCollection<FileItem> _remoteFiles = new();

    private string _currentLocalPath = "";
    private string _currentRemotePath = "";

    private ObservableCollection<FileItem> _searchResults = new();
    private bool _isSearchLocal = true;

    private bool _isLocalFocused = true;
    private List<FileItem> _clipboardFiles = new();
    private string _clipboardSource = "";
    private bool _isCut = false;
    private List<FileItem> _draggedFiles = new();
    private string _dragSource = "";
    private FileItem? _currentHighlightedFolder = null;
    private DateTime _lastSelectionTime = DateTime.MinValue;
    private bool _awaitingDoubleTap = false;
    private FileItem? _potentialRenameItem = null;
    private bool _showDateCol = true;
    private bool _showTypeCol = true;
    private bool _showSizeCol = true;

    // --- Window PropertyChanged Event ---
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    // --- Local Column Resizing Properties ---
    private double _localNameColWidth = 250.0;
    public double LocalNameColWidth { get => _localNameColWidth; set { _localNameColWidth = value; OnPropertyChanged(nameof(LocalNameColWidth)); OnPropertyChanged(nameof(LocalNameColGridLength)); } }
    public GridLength LocalNameColGridLength => new GridLength(_localNameColWidth);

    private double _localDateColWidth = 150.0;
    public double LocalDateColWidth { get => _localDateColWidth; set { _localDateColWidth = value; OnPropertyChanged(nameof(LocalDateColWidth)); OnPropertyChanged(nameof(LocalDateColGridLength)); } }
    public GridLength LocalDateColGridLength => new GridLength(_localDateColWidth);

    private double _localTypeColWidth = 120.0;
    public double LocalTypeColWidth { get => _localTypeColWidth; set { _localTypeColWidth = value; OnPropertyChanged(nameof(LocalTypeColWidth)); OnPropertyChanged(nameof(LocalTypeColGridLength)); } }
    public GridLength LocalTypeColGridLength => new GridLength(_localTypeColWidth);

    private double _localSizeColWidth = 100.0;
    public double LocalSizeColWidth { get => _localSizeColWidth; set { _localSizeColWidth = value; OnPropertyChanged(nameof(LocalSizeColWidth)); OnPropertyChanged(nameof(LocalSizeColGridLength)); } }
    public GridLength LocalSizeColGridLength => new GridLength(_localSizeColWidth);

    // --- Remote Column Resizing Properties ---
    private double _remoteNameColWidth = 250.0;
    public double RemoteNameColWidth { get => _remoteNameColWidth; set { _remoteNameColWidth = value; OnPropertyChanged(nameof(RemoteNameColWidth)); OnPropertyChanged(nameof(RemoteNameColGridLength)); } }
    public GridLength RemoteNameColGridLength => new GridLength(_remoteNameColWidth);

    private double _remoteDateColWidth = 150.0;
    public double RemoteDateColWidth { get => _remoteDateColWidth; set { _remoteDateColWidth = value; OnPropertyChanged(nameof(RemoteDateColWidth)); OnPropertyChanged(nameof(RemoteDateColGridLength)); } }
    public GridLength RemoteDateColGridLength => new GridLength(_remoteDateColWidth);

    private double _remoteTypeColWidth = 120.0;
    public double RemoteTypeColWidth { get => _remoteTypeColWidth; set { _remoteTypeColWidth = value; OnPropertyChanged(nameof(RemoteTypeColWidth)); OnPropertyChanged(nameof(RemoteTypeColGridLength)); } }
    public GridLength RemoteTypeColGridLength => new GridLength(_remoteTypeColWidth);

    private double _remoteSizeColWidth = 100.0;
    public double RemoteSizeColWidth { get => _remoteSizeColWidth; set { _remoteSizeColWidth = value; OnPropertyChanged(nameof(RemoteSizeColWidth)); OnPropertyChanged(nameof(RemoteSizeColGridLength)); } }
    public GridLength RemoteSizeColGridLength => new GridLength(_remoteSizeColWidth);

    public SftpWindow(ServerConfig cfg, List<ServerConfig>? allServers = null)
    {
        this.InitializeComponent();

        if (allServers != null) _allServers = allServers;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets\\scarpa_icon.ico");
        appWindow.SetIcon(iconPath);

        this.Title = $"{cfg.Name} - SFTP File Manager";

        LocalFileList.ItemsSource = _localFiles;
        RemoteFileList.ItemsSource = _remoteFiles;
        SearchResultsList.ItemsSource = _searchResults;

        this.Closed += SftpWindow_Closed;

        LoadLocalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        SetActivePane(true);

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += (s, e) =>
            {
                _ = ConnectAndListFilesAsync(cfg);
            };
        }
    }

    // --- Column Splitter Methods ---
    private void ColumnSplitter_DragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement thumb && thumb.Tag is string tag)
        {
            if (tag == "LocalName") LocalNameColWidth = Math.Max(50, LocalNameColWidth + e.HorizontalChange);
            else if (tag == "LocalDate") LocalDateColWidth = Math.Max(50, LocalDateColWidth + e.HorizontalChange);
            else if (tag == "LocalType") LocalTypeColWidth = Math.Max(50, LocalTypeColWidth + e.HorizontalChange);
            else if (tag == "LocalSize") LocalSizeColWidth = Math.Max(50, LocalSizeColWidth + e.HorizontalChange);
            else if (tag == "RemoteName") RemoteNameColWidth = Math.Max(50, RemoteNameColWidth + e.HorizontalChange);
            else if (tag == "RemoteDate") RemoteDateColWidth = Math.Max(50, RemoteDateColWidth + e.HorizontalChange);
            else if (tag == "RemoteType") RemoteTypeColWidth = Math.Max(50, RemoteTypeColWidth + e.HorizontalChange);
            else if (tag == "RemoteSize") RemoteSizeColWidth = Math.Max(50, RemoteSizeColWidth + e.HorizontalChange);
        }
    }

    private void ColumnSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement thumb && thumb.Tag is string tag)
        {
            if (tag == "LocalName")
            {
                double maxLen = _localFiles.Any() ? _localFiles.Max(x => x.Name.Length) : 0;
                LocalNameColWidth = Math.Max(120, (maxLen * 8.5) + 60);
            }
            else if (tag == "RemoteName")
            {
                double maxLen = _remoteFiles.Any() ? _remoteFiles.Max(x => x.Name.Length) : 0;
                RemoteNameColWidth = Math.Max(120, (maxLen * 8.5) + 60);
            }
            else if (tag == "LocalDate") LocalDateColWidth = 150;
            else if (tag == "LocalType") LocalTypeColWidth = 120;
            else if (tag == "LocalSize") LocalSizeColWidth = 100;
            else if (tag == "RemoteDate") RemoteDateColWidth = 150;
            else if (tag == "RemoteType") RemoteTypeColWidth = 120;
            else if (tag == "RemoteSize") RemoteSizeColWidth = 100;
        }
    }

    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _iconCache = new();

    private async Task LoadFileIconAsync(FileItem item)
    {
        if (item.IsDirectory) return;

        string ext = Path.GetExtension(item.Name).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".blank";

        // 1. Check if we already loaded this extension in memory
        if (_iconCache.TryGetValue(ext, out var cachedIcon))
        {
            DispatcherQueue.TryEnqueue(() => item.ImageIcon = cachedIcon);
            return;
        }

        try
        {
            // 2. Create a dummy zero-byte file in Temp to trick Windows into handing over the exact shell icon
            string tempFile = Path.Combine(Path.GetTempPath(), $"scarpa_icon_dummy{ext}");
            if (!File.Exists(tempFile)) File.WriteAllBytes(tempFile, Array.Empty<byte>());

            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(tempFile);
            var thumbnail = await storageFile.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.ListView,
                24,
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

            if (thumbnail != null)
            {
                // 3. Send the thumbnail back to the UI Thread to update the image
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!_iconCache.ContainsKey(ext))
                    {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        _iconCache[ext] = bitmap;
                    }
                    item.ImageIcon = _iconCache[ext];
                });
            }
        }
        catch { /* Fails silently and leaves the default File FontIcon visible */ }
    }

    // --- Directory Loading ---
    private void LoadLocalDirectory(string path)
    {
        try
        {
            _localFiles.Clear();
            _currentLocalPath = path;
            UpdateBreadcrumbs(LocalPathContainer, path, true);

            if (string.IsNullOrEmpty(path))
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    _localFiles.Add(new FileItem
                    {
                        Name = drive.Name.TrimEnd('\\'),
                        FullPath = drive.Name,
                        IsDirectory = true,
                        ShowDate = _showDateCol,
                        ShowType = _showTypeCol,
                        ShowSize = _showSizeCol
                    });
                }
                return;
            }

            var dir = new DirectoryInfo(path);

            if (dir.Parent != null)
            {
                _localFiles.Add(new FileItem
                {
                    Name = "..",
                    FullPath = dir.Parent.FullName,
                    IsDirectory = true,
                    ShowDate = _showDateCol,
                    ShowType = _showTypeCol,
                    ShowSize = _showSizeCol
                });
            }
            else
            {
                _localFiles.Add(new FileItem
                {
                    Name = "..",
                    FullPath = "",
                    IsDirectory = true,
                    ShowDate = _showDateCol,
                    ShowType = _showTypeCol,
                    ShowSize = _showSizeCol
                });
            }

            foreach (var d in dir.GetDirectories().OrderBy(x => x.Name))
            {
                _localFiles.Add(new FileItem
                {
                    Name = d.Name,
                    FullPath = d.FullName,
                    IsDirectory = true,
                    LastModified = d.LastWriteTime,
                    Size = 0,
                    ShowDate = _showDateCol,
                    ShowType = _showTypeCol,
                    ShowSize = _showSizeCol
                });
            }

            foreach (var f in dir.GetFiles().OrderBy(x => x.Name))
            {
                // Extracted into a variable so we can pass it to the icon loader
                var newLocalItem = new FileItem
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsDirectory = false,
                    LastModified = f.LastWriteTime,
                    Size = f.Length,
                    ShowDate = _showDateCol,
                    ShowType = _showTypeCol,
                    ShowSize = _showSizeCol
                };

                _localFiles.Add(newLocalItem);

                // Trigger the native icon extraction in the background
                _ = LoadFileIconAsync(newLocalItem);
            }
        }
        catch (Exception) { /* Silently ignore */ }
    }

    private void LoadRemoteDirectory(string path)
    {
        if (!_sftpService.IsConnected) return;

        try
        {
            var files = _sftpService.List(path);

            DispatcherQueue.TryEnqueue(() =>
            {
                _remoteFiles.Clear();
                _currentRemotePath = path;
                UpdateBreadcrumbs(RemotePathContainer, path, false);

                if (path != "/")
                {
                    string parentPath = path.Substring(0, path.LastIndexOf('/'));
                    if (string.IsNullOrEmpty(parentPath)) parentPath = "/";

                    _remoteFiles.Add(new FileItem
                    {
                        Name = "..",
                        FullPath = parentPath,
                        IsDirectory = true,
                        ShowDate = _showDateCol,
                        ShowType = _showTypeCol,
                        ShowSize = _showSizeCol
                    });
                }

                foreach (var f in files.Where(x => x.Name != "." && x.Name != "..").OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
                {
                    // Extracted into a variable so we can pass it to the icon loader
                    var newRemoteItem = new FileItem
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        IsDirectory = f.IsDirectory,
                        LastModified = f.LastWriteTime,
                        Size = f.Length,
                        ShowDate = _showDateCol,
                        ShowType = _showTypeCol,
                        ShowSize = _showSizeCol
                    };

                    _remoteFiles.Add(newRemoteItem);

                    // Trigger the native icon extraction if it's a file, not a directory
                    if (!newRemoteItem.IsDirectory)
                    {
                        _ = LoadFileIconAsync(newRemoteItem);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RemotePathContainer.Children.Clear();
                RemotePathContainer.Children.Add(new TextBlock { Text = $"Error: {ex.Message}", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red) });
            });
        }
    }

    private async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView listView) return;
        var selected = listView.SelectedItem as FileItem;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (selected != null && !selected.IsEditing)
            {
                e.Handled = true;
                if (selected.IsDirectory)
                {
                    if (listView == LocalFileList) LoadLocalDirectory(selected.FullPath);
                    else Task.Run(() => LoadRemoteDirectory(selected.FullPath));
                }
                else
                {
                    // Open the file if it's not a directory
                    await OpenFileAsync(selected, isLocal: listView == LocalFileList);
                }
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Back)
        {
            if (e.OriginalSource is TextBox) return;

            e.Handled = true;
            var activeFiles = listView == LocalFileList ? _localFiles : _remoteFiles;
            var parentFolder = activeFiles.FirstOrDefault(x => x.Name == "..");

            if (parentFolder != null)
            {
                if (listView == LocalFileList) LoadLocalDirectory(parentFolder.FullPath);
                else Task.Run(() => LoadRemoteDirectory(parentFolder.FullPath));
            }
        }
    }

    private async Task ConnectAndListFilesAsync(ServerConfig cfg)
    {
        var dispatcher = this.DispatcherQueue;
        var xamlRoot = this.Content?.XamlRoot;

        if (xamlRoot == null) return;

        await Task.Run(async () =>
        {
            try
            {
                // 1. Keep your UI update so the user sees what is happening
                if (!string.IsNullOrEmpty(cfg.JumpServerId))
                {
                    var jumpServer = JumpServerService.ResolveJumpServer(cfg, _allServers);
                    if (jumpServer != null)
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            RemotePathContainer.Children.Clear();
                            RemotePathContainer.Children.Add(new TextBlock
                            {
                                Text = $"Connecting via Jump Host ({jumpServer.Name})...",
                                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange)
                            });
                        });
                    }
                }

                // 2. Use the new centralized method for the actual heavy lifting
                var (targetConfig, tunnel) = await JumpServerService.PrepareConnectionAsync(cfg, _allServers);

                // 3. Store the tunnel so SftpWindow_Closed can clean it up later
                _activeJumpTunnel = tunnel;

                // 4. Connect to the final target (which is now properly routed)
                _sftpService.Connect(targetConfig, targetConfig.Password, dispatcher, xamlRoot);
                LoadRemoteDirectory(_sftpService.WorkingDirectory);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RemotePathContainer.Children.Clear();
                    RemotePathContainer.Children.Add(new TextBlock
                    {
                        Text = $"Connection Failed: {ex.Message}",
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red)
                    });
                });
            }
        });
    }

    // --- Toolbar Actions ---
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var selected = LocalFileList.SelectedItems.OfType<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0 || !_sftpService.IsConnected) return;

        string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
        await PerformTransferAsync($"Uploading {selected.Count} item(s)...", async () =>
        {
            string listPath = string.IsNullOrEmpty(targetDir) ? "/" : targetDir;
            var existingRemote = _sftpService.List(listPath).ToList();

            foreach (var item in selected)
            {
                var conflict = existingRemote.FirstOrDefault(x => x.Name == item.Name);
                if (conflict != null)
                {
                    if (!await ConfirmOverwriteAsync(item.Name)) continue;
                    _sftpService.Delete($"{targetDir}/{item.Name}", conflict.IsDirectory);
                }
                UploadRecursive(item.FullPath, $"{targetDir}/{item.Name}");
            }
        });

        LoadRemoteDirectory(_currentRemotePath);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var selected = RemoteFileList.SelectedItems.OfType<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0 || !_sftpService.IsConnected) return;

        string targetDir = _currentLocalPath;
        await PerformTransferAsync($"Downloading {selected.Count} item(s)...", async () =>
        {
            foreach (var item in selected)
            {
                string target = Path.Combine(targetDir, item.Name);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    if (!await ConfirmOverwriteAsync(item.Name)) continue;
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    else File.Delete(target);
                }
                DownloadRecursive(item.FullPath, target, item.IsDirectory);
            }
        });

        LoadLocalDirectory(_currentLocalPath);
    }

    private void UploadRecursive(string localPath, string remotePath)
    {
        if (Directory.Exists(localPath))
        {
            try { _sftpService.CreateDirectory(remotePath); } catch { /* Ignore if exists */ }
            foreach (var file in Directory.GetFiles(localPath))
                _sftpService.Upload(file, $"{remotePath}/{Path.GetFileName(file)}");
            foreach (var dir in Directory.GetDirectories(localPath))
                UploadRecursive(dir, $"{remotePath}/{Path.GetFileName(dir)}");
        }
        else
        {
            _sftpService.Upload(localPath, remotePath);
        }
    }

    private void DownloadRecursive(string remotePath, string localPath, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.CreateDirectory(localPath);
            var items = _sftpService.List(remotePath);
            foreach (var item in items)
            {
                if (item.Name == "." || item.Name == "..") continue;
                DownloadRecursive(item.FullName, Path.Combine(localPath, item.Name), item.IsDirectory);
            }
        }
        else
        {
            _sftpService.Download(remotePath, localPath);
        }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var targetCollection = _isLocalFocused ? _localFiles : _remoteFiles;
        var targetListView = _isLocalFocused ? LocalFileList : RemoteFileList;

        var newFolder = new FileItem
        {
            Name = "New Folder",
            IsDirectory = true,
            ShowDate = _showDateCol,
            ShowType = _showTypeCol,
            ShowSize = _showSizeCol,
            IsNewFolder = true
        };

        // Insert just below ".." if it exists
        int insertIndex = (targetCollection.Count > 0 && targetCollection[0].Name == "..") ? 1 : 0;
        targetCollection.Insert(insertIndex, newFolder);

        targetListView.SelectedItem = newFolder;
        targetListView.ScrollIntoView(newFolder);

        // Wait a tiny bit for the UI container to generate, then trigger your existing rename mode
        await Task.Delay(50);
        TriggerRename(targetListView, newFolder);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        var selected = activeList.SelectedItems.OfType<FileItem>().Where(x => x.Name != "..").ToList();

        if (selected.Count == 0 || this.Content?.XamlRoot == null) return;

        var confirmDialog = new ContentDialog
        {
            Title = "Confirm Delete",
            Content = $"Are you sure you want to permanently delete {selected.Count} item(s)?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (_isLocalFocused)
        {
            await PerformTransferAsync("Deleting local item(s)...", () =>
            {
                foreach (var item in selected)
                {
                    if (item.IsDirectory) Directory.Delete(item.FullPath, true);
                    else File.Delete(item.FullPath);
                }
            });
            LoadLocalDirectory(_currentLocalPath);
        }
        else if (_sftpService.IsConnected)
        {
            await PerformTransferAsync("Deleting remote item(s)...", () =>
            {
                foreach (var item in selected)
                    _sftpService.Delete(item.FullPath, item.IsDirectory);
            });
            LoadRemoteDirectory(_currentRemotePath);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLocalDirectory(_currentLocalPath);
        if (_sftpService.IsConnected) Task.Run(() => LoadRemoteDirectory(_currentRemotePath));
    }

    // --- Focus & UI State ---

    private void SetActivePane(bool isLocal)
    {
        _isLocalFocused = isLocal;

        var activeBrush = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        var inactiveBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var topOnlyThickness = new Thickness(0, 2, 0, 0);

        LocalFileList.BorderBrush = isLocal ? activeBrush : inactiveBrush;
        LocalFileList.BorderThickness = topOnlyThickness;

        RemoteFileList.BorderBrush = !isLocal ? activeBrush : inactiveBrush;
        RemoteFileList.BorderThickness = topOnlyThickness;
    }

    private void LocalFileList_GotFocus(object sender, RoutedEventArgs e) => SetActivePane(true);
    private void LocalFileList_PointerPressed(object sender, PointerRoutedEventArgs e) => SetActivePane(true);

    private void RemoteFileList_GotFocus(object sender, RoutedEventArgs e) => SetActivePane(false);
    private void RemoteFileList_PointerPressed(object sender, PointerRoutedEventArgs e) => SetActivePane(false);

    // --- Keyboard Accelerators ---

    private void CtrlC_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: false);
        args.Handled = true;
    }

    private void CtrlX_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: true);
        args.Handled = true;
    }

    private void CtrlV_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        PasteClipboard(_isLocalFocused);
        args.Handled = true;
    }

    private void DeleteKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Delete_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // --- Clipboard Operations ---

    private void CopySelected(ListView? listView, bool isLocal, bool isCut)
    {
        if (listView == null) return;
        var selected = listView.SelectedItems.OfType<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0) return;

        _clipboardFiles = selected;
        _clipboardSource = isLocal ? "Local" : "Remote";
        _isCut = isCut;
    }

    private async void PasteClipboard(bool pasteToLocal)
    {
        if (_clipboardFiles.Count == 0 || this.Content?.XamlRoot == null) return;

        if ((_clipboardSource == "Local" && !pasteToLocal) || (_clipboardSource == "Remote" && pasteToLocal))
        {
            var dialog = new ContentDialog
            {
                Title = "Cross-Pane Paste Disabled",
                Content = "Please use the Upload and Download buttons in the toolbar to transfer files between your computer and the server.",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        if (pasteToLocal)
        {
            await PerformTransferAsync(_isCut ? $"Moving {_clipboardFiles.Count} item(s)..." : $"Copying {_clipboardFiles.Count} item(s)...", async () =>
            {
                foreach (var item in _clipboardFiles)
                {
                    string target = Path.Combine(_currentLocalPath, item.Name);
                    if (item.FullPath == target) continue;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        if (!await ConfirmOverwriteAsync(item.Name)) continue;
                        if (Directory.Exists(target)) Directory.Delete(target, true);
                        else File.Delete(target);
                    }

                    if (_isCut)
                    {
                        if (item.IsDirectory) Directory.Move(item.FullPath, target);
                        else File.Move(item.FullPath, target);
                    }
                    else
                    {
                        if (item.IsDirectory) CopyDirectoryLocal(item.FullPath, target);
                        else File.Copy(item.FullPath, target, true);
                    }
                }
            });
            LoadLocalDirectory(_currentLocalPath);
        }
        else
        {
            string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
            await PerformTransferAsync(_isCut ? $"Moving {_clipboardFiles.Count} item(s)..." : $"Copying {_clipboardFiles.Count} item(s)...", async () =>
            {
                string listPath = string.IsNullOrEmpty(targetDir) ? "/" : targetDir;
                var existingRemote = _sftpService.List(listPath).ToList();

                foreach (var item in _clipboardFiles)
                {
                    string target = $"{targetDir}/{item.Name}";
                    if (item.FullPath == target) continue;

                    var conflict = existingRemote.FirstOrDefault(x => x.Name == item.Name);
                    if (conflict != null)
                    {
                        if (!await ConfirmOverwriteAsync(item.Name)) continue;
                        _sftpService.Delete(target, conflict.IsDirectory);
                    }

                    if (_isCut)
                    {
                        _sftpService.Rename(item.FullPath, target);
                    }
                    else
                    {
                        if (item.IsDirectory) CopyDirectoryRemote(item.FullPath, target);
                        else CopyFileRemote(item.FullPath, target);
                    }
                }
            });
            LoadRemoteDirectory(_currentRemotePath);
        }

        if (_isCut)
        {
            _clipboardFiles.Clear();
            _isCut = false;
        }
    }

    private void CopyDirectoryLocal(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryLocal(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private void CopyFileRemote(string sourceRemote, string destRemote)
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            _sftpService.Download(sourceRemote, tempFile);
            _sftpService.Upload(tempFile, destRemote);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private void CopyDirectoryRemote(string sourceRemote, string destRemote)
    {
        try { _sftpService.CreateDirectory(destRemote); } catch { /* Ignore if exists */ }

        foreach (var item in _sftpService.List(sourceRemote))
        {
            if (item.Name == "." || item.Name == "..") continue;

            string newSource = $"{sourceRemote}/{item.Name}";
            string newDest = $"{destRemote}/{item.Name}";

            if (item.IsDirectory) CopyDirectoryRemote(newSource, newDest);
            else CopyFileRemote(newSource, newDest);
        }
    }

    // --- Core Utilities ---
    private async Task<bool> ConfirmOverwriteAsync(string itemName)
    {
        var tcs = new TaskCompletionSource<bool>();

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (this.Content?.XamlRoot == null)
            {
                tcs.SetResult(false);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Confirm Replace",
                Content = $"An item named '{itemName}' already exists at the destination.\n\nDo you want to replace it? This will permanently delete the existing item.",
                PrimaryButtonText = "Replace",
                CloseButtonText = "Skip",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            try
            {
                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary);
            }
            catch
            {
                tcs.SetResult(false);
            }
        });

        return await tcs.Task;
    }

    // --- Cancellation & Progress Handlers ---

    private System.Threading.CancellationTokenSource? _cancellationTokenSource;

    private void CancelProgress_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        CancelProgressButton.IsEnabled = false;
        CancelProgressButton.Content = "Cancelling...";
    }

    private async Task PerformCancellableTaskAsync(string message, Func<System.Threading.CancellationToken, Task> work)
    {
        ProgressText.Text = message;
        CancelProgressButton.Visibility = Visibility.Visible;
        CancelProgressButton.IsEnabled = true;
        CancelProgressButton.Content = "Cancel";

        using (_cancellationTokenSource = new System.Threading.CancellationTokenSource())
        {
            var token = _cancellationTokenSource.Token;
            var workTask = Task.Run(() => work(token), token);
            var delayTask = Task.Delay(500, token);

            if (await Task.WhenAny(workTask, delayTask) == delayTask)
            {
                ProgressOverlay.Visibility = Visibility.Visible;
            }

            try
            {
                await workTask;
            }
            catch (OperationCanceledException)
            {
                // Silently swallow the cancel exception so the UI just closes the popup
            }
            catch (Exception ex)
            {
                if (this.Content?.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Error",
                        Content = ex.Message,
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
            finally
            {
                ProgressOverlay.Visibility = Visibility.Collapsed;
                CancelProgressButton.Visibility = Visibility.Collapsed; // Hide it again for normal transfers
                _cancellationTokenSource = null;
            }
        }
    }

    private async Task PerformTransferAsync(string message, Func<Task> work)
    {
        ProgressText.Text = message;
        var workTask = Task.Run(work);
        var delayTask = Task.Delay(500);

        if (await Task.WhenAny(workTask, delayTask) == delayTask)
        {
            ProgressOverlay.Visibility = Visibility.Visible;
        }

        try
        {
            await workTask;
        }
        catch (Exception ex)
        {
            if (this.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Transfer Error",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PerformTransferAsync(string message, Action work)
    {
        ProgressText.Text = message;
        var workTask = Task.Run(work);
        var delayTask = Task.Delay(500);

        if (await Task.WhenAny(workTask, delayTask) == delayTask)
        {
            ProgressOverlay.Visibility = Visibility.Visible;
        }

        try
        {
            await workTask;
        }
        catch (Exception ex)
        {
            if (this.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Transfer Error",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SftpWindow_Closed(object sender, WindowEventArgs args)
    {
        _sftpService.Dispose();
        _activeJumpTunnel?.Dispose();
    }

    // --- Context Menu Handlers ---

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListView listView) return;

        bool isLocal = listView == LocalFileList;
        SetActivePane(isLocal);

        bool shouldShowMenu = false;

        if (e.OriginalSource is FrameworkElement element && element.DataContext is FileItem item)
        {
            if (!listView.SelectedItems.Contains(item))
            {
                listView.SelectedItem = item;
            }
            shouldShowMenu = true;
        }
        else
        {
            if (listView.SelectedItems.Count > 0)
            {
                shouldShowMenu = true;
            }
        }

        if (shouldShowMenu)
        {
            var showOptions = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(listView)
            };

            FileContextMenu.ShowAt(listView, showOptions);
        }
    }

    private void ContextCut_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: true);
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: false);
    }

    private void ContextPaste_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        PasteClipboard(_isLocalFocused);
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        Delete_Click(this, new RoutedEventArgs());
    }

    // --- Inline Renaming Logic ---

    private void F2_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ContextRename_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private async void TriggerRename(ListView activeList, FileItem selected)
    {
        selected.IsEditing = true;
        await Task.Delay(50);

        if (activeList.ContainerFromItem(selected) is ListViewItem container)
        {
            var tb = FindVisualChild<TextBox>(container);
            if (tb != null)
            {
                tb.Focus(FocusState.Programmatic);
                tb.SelectAll();
            }
        }
    }

    private void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        var selected = activeList.SelectedItems.OfType<FileItem>().Where(x => x.Name != "..").FirstOrDefault();

        if (selected != null)
        {
            TriggerRename(activeList, selected);
        }
    }

    private async void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not FileItem item) return;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (item.IsNewFolder) await CommitNewFolder(tb, item);
            else await CommitRename(tb, item);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (item.IsNewFolder) CancelNewFolder(item);
            else CancelRename(tb, item);
        }
    }

    private async void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item)
        {
            if (item.IsNewFolder) await CommitNewFolder(tb, item);
            else await CommitRename(tb, item);
        }
    }

    private void CancelRename(TextBox tb, FileItem item)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        activeList.Focus(FocusState.Pointer);
        item.IsEditing = false;
        tb.Text = item.Name;
    }

    private async Task CommitRename(TextBox tb, FileItem item)
    {
        if (!item.IsEditing) return;

        string newName = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            CancelRename(tb, item);
            return;
        }

        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        activeList.Focus(FocusState.Pointer);
        item.IsEditing = false;

        try
        {
            bool isLocalItem = _localFiles.Contains(item);

            if (isLocalItem)
            {
                string baseDir = Path.GetDirectoryName(item.FullPath) ?? _currentLocalPath;
                string newPath = Path.Combine(baseDir, newName);

                if (File.Exists(newPath) || Directory.Exists(newPath))
                {
                    if (!await ConfirmOverwriteAsync(newName))
                    {
                        CancelRename(tb, item);
                        return;
                    }
                    if (Directory.Exists(newPath)) Directory.Delete(newPath, true);
                    else File.Delete(newPath);
                }

                if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
                else File.Move(item.FullPath, newPath);

                LoadLocalDirectory(_currentLocalPath);
            }
            else
            {
                string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
                string newPath = $"{targetDir}/{newName}";

                await PerformTransferAsync($"Renaming to {newName}...", async () =>
                {
                    string listPath = string.IsNullOrEmpty(targetDir) ? "/" : targetDir;
                    var existingRemote = _sftpService.List(listPath).ToList();
                    var conflict = existingRemote.FirstOrDefault(x => x.Name == newName);

                    if (conflict != null)
                    {
                        if (!await ConfirmOverwriteAsync(newName)) return;
                        _sftpService.Delete(newPath, conflict.IsDirectory);
                    }

                    _sftpService.Rename(item.FullPath, newPath);
                });

                LoadRemoteDirectory(_currentRemotePath);
            }
        }
        catch (Exception ex)
        {
            CancelRename(tb, item);
            if (this.Content?.XamlRoot != null)
            {
                var errDlg = new ContentDialog { Title = "Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await errDlg.ShowAsync();
            }
        }
    }

    private void CancelNewFolder(FileItem item)
    {
        item.IsEditing = false;
        if (_localFiles.Contains(item)) _localFiles.Remove(item);
        if (_remoteFiles.Contains(item)) _remoteFiles.Remove(item);
    }

    private async Task CommitNewFolder(TextBox tb, FileItem item)
    {
        if (!item.IsEditing) return;

        string newName = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelNewFolder(item);
            return;
        }

        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        activeList.Focus(FocusState.Pointer);
        item.IsEditing = false;
        item.IsNewFolder = false;

        try
        {
            if (_isLocalFocused)
            {
                string targetDir = Path.Combine(_currentLocalPath, newName);
                if (Directory.Exists(targetDir) || File.Exists(targetDir))
                {
                    CancelNewFolder(item);
                    var errDlg = new ContentDialog { Title = "Error", Content = $"An item named '{newName}' already exists.", CloseButtonText = "OK", XamlRoot = this.Content?.XamlRoot };
                    await errDlg.ShowAsync();
                    return;
                }

                Directory.CreateDirectory(targetDir);
                LoadLocalDirectory(_currentLocalPath);
            }
            else
            {
                string targetDir = _currentRemotePath == "/" ? $"/{newName}" : $"{_currentRemotePath}/{newName}";

                await PerformTransferAsync($"Creating folder {newName}...", () =>
                {
                    _sftpService.CreateDirectory(targetDir);
                });
                LoadRemoteDirectory(_currentRemotePath);
            }
        }
        catch (Exception ex)
        {
            CancelNewFolder(item);
            if (this.Content?.XamlRoot != null)
            {
                var errDlg = new ContentDialog { Title = "Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await errDlg.ShowAsync();
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // --- Click Tracking Logic ---

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lastSelectionTime = DateTime.Now;
    }

    private void FileList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListView listView) return;

        if (e.OriginalSource is FrameworkElement element && element.DataContext is FileItem item)
        {
            if (listView.SelectedItems.Count == 1 && listView.SelectedItem == item && item.Name != "..")
            {
                var timeSinceSelection = (DateTime.Now - _lastSelectionTime).TotalMilliseconds;

                if (timeSinceSelection > 500)
                {
                    _potentialRenameItem = item;
                    _awaitingDoubleTap = true;

                    var dispatcher = this.DispatcherQueue;
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        dispatcher.TryEnqueue(() =>
                        {
                            if (_awaitingDoubleTap && _potentialRenameItem == item && !item.IsEditing)
                            {
                                TriggerRename(listView, item);
                            }
                            _awaitingDoubleTap = false;
                            _potentialRenameItem = null;
                        });
                    });
                }
            }
            return;
        }

        listView.SelectedItems.Clear();
    }

    private async Task OpenFileAsync(FileItem file, bool isLocal)
    {
        try
        {
            string pathToOpen = file.FullPath;

            // If it's a remote file, we must download it to the Windows Temp folder first
            if (!isLocal)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "ScarpaSFTP");
                Directory.CreateDirectory(tempDir);
                pathToOpen = Path.Combine(tempDir, file.Name);

                bool success = true;
                await PerformTransferAsync($"Downloading {file.Name} to open...", () =>
                {
                    try { _sftpService.Download(file.FullPath, pathToOpen); }
                    catch { success = false; throw; } // PerformTransferAsync will handle the UI error
                });

                if (!success || !File.Exists(pathToOpen)) return;
            }

            // UseShellExecute = true tells Windows to look up the default associated application
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = pathToOpen,
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fires if Windows strictly refuses to open the file due to zero associations
            if (this.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Cannot Open File",
                    Content = $"There is no default application installed to open '{Path.GetExtension(file.Name)}' files.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            if (this.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to open file: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
    }

    private async void LocalFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _awaitingDoubleTap = false;
        if (LocalFileList.SelectedItem is FileItem selected)
        {
            if (selected.IsDirectory) LoadLocalDirectory(selected.FullPath);
            else await OpenFileAsync(selected, isLocal: true);
        }
    }

    private async void RemoteFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _awaitingDoubleTap = false;
        if (RemoteFileList.SelectedItem is FileItem selected)
        {
            if (selected.IsDirectory) Task.Run(() => LoadRemoteDirectory(selected.FullPath));
            else await OpenFileAsync(selected, isLocal: false);
        }
    }

    // --- Drag and Drop Handlers ---

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView listView) return;
        _dragSource = listView == LocalFileList ? "Local" : "Remote";

        _draggedFiles = e.Items.Cast<FileItem>().Where(x => x.Name != "..").ToList();

        if (_draggedFiles.Count == 0)
        {
            e.Cancel = true;
        }
    }

    private FileItem? GetFolderUnderMouse(ListView listView, DragEventArgs e)
    {
        var pos = e.GetPosition(listView);

        foreach (var item in listView.Items)
        {
            if (item is FileItem fileItem && fileItem.IsDirectory)
            {
                if (listView.ContainerFromItem(item) is ListViewItem lvi)
                {
                    try
                    {
                        var transform = lvi.TransformToVisual(listView);
                        var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, lvi.ActualWidth, lvi.ActualHeight));

                        if (bounds.Contains(pos))
                        {
                            return fileItem;
                        }
                    }
                    catch { }
                }
            }
        }
        return null;
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView || _draggedFiles.Count == 0) return;

        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;

        FileItem? targetFolder = GetFolderUnderMouse(listView, e);

        if (_currentHighlightedFolder != targetFolder)
        {
            ClearDropHighlight();
            if (targetFolder != null)
            {
                _currentHighlightedFolder = targetFolder;
                _currentHighlightedFolder.IsDropHighlight = true;
            }
        }

        string dropTarget = listView == LocalFileList ? "Local" : "Remote";
        e.AcceptedOperation = _dragSource != dropTarget
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void ClearDropHighlight()
    {
        if (_currentHighlightedFolder != null)
        {
            _currentHighlightedFolder.IsDropHighlight = false;
            _currentHighlightedFolder = null;
        }
    }

    private void FileList_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
    }

    private async Task ExecuteDropTransferAsync(string dragSource, string dropTarget, string finalDest)
    {
        if (dragSource == "Local" && dropTarget == "Remote")
        {
            if (!_sftpService.IsConnected) return;
            await PerformTransferAsync($"Uploading {_draggedFiles.Count} item(s)...", async () =>
            {
                string listPath = string.IsNullOrEmpty(finalDest) ? "/" : finalDest;
                var existingRemote = _sftpService.List(listPath).ToList();

                foreach (var file in _draggedFiles)
                {
                    var conflict = existingRemote.FirstOrDefault(x => x.Name == file.Name);
                    if (conflict != null)
                    {
                        if (!await ConfirmOverwriteAsync(file.Name)) continue;
                        _sftpService.Delete($"{finalDest}/{file.Name}", conflict.IsDirectory);
                    }
                    UploadRecursive(file.FullPath, $"{finalDest}/{file.Name}");
                }
            });
            LoadRemoteDirectory(_currentRemotePath);
        }
        else if (dragSource == "Remote" && dropTarget == "Local")
        {
            await PerformTransferAsync($"Downloading {_draggedFiles.Count} item(s)...", async () =>
            {
                foreach (var file in _draggedFiles)
                {
                    string target = Path.Combine(finalDest, file.Name);
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        if (!await ConfirmOverwriteAsync(file.Name)) continue;
                        if (Directory.Exists(target)) Directory.Delete(target, true);
                        else File.Delete(target);
                    }
                    DownloadRecursive(file.FullPath, target, file.IsDirectory);
                }
            });
            LoadLocalDirectory(_currentLocalPath);
        }
        else if (dragSource == "Local" && dropTarget == "Local")
        {
            await PerformTransferAsync($"Moving {_draggedFiles.Count} item(s)...", async () =>
            {
                foreach (var file in _draggedFiles)
                {
                    string dest = Path.Combine(finalDest, file.Name);
                    if (file.FullPath == dest) continue;

                    if (File.Exists(dest) || Directory.Exists(dest))
                    {
                        if (!await ConfirmOverwriteAsync(file.Name)) continue;
                        if (Directory.Exists(dest)) Directory.Delete(dest, true);
                        else File.Delete(dest);
                    }

                    if (file.IsDirectory) Directory.Move(file.FullPath, dest);
                    else File.Move(file.FullPath, dest);
                }
            });
            LoadLocalDirectory(_currentLocalPath);
        }
        else if (dragSource == "Remote" && dropTarget == "Remote")
        {
            await PerformTransferAsync($"Moving {_draggedFiles.Count} item(s)...", async () =>
            {
                var existingRemote = _sftpService.List(finalDest).ToList();
                foreach (var file in _draggedFiles)
                {
                    string dest = $"{finalDest}/{file.Name}";
                    if (file.FullPath == dest) continue;

                    var conflict = existingRemote.FirstOrDefault(x => x.Name == file.Name);
                    if (conflict != null)
                    {
                        if (!await ConfirmOverwriteAsync(file.Name)) continue;
                        _sftpService.Delete(dest, conflict.IsDirectory);
                    }
                    _sftpService.Rename(file.FullPath, dest);
                }
            });
            LoadRemoteDirectory(_currentRemotePath);
        }

        _draggedFiles.Clear();
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView) return;
        string dropTarget = listView == LocalFileList ? "Local" : "Remote";

        FileItem? targetFolder = GetFolderUnderMouse(listView, e);
        ClearDropHighlight();
        if (_draggedFiles.Count == 0) return;

        string finalDest;
        if (dropTarget == "Local")
        {
            finalDest = targetFolder != null ? targetFolder.FullPath : _currentLocalPath;
        }
        else
        {
            string baseTarget = _currentRemotePath == "/" ? "" : _currentRemotePath;
            finalDest = targetFolder != null ? targetFolder.FullPath : baseTarget;
        }

        await ExecuteDropTransferAsync(_dragSource, dropTarget, finalDest);
    }

    private void UpdateBreadcrumbs(StackPanel container, string path, bool isLocal)
    {
        container.Children.Clear();
        double breadcrumbFontSize = 16.0;

        if (isLocal && string.IsNullOrEmpty(path))
        {
            var pcBtn = new HyperlinkButton
            {
                Content = "This PC",
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                FontSize = breadcrumbFontSize,
                Tag = ""
            };
            ConfigureBreadcrumbButton(pcBtn, "", isLocal);
            container.Children.Add(pcBtn);
            return;
        }

        if (string.IsNullOrEmpty(path)) return;

        char separator = isLocal ? '\\' : '/';
        string[] parts = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        string currentPath = "";
        if (isLocal && path.StartsWith("\\\\")) currentPath = "\\\\";
        if (!isLocal && path.StartsWith("/")) currentPath = "/";

        if (parts.Length == 0)
        {
            var rootBtn = new HyperlinkButton
            {
                Content = "\\",
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                FontSize = breadcrumbFontSize,
                Tag = isLocal ? "C:\\" : "/"
            };
            ConfigureBreadcrumbButton(rootBtn, rootBtn.Tag?.ToString() ?? "", isLocal);
            container.Children.Add(rootBtn);
            return;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            currentPath += parts[i];

            string targetPath = currentPath;
            if (isLocal && targetPath.EndsWith(":")) targetPath += "\\";

            var btn = new HyperlinkButton
            {
                Content = parts[i],
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                FontSize = breadcrumbFontSize,
                Tag = targetPath
            };

            ConfigureBreadcrumbButton(btn, targetPath, isLocal);
            container.Children.Add(btn);

            if (i < parts.Length - 1)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "\\",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0),
                    FontSize = breadcrumbFontSize
                });
                currentPath += separator.ToString();
            }
        }
    }

    private void ConfigureBreadcrumbButton(HyperlinkButton btn, string targetPath, bool isLocal)
    {
        btn.Click += (s, e) =>
        {
            if (isLocal) LoadLocalDirectory(targetPath);
            else Task.Run(() => LoadRemoteDirectory(targetPath));
        };

        // --- NEW: Prevent the tap from bubbling up to the Border (which triggers edit mode) ---
        btn.Tapped += (s, e) => e.Handled = true;

        btn.AllowDrop = true;
        string dropTarget = isLocal ? "Local" : "Remote";

        btn.DragOver += (s, e) =>
        {
            if (_draggedFiles.Count == 0) return;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;

            e.AcceptedOperation = _dragSource != dropTarget
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

            btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        };

        btn.DragLeave += (s, e) =>
        {
            btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        };

        btn.Drop += async (s, e) =>
        {
            btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            if (_draggedFiles.Count == 0) return;

            string finalDest = targetPath;
            if (!isLocal && targetPath == "/") finalDest = "";

            await ExecuteDropTransferAsync(_dragSource, dropTarget, finalDest);
        };
    }

    private void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            HeaderContextMenu.ShowAt(element, e.GetPosition(element));
        }
    }

    private void ToggleColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
        {
            if (item.Name == "MenuToggleDate") _showDateCol = item.IsChecked;
            if (item.Name == "MenuToggleType") _showTypeCol = item.IsChecked;
            if (item.Name == "MenuToggleSize") _showSizeCol = item.IsChecked;

            var dateVis = _showDateCol ? Visibility.Visible : Visibility.Collapsed;
            LocalHeaderDate.Visibility = dateVis;
            RemoteHeaderDate.Visibility = dateVis;

            var typeVis = _showTypeCol ? Visibility.Visible : Visibility.Collapsed;
            LocalHeaderType.Visibility = typeVis;
            RemoteHeaderType.Visibility = typeVis;

            var sizeVis = _showSizeCol ? Visibility.Visible : Visibility.Collapsed;
            LocalHeaderSize.Visibility = sizeVis;
            RemoteHeaderSize.Visibility = sizeVis;

            foreach (var file in _localFiles)
            {
                file.ShowDate = _showDateCol; file.ShowType = _showTypeCol; file.ShowSize = _showSizeCol;
            }
            foreach (var file in _remoteFiles)
            {
                file.ShowDate = _showDateCol; file.ShowType = _showTypeCol; file.ShowSize = _showSizeCol;
            }
        }
    }

    // --- Local Address Bar Logic ---

    private void LocalAddressBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        LocalPathContainer.Visibility = Visibility.Collapsed;
        LocalPathInput.Visibility = Visibility.Visible;
        LocalPathInput.Text = _currentLocalPath;
        LocalPathInput.Focus(FocusState.Programmatic);
        LocalPathInput.SelectAll();
    }

    private void LocalPathInput_LostFocus(object sender, RoutedEventArgs e)
    {
        LocalPathInput.Visibility = Visibility.Collapsed;
        LocalPathContainer.Visibility = Visibility.Visible;
    }

    private void LocalPathInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            string newPath = LocalPathInput.Text.Trim();

            LocalPathInput.Visibility = Visibility.Collapsed;
            LocalPathContainer.Visibility = Visibility.Visible;

            if (newPath != "")
            {
                newPath = newPath.Replace('/', '\\');
                if (newPath.StartsWith("\\") && newPath.Length >= 3 && newPath[2] == ':')
                    newPath = newPath.Substring(1);
            }

            if (newPath != _currentLocalPath) LoadLocalDirectory(newPath);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            LocalPathInput.Visibility = Visibility.Collapsed;
            LocalPathContainer.Visibility = Visibility.Visible;
        }
    }

    // --- Remote Address Bar Logic ---

    private void RemoteAddressBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        RemotePathContainer.Visibility = Visibility.Collapsed;
        RemotePathInput.Visibility = Visibility.Visible;

        string editPath = _currentRemotePath.Replace('/', '\\');
        if (editPath.StartsWith("\\") && editPath.Length >= 3 && editPath[2] == ':')
            editPath = editPath.Substring(1);

        RemotePathInput.Text = editPath;
        RemotePathInput.Focus(FocusState.Programmatic);
        RemotePathInput.SelectAll();
    }

    private void RemotePathInput_LostFocus(object sender, RoutedEventArgs e)
    {
        RemotePathInput.Visibility = Visibility.Collapsed;
        RemotePathContainer.Visibility = Visibility.Visible;
    }

    private void RemotePathInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            string newPath = RemotePathInput.Text.Trim();

            RemotePathInput.Visibility = Visibility.Collapsed;
            RemotePathContainer.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(newPath))
            {
                newPath = newPath.Replace('\\', '/');
                if (newPath.Length >= 2 && char.IsLetter(newPath[0]) && newPath[1] == ':')
                    newPath = "/" + newPath;

                if (newPath != _currentRemotePath) Task.Run(() => LoadRemoteDirectory(newPath));
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            RemotePathInput.Visibility = Visibility.Collapsed;
            RemotePathContainer.Visibility = Visibility.Visible;
        }
    }
    private async void ContextCalculateSize_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;

        var selectedFolders = activeList.SelectedItems.OfType<FileItem>()
                                      .Where(x => x.IsDirectory && x.Name != "..").ToList();

        if (selectedFolders.Count == 0) return;

        foreach (var folder in selectedFolders)
        {
            folder.Size = -1;

            if (_isLocalFocused)
            {
                await Task.Run(() =>
                {
                    long totalSize = CalculateLocalFolderSize(new DirectoryInfo(folder.FullPath));
                    DispatcherQueue.TryEnqueue(() => folder.Size = totalSize);
                });
            }
            else
            {
                await Task.Run(() =>
                {
                    long totalSize = CalculateRemoteFolderSize(folder.FullPath);
                    DispatcherQueue.TryEnqueue(() => folder.Size = totalSize);
                });
            }
        }
    }

    private long CalculateLocalFolderSize(DirectoryInfo dir)
    {
        long size = 0;
        try
        {
            foreach (var fi in dir.GetFiles()) size += fi.Length;
            foreach (var di in dir.GetDirectories()) size += CalculateLocalFolderSize(di);
        }
        catch { /* Silently ignore files/folders we don't have access to */ }
        return size;
    }

    private long CalculateRemoteFolderSize(string path)
    {
        long size = 0;
        try
        {
            var items = _sftpService.List(path);
            foreach (var item in items)
            {
                if (item.Name == "." || item.Name == "..") continue;

                if (item.IsDirectory) size += CalculateRemoteFolderSize(item.FullName);
                else size += item.Length;
            }
        }
        catch { /* Silently ignore SFTP permission errors */ }
        return size;
    }
    private void ContextResume_Click(object sender, RoutedEventArgs e)
    {
        FileContextMenu.Hide();
        Resume_Click(sender, e);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;

        var selected = activeList.SelectedItems.OfType<FileItem>()
                                 .Where(x => !x.IsDirectory && x.Name != "..").ToList();

        if (selected.Count == 0 || !_sftpService.IsConnected) return;

        if (_isLocalFocused)
        {
            string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
            await PerformTransferAsync($"Resuming upload for {selected.Count} file(s)...", () =>
            {
                string listPath = string.IsNullOrEmpty(targetDir) ? "/" : targetDir;
                var existingRemote = _sftpService.List(listPath).ToList();

                foreach (var item in selected)
                {
                    var conflict = existingRemote.FirstOrDefault(x => x.Name == item.Name);

                    if (conflict != null && conflict.Length < item.Size)
                    {
                        _sftpService.ResumeUpload(item.FullPath, $"{targetDir}/{item.Name}", conflict.Length);
                    }
                    else if (conflict == null)
                    {
                        _sftpService.Upload(item.FullPath, $"{targetDir}/{item.Name}");
                    }
                }
            });
            LoadRemoteDirectory(_currentRemotePath);
        }
        else
        {
            string targetDir = _currentLocalPath;
            await PerformTransferAsync($"Resuming download for {selected.Count} file(s)...", () =>
            {
                foreach (var item in selected)
                {
                    string target = Path.Combine(targetDir, item.Name);

                    if (File.Exists(target))
                    {
                        long localSize = new FileInfo(target).Length;
                        if (localSize < item.Size)
                        {
                            _sftpService.ResumeDownload(item.FullPath, target, localSize);
                        }
                    }
                    else
                    {
                        _sftpService.Download(item.FullPath, target);
                    }
                }
            });
            LoadLocalDirectory(_currentLocalPath);
        }
    }
    // --- Search Logic ---

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (this.Content?.XamlRoot == null) return;

        var inputBox = new TextBox { PlaceholderText = "e.g. *.txt or report_?", Width = 300 };
        var recursiveCheck = new CheckBox { Content = "Search subfolders recursively", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };

        var dialog = new ContentDialog
        {
            Title = _isLocalFocused ? "Search Local Files" : "Search Remote Files",
            Content = new StackPanel { Children = { inputBox, recursiveCheck } },
            PrimaryButtonText = "Search",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string query = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            bool recursive = recursiveCheck.IsChecked ?? false;

            // Remember where this search was executed so the Context Menu knows how to handle the files
            _isSearchLocal = _isLocalFocused;

            if (_isSearchLocal) await PerformLocalSearchAsync(query, recursive);
            else await PerformRemoteSearchAsync(query, recursive);
        }
    }

    private async Task PerformLocalSearchAsync(string query, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(_currentLocalPath)) return;
        string searchPattern = query.Contains("*") || query.Contains("?") ? query : $"*{query}*";

        await PerformCancellableTaskAsync($"Searching local files for '{searchPattern}'...", (token) =>
        {
            var results = new List<FileItem>();

            void SearchLocalDir(string path)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var dirInfo = new DirectoryInfo(path);
                    foreach (var d in dirInfo.GetDirectories(searchPattern, SearchOption.TopDirectoryOnly))
                    {
                        token.ThrowIfCancellationRequested();
                        results.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDirectory = true, LastModified = d.LastWriteTime });
                    }
                    foreach (var f in dirInfo.GetFiles(searchPattern, SearchOption.TopDirectoryOnly))
                    {
                        token.ThrowIfCancellationRequested();
                        var item = new FileItem { Name = f.Name, FullPath = f.FullName, IsDirectory = false, LastModified = f.LastWriteTime, Size = f.Length };
                        results.Add(item);
                        _ = LoadFileIconAsync(item);
                    }
                    if (recursive)
                    {
                        foreach (var d in dirInfo.GetDirectories("*", SearchOption.TopDirectoryOnly)) SearchLocalDir(d.FullName);
                    }
                }
                catch { }
            }

            SearchLocalDir(_currentLocalPath);

            DispatcherQueue.TryEnqueue(() =>
            {
                _searchResults.Clear();
                foreach (var item in results) _searchResults.Add(item);
                SearchResultsTitle.Text = $"Local Search Results for '{searchPattern}'";
                SearchResultsOverlay.Visibility = Visibility.Visible;
            });

            return Task.CompletedTask;
        });
    }

    private async Task PerformRemoteSearchAsync(string query, bool recursive)
    {
        if (!_sftpService.IsConnected || string.IsNullOrWhiteSpace(_currentRemotePath)) return;

        string searchPattern = query.Contains("*") || query.Contains("?") ? query : $"*{query}*";
        var regex = WildcardToRegex(searchPattern);

        await PerformCancellableTaskAsync($"Searching remote files for '{searchPattern}'...", (token) =>
        {
            var results = new List<FileItem>();

            void SearchRemoteDirectory(string path)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var items = _sftpService.List(path);
                    foreach (var item in items)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Name == "." || item.Name == "..") continue;

                        if (regex.IsMatch(item.Name))
                        {
                            var fileItem = new FileItem { Name = item.Name, FullPath = item.FullName, IsDirectory = item.IsDirectory, LastModified = item.LastWriteTime, Size = item.Length };
                            results.Add(fileItem);
                            if (!item.IsDirectory) _ = LoadFileIconAsync(fileItem);
                        }
                        if (recursive && item.IsDirectory) SearchRemoteDirectory(item.FullName);
                    }
                }
                catch { }
            }

            SearchRemoteDirectory(_currentRemotePath);

            DispatcherQueue.TryEnqueue(() =>
            {
                _searchResults.Clear();
                foreach (var item in results) _searchResults.Add(item);
                SearchResultsTitle.Text = $"Remote Search Results for '{searchPattern}'";
                SearchResultsOverlay.Visibility = Visibility.Visible;
            });

            return Task.CompletedTask;
        });
    }

    private static System.Text.RegularExpressions.Regex WildcardToRegex(string pattern)
    {
        // Escapes special regex characters, then converts DOS wildcards (* and ?) to regex equivalents
        string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                                  .Replace("\\*", ".*")
                                  .Replace("\\?", ".") + "$";

        return new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    // --- Search Results Overlay Handlers ---

    private void CloseSearchResults_Click(object sender, RoutedEventArgs e)
    {
        SearchResultsOverlay.Visibility = Visibility.Collapsed;
    }

    private void SearchResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is FileItem item)
        {
            SearchResultsList.SelectedItem = item;
            SearchResultsContextMenu.ShowAt(element, e.GetPosition(element));
        }
    }

    private void SearchResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Double click does the same thing as clicking "Open"
        SearchResultOpen_Click(sender, e);
    }

    private async void SearchResultOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is FileItem item)
        {
            if (item.IsDirectory)
            {
                // If they open a folder, navigate the main pane to it and close the search window
                SearchResultsOverlay.Visibility = Visibility.Collapsed;
                SetActivePane(_isSearchLocal);

                if (_isSearchLocal) LoadLocalDirectory(item.FullPath);
                else _ = Task.Run(() => LoadRemoteDirectory(item.FullPath));
            }
            else
            {
                // If they open a file, use the Windows shell execution we built earlier
                await OpenFileAsync(item, _isSearchLocal);
            }
        }
    }

    private void SearchResultGoToLocation_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is FileItem item)
        {
            SearchResultsOverlay.Visibility = Visibility.Collapsed;
            SetActivePane(_isSearchLocal);

            string parentPath;
            if (_isSearchLocal)
            {
                // Get the parent folder for Windows paths
                parentPath = Path.GetDirectoryName(item.FullPath) ?? _currentLocalPath;
                LoadLocalDirectory(parentPath);
            }
            else
            {
                // Get the parent folder for Linux/SFTP paths
                parentPath = item.FullPath.Substring(0, item.FullPath.LastIndexOf('/'));
                if (string.IsNullOrEmpty(parentPath)) parentPath = "/";

                _ = Task.Run(() => LoadRemoteDirectory(parentPath));
            }
        }
    }


}