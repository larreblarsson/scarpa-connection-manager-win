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

public class FileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string Icon => IsDirectory ? "\uE8D5" : "\uE7C3";
    public Brush IconColor => IsDirectory
        ? new SolidColorBrush(Microsoft.UI.Colors.Gold)
        : new SolidColorBrush(Microsoft.UI.Colors.LightGray);
}

public sealed partial class SftpWindow : Window
{
    private readonly SftpService _sftpService = new();
    private ObservableCollection<FileItem> _localFiles = new();
    private ObservableCollection<FileItem> _remoteFiles = new();

    private string _currentLocalPath = "";
    private string _currentRemotePath = "";

    private bool _isLocalFocused = true;
    private List<FileItem> _clipboardFiles = new();
    private string _clipboardSource = ""; // Will be "Local" or "Remote"
    private bool _isCut = false;

    public SftpWindow(ServerConfig cfg)
    {
        this.InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets\\scarpa_icon.ico");
        appWindow.SetIcon(iconPath);

        this.Title = $"{cfg.Name} - SFTP File Manager";

        LocalFileList.ItemsSource = _localFiles;
        RemoteFileList.ItemsSource = _remoteFiles;

        this.Closed += SftpWindow_Closed;

        LoadLocalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _ = ConnectAndListFilesAsync(cfg);

        SetActivePane(true);
    }

    // --- Directory Loading ---

    private void LoadLocalDirectory(string path)
    {
        try
        {
            _localFiles.Clear();
            _currentLocalPath = path;
            LocalPathText.Text = path;

            var dir = new DirectoryInfo(path);

            if (dir.Parent != null)
            {
                _localFiles.Add(new FileItem { Name = "..", FullPath = dir.Parent.FullName, IsDirectory = true });
            }

            foreach (var d in dir.GetDirectories().OrderBy(x => x.Name))
            {
                if (!d.Attributes.HasFlag(FileAttributes.Hidden))
                    _localFiles.Add(new FileItem { Name = d.Name, FullPath = d.FullName, IsDirectory = true });
            }

            foreach (var f in dir.GetFiles().OrderBy(x => x.Name))
            {
                if (!f.Attributes.HasFlag(FileAttributes.Hidden))
                    _localFiles.Add(new FileItem { Name = f.Name, FullPath = f.FullName, IsDirectory = false });
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    private void LocalFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LocalFileList.SelectedItem is FileItem selected && selected.IsDirectory)
            LoadLocalDirectory(selected.FullPath);
    }

    private async Task ConnectAndListFilesAsync(ServerConfig cfg)
    {
        var dispatcher = this.DispatcherQueue;
        var xamlRoot = this.Content.XamlRoot;

        await Task.Run(() =>
        {
            try
            {
                _sftpService.Connect(cfg, cfg.Password, dispatcher, xamlRoot);
                LoadRemoteDirectory(_sftpService.WorkingDirectory);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RemotePathText.Text = $"Connection Failed: {ex.Message}";
                    RemotePathText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                });
            }
        });
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
                RemotePathText.Text = path;

                if (path != "/")
                {
                    string parentPath = path.Substring(0, path.LastIndexOf('/'));
                    if (string.IsNullOrEmpty(parentPath)) parentPath = "/";
                    _remoteFiles.Add(new FileItem { Name = "..", FullPath = parentPath, IsDirectory = true });
                }

                foreach (var f in files.Where(x => x.Name != "." && x.Name != "..").OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
                {
                    _remoteFiles.Add(new FileItem { Name = f.Name, FullPath = f.FullName, IsDirectory = f.IsDirectory });
                }
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => RemotePathText.Text = $"Error: {ex.Message}");
        }
    }

    private void RemoteFileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RemoteFileList.SelectedItem is FileItem selected && selected.IsDirectory)
            Task.Run(() => LoadRemoteDirectory(selected.FullPath));
    }

    // --- Toolbar Actions ---

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var selected = LocalFileList.SelectedItems.Cast<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0 || !_sftpService.IsConnected) return;

        string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
        await PerformTransferAsync($"Uploading {selected.Count} item(s)...", () =>
        {
            foreach (var item in selected)
                UploadRecursive(item.FullPath, $"{targetDir}/{item.Name}");
        });

        LoadRemoteDirectory(_currentRemotePath);
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

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var selected = RemoteFileList.SelectedItems.Cast<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0 || !_sftpService.IsConnected) return;

        string targetDir = _currentLocalPath;
        await PerformTransferAsync($"Downloading {selected.Count} item(s)...", () =>
        {
            foreach (var item in selected)
                DownloadRecursive(item.FullPath, Path.Combine(targetDir, item.Name), item.IsDirectory);
        });

        LoadLocalDirectory(_currentLocalPath);
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
        var inputBox = new TextBox { PlaceholderText = "Folder Name", Width = 300 };
        var locationCombo = new ComboBox { Width = 300, Margin = new Thickness(0, 10, 0, 0) };
        locationCombo.Items.Add("Local: " + _currentLocalPath);
        locationCombo.Items.Add("Remote: " + _currentRemotePath);
        locationCombo.SelectedIndex = 1; // Default to creating it on the Remote side

        var dialog = new ContentDialog
        {
            Title = "Create New Folder",
            Content = new StackPanel { Children = { inputBox, locationCombo } },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string newName = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            try
            {
                if (locationCombo.SelectedIndex == 0) // Local
                {
                    Directory.CreateDirectory(Path.Combine(_currentLocalPath, newName));
                    LoadLocalDirectory(_currentLocalPath);
                }
                else // Remote
                {
                    string target = _currentRemotePath == "/" ? $"/{newName}" : $"{_currentRemotePath}/{newName}";
                    _sftpService.CreateDirectory(target);
                    LoadRemoteDirectory(_currentRemotePath);
                }
            }
            catch (Exception ex)
            {
                var errDlg = new ContentDialog { Title = "Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await errDlg.ShowAsync();
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        var selected = activeList.SelectedItems.Cast<FileItem>().Where(x => x.Name != "..").ToList();

        if (selected.Count == 0) return;

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

        var activeBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
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
        // Passed 'this' instead of 'null' to fix CS8625 warning
        Delete_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // --- Clipboard Operations ---

    private void CopySelected(ListView? listView, bool isLocal, bool isCut)
    {
        if (listView == null) return;
        var selected = listView.SelectedItems.Cast<FileItem>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0) return;

        _clipboardFiles = selected;
        _clipboardSource = isLocal ? "Local" : "Remote";
        _isCut = isCut;
    }

    private async void PasteClipboard(bool pasteToLocal)
    {
        if (_clipboardFiles.Count == 0) return;

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
            await PerformTransferAsync(_isCut ? $"Moving {_clipboardFiles.Count} item(s)..." : $"Copying {_clipboardFiles.Count} item(s)...", () =>
            {
                foreach (var item in _clipboardFiles)
                {
                    string target = Path.Combine(_currentLocalPath, item.Name);
                    if (item.FullPath == target) continue;

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
            await PerformTransferAsync(_isCut ? $"Moving {_clipboardFiles.Count} item(s)..." : $"Copying {_clipboardFiles.Count} item(s)...", () =>
            {
                foreach (var item in _clipboardFiles)
                {
                    string target = $"{targetDir}/{item.Name}";
                    if (item.FullPath == target) continue;

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

    private async Task PerformTransferAsync(string message, Action work)
    {
        ProgressText.Text = message;
        ProgressOverlay.Visibility = Visibility.Visible;

        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
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
        finally
        {
            ProgressOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SftpWindow_Closed(object sender, WindowEventArgs args)
    {
        _sftpService.Dispose();
    }
    // --- Context Menu Handlers ---

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var listView = sender as ListView;
        if (listView == null) return;

        bool isLocal = listView == LocalFileList;
        SetActivePane(isLocal); // Instantly move the blue border

        // Find the specific file that was right-clicked
        if (e.OriginalSource is FrameworkElement element && element.DataContext is FileItem item)
        {
            // If the item isn't already part of a multi-selection, select it exclusively
            if (!listView.SelectedItems.Contains(item))
            {
                listView.SelectedItem = item;
            }

            // Show the modern horizontal flyout at the mouse pointer
            FileContextMenu.ShowAt(listView, e.GetPosition(listView));
        }
    }

    private void ContextCut_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: true);
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        CopySelected(activeList, _isLocalFocused, isCut: false);
    }

    private void ContextPaste_Click(object sender, RoutedEventArgs e)
    {
        PasteClipboard(_isLocalFocused);
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        Delete_Click(this, new RoutedEventArgs());
    }

    private async void ContextRename_Click(object sender, RoutedEventArgs e)
    {
        var activeList = _isLocalFocused ? LocalFileList : RemoteFileList;
        var selected = activeList.SelectedItems.Cast<FileItem>().Where(x => x.Name != "..").FirstOrDefault();

        if (selected == null) return;

        var inputBox = new TextBox { Text = selected.Name, Width = 300 };

        // Auto-select text so the user can just start typing immediately
        inputBox.Loaded += (s, ev) =>
        {
            inputBox.Focus(FocusState.Programmatic);
            inputBox.SelectAll();
        };

        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = inputBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string newName = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == selected.Name) return;

            try
            {
                if (_isLocalFocused)
                {
                    string newPath = Path.Combine(_currentLocalPath, newName);
                    if (selected.IsDirectory) Directory.Move(selected.FullPath, newPath);
                    else File.Move(selected.FullPath, newPath);
                    LoadLocalDirectory(_currentLocalPath);
                }
                else
                {
                    string targetDir = _currentRemotePath == "/" ? "" : _currentRemotePath;
                    string newPath = $"{targetDir}/{newName}";

                    await PerformTransferAsync($"Renaming to {newName}...", () =>
                    {
                        _sftpService.Rename(selected.FullPath, newPath);
                    });

                    LoadRemoteDirectory(_currentRemotePath);
                }
            }
            catch (Exception ex)
            {
                var errDlg = new ContentDialog { Title = "Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
                await errDlg.ShowAsync();
            }
        }
    }
}