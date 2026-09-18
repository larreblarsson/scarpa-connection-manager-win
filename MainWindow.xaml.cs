using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace scarpa_connection_manager_win;

// 1. Pure Data Model for the TreeView
public class TreeItemData : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private bool _isEditing;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string IconGlyph { get; set; } = "";
    public Brush IconColor { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(ReadVisibility));
            OnPropertyChanged(nameof(EditVisibility));
        }
    }

    public Visibility ReadVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private List<ServerConfig> _servers = new();
    private string _passphrase = "";
    private TreeViewNode? _lastClickedNode;
    private DateTime _lastClickTime = DateTime.MinValue;
    private DispatcherTimer? _renameTimer;

    private Dictionary<TreeViewNode, object> _nodeTags = new();

    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "Scarpa Connection Manager";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        var windowWidth = 800;
        var windowHeight = 700;

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var workArea = displayArea.WorkArea;
            var x = (workArea.Width - windowWidth) / 2;
            var y = (workArea.Height - windowHeight) / 2;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, windowWidth, windowHeight));
        }
        else
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(windowWidth, windowHeight));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        _settings = SettingsService.Load();

        if (string.IsNullOrEmpty(_settings.MasterHashHex))
        {
            if (!await CreateMasterPassphraseAsync()) { Close(); return; }
        }
        else if (!await UnlockVaultAsync()) { Close(); return; }

        RebuildTree();
        Log($"Loaded {_servers.Count} server(s) from {AppPaths.ServerFile}");
    }

    private void Tree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            string newFolderPath = "";
            if (args.NewParentItem is TreeViewNode parentNode && _nodeTags.TryGetValue(parentNode, out var parentTag))
            {
                if (parentTag is string path) newFolderPath = path;
            }

            bool modified = false;
            foreach (var item in args.Items)
            {
                if (item is TreeViewNode draggedNode && _nodeTags.TryGetValue(draggedNode, out var draggedObj))
                {
                    if (draggedObj is ServerConfig server)
                    {
                        server.Folder = newFolderPath;
                        modified = true;
                    }
                    else if (draggedObj is string oldFolder)
                    {
                        string folderName = oldFolder.Contains("\\") ? oldFolder.Substring(oldFolder.LastIndexOf('\\') + 1) : oldFolder;
                        string newPath = string.IsNullOrEmpty(newFolderPath) ? folderName : $"{newFolderPath}\\{folderName}";

                        foreach (var srv in _servers.Where(x => x.Folder == oldFolder || (x.Folder != null && x.Folder.StartsWith(oldFolder + "\\"))))
                        {
                            srv.Folder = newPath + srv.Folder.Substring(oldFolder.Length);
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                CryptoStore.Save(_servers, _passphrase);
                RebuildTree();
            }
        });
    }

    private async Task<bool> CreateMasterPassphraseAsync()
    {
        var dlg = new Dialogs.PassphraseDialog("Set master passphrase",
            "Your connections are encrypted with AES-256. Choose a master passphrase — it cannot be recovered if lost.",
            requireConfirm: true, showRemember: true)
        {
            XamlRoot = this.Content.XamlRoot
        };

        await dlg.ShowAsync();
        if (!dlg.IsSuccess) return false;

        var salt = CryptoStore.GenerateSalt();
        _settings.MasterSaltHex = Convert.ToHexString(salt);
        _settings.MasterHashHex = CryptoStore.HashPassphrase(dlg.Passphrase, salt);
        _settings.RememberMaster = dlg.RememberMe;
        SettingsService.Save(_settings);

        _passphrase = dlg.Passphrase;
        _servers = new List<ServerConfig>();
        CryptoStore.Save(_servers, _passphrase);

        if (dlg.RememberMe) CredentialVault.Set(_passphrase); else CredentialVault.Clear();
        return true;
    }

    private async Task<bool> UnlockVaultAsync()
    {
        var salt = Convert.FromHexString(_settings.MasterSaltHex ?? "");
        var saved = _settings.RememberMaster ? (CredentialVault.Get() ?? "") : "";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var dlg = new Dialogs.PassphraseDialog("Unlock vault", "Enter your master passphrase.",
                showRemember: true, rememberChecked: _settings.RememberMaster, defaultPassword: saved)
            {
                XamlRoot = this.Content.XamlRoot
            };

            await dlg.ShowAsync();
            if (!dlg.IsSuccess) return false;

            if (CryptoStore.HashPassphrase(dlg.Passphrase, salt) != _settings.MasterHashHex)
            {
                await ShowAlertAsync("Scarpa", "Incorrect master passphrase.");
                saved = "";
                continue;
            }

            _passphrase = dlg.Passphrase;
            _settings.RememberMaster = dlg.RememberMe;

            if (dlg.RememberMe) CredentialVault.Set(_passphrase);
            else CredentialVault.Clear();

            SettingsService.Save(_settings);

            try { _servers = CryptoStore.Load(_passphrase); return true; }
            catch (Exception ex)
            {
                await ShowAlertAsync("Could not open vault", ex.Message);
                return false;
            }
        }
        return false;
    }

    private async Task ShowAlertAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void Log(string message)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    // --- TreeView Logic ---

    private void RebuildTree()
    {
        var expandedFolders = new HashSet<string>();
        bool isFirstLoad = Tree.RootNodes.Count == 0;

        void SaveExpandedState(TreeViewNode node)
        {
            if (node.IsExpanded && _nodeTags.TryGetValue(node, out var tag) && tag is string path)
            {
                expandedFolders.Add(path);
            }
            foreach (var child in node.Children) SaveExpandedState(child);
        }

        foreach (var root in Tree.RootNodes) SaveExpandedState(root);

        Tree.RootNodes.Clear();
        _nodeTags.Clear();

        var rootData = new TreeItemData { Name = AppPaths.RootFolder, IconGlyph = "\uE8D5", IconColor = new SolidColorBrush(Microsoft.UI.Colors.Gold) };
        var rootNode = new TreeViewNode { Content = rootData, IsExpanded = isFirstLoad || expandedFolders.Contains(AppPaths.RootFolder) };
        _nodeTags[rootNode] = AppPaths.RootFolder;

        TreeViewNode GetOrCreateNode(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || folderPath == AppPaths.RootFolder) return rootNode;

            var parts = folderPath.Split('/');
            TreeViewNode current = rootNode;
            string currentPath = "";

            foreach (var part in parts)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";
                TreeViewNode? found = null;
                foreach (var item in current.Children)
                {
                    if (_nodeTags.TryGetValue(item, out var tag) && tag is string s && s == currentPath) { found = item; break; }
                }

                if (found == null)
                {
                    var folderData = new TreeItemData { Name = part, IconGlyph = "\uE8D5", IconColor = new SolidColorBrush(Microsoft.UI.Colors.Gold) };
                    found = new TreeViewNode { Content = folderData, IsExpanded = expandedFolders.Contains(currentPath) };
                    _nodeTags[found] = currentPath;

                    int insertIndex = 0;
                    while (insertIndex < current.Children.Count && _nodeTags.TryGetValue(current.Children[insertIndex], out var t) && t is string) insertIndex++;
                    current.Children.Insert(insertIndex, found);
                }
                current = found;
            }
            return current;
        }

        var allFolders = _settings.Folders.Concat(_servers.Select(s => s.Folder ?? "")).Where(f => !string.IsNullOrEmpty(f) && f != AppPaths.RootFolder).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in allFolders) GetOrCreateNode(f);

        foreach (var s in _servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parent = GetOrCreateNode(s.Folder ?? AppPaths.RootFolder);
            var serverData = new TreeItemData { Name = s.Name, IconGlyph = "\uE7F4", IconColor = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue) };
            var serverNode = new TreeViewNode { Content = serverData };
            _nodeTags[serverNode] = s;
            parent.Children.Add(serverNode);
        }

        SortTreeNodes(rootNode);
        Tree.RootNodes.Add(rootNode);
    }

    private ServerConfig? SelectedServer()
    {
        if (Tree.SelectedNodes.Count > 0 && _nodeTags.TryGetValue(Tree.SelectedNodes[0], out var tag)) return tag as ServerConfig;
        return null;
    }

    private string? SelectedFolder()
    {
        if (Tree.SelectedNodes.Count > 0 && _nodeTags.TryGetValue(Tree.SelectedNodes[0], out var tag)) return tag as string;
        return null;
    }

    private void Tree_DoubleClick(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Cancel the rename timer if a fast double-click is detected
        _renameTimer?.Stop();

        if (Tree.SelectedNodes.Count > 0)
        {
            var node = Tree.SelectedNodes[0];

            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is ServerConfig)
                {
                    // Fast double-click on a server
                    Ssh_Click(sender, new RoutedEventArgs());
                }
                else if (tag is string)
                {
                    // Fast double-click on a folder
                    node.IsExpanded = !node.IsExpanded;
                }
            }
        }
    }
    private void Tree_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element)
        {
            // Ignore clicks on the scrollbar so we don't deselect while scrolling
            if (FindParent<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(element) != null)
                return;

            // If the clicked element is not part of a TreeViewItem, it's empty space
            if (FindParent<TreeViewItem>(element) == null)
            {
                Tree.SelectedNodes.Clear();
            }
        }
    }
    private void Tree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element)
        {
            var treeViewItem = FindParent<TreeViewItem>(element);
            if (treeViewItem != null)
            {
                // Select the item that was right-clicked
                var node = Tree.NodeFromContainer(treeViewItem);
                if (node != null)
                {
                    Tree.SelectedNodes.Clear();
                    Tree.SelectedNodes.Add(node);
                }
            }
            else
            {
                // Right-clicked in white space, clear selection
                Tree.SelectedNodes.Clear();
            }
        }
    }
    private void ContextMenu_AddFolder_Click(object sender, RoutedEventArgs e)
    {
        // Expand the target folder so the user can see the new item when it's added
        var targetNode = GetTargetParentNode();
        targetNode.IsExpanded = true;

        // Call your existing folder creation logic
        NewFolder_Click(sender, e);
    }

    private void ContextMenu_AddServer_Click(object sender, RoutedEventArgs e)
    {
        // Expand the target folder so the user can see the new item when it's added
        var targetNode = GetTargetParentNode();
        targetNode.IsExpanded = true;

        // Call your existing server creation logic
        AddServer_Click(sender, e);
    }

    private TreeViewNode GetTargetParentNode()
    {
        if (Tree.SelectedNodes.Count > 0)
        {
            var node = Tree.SelectedNodes[0];

            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is string)
                {
                    // It's a folder, return it directly
                    return node;
                }
                else if (tag is ServerConfig)
                {
                    // It's a server, return its parent folder
                    return node.Parent;
                }
            }
        }

        // Fallback: Return the root folder if nothing is selected (white space)
        return Tree.RootNodes[0];
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);

        if (parent == null)
            return null;

        if (parent is T typedParent)
            return typedParent;

        return FindParent<T>(parent);
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter: Ssh_Click(sender, new RoutedEventArgs()); break;
            case VirtualKey.Delete: DeleteSelected_Click(sender, new RoutedEventArgs()); break;
        }
    }

    private void Ssh_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedServer();
        if (cfg == null) { Log("Select a server first."); return; }
        Log($"Launching SSH: {cfg.Name}");
        ConnectionLauncher.LaunchSsh(cfg, _settings);
    }

    private void SftpCli_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedServer();
        if (cfg == null) { Log("Select a server first."); return; }
        Log($"Launching SFTP CLI: {cfg.Name}");
        ConnectionLauncher.LaunchSftpCli(cfg, _settings);
    }

    private void SftpGui_Click(object sender, RoutedEventArgs e) { Log("SFTP GUI window needs porting."); }

    private IEnumerable<string> AllFolders() =>
    _servers.Select(s => s.Folder ?? "")
        .Concat(_settings.Folders)
        .Append(AppPaths.RootFolder)
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Distinct();

    private void Persist()
    {
        try { CryptoStore.Save(_servers, _passphrase); }
        catch (Exception ex) { Log($"ERROR saving vault: {ex.Message}"); }
    }

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Dialogs.ServerDialog(null, AllFolders(), this.Content.XamlRoot, SelectedFolder() ?? SelectedServer()?.Folder);
        if (!await dlg.ShowModalAsync()) return;

        _servers.Add(dlg.Config);
        Persist();
        RebuildTree();
        Log($"Added server {dlg.Config.Name}");
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedServer();
        if (cfg == null) { Log("Select a server first."); return; }

        var dlg = new Dialogs.ServerDialog(cfg, AllFolders(), this.Content.XamlRoot);
        if (!await dlg.ShowModalAsync()) return;

        _servers[_servers.IndexOf(cfg)] = dlg.Config;
        Persist();
        RebuildTree();
        Log($"Updated {dlg.Config.Name}");
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) { Log("Duplicate requires active servers."); }

    // --- Inline Rename Logic ---
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedNodes.Count == 0) return;

        var node = Tree.SelectedNodes[0];
        if (node.Content is TreeItemData data)
        {
            if (data.Name == AppPaths.RootFolder)
            {
                Log("Cannot rename the root folder.");
                return;
            }
            data.IsEditing = true;
        }
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            // Trigger immediately if it happens to load in a visible state
            if (tb.Visibility == Visibility.Visible)
            {
                tb.Focus(FocusState.Programmatic);
                tb.SelectAll();
            }

            // Listen for future visibility changes (when IsEditing becomes true)
            tb.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (s, dp) =>
            {
                if (s is TextBox t && t.Visibility == Visibility.Visible)
                {
                    // Use DispatcherQueue to ensure the UI has finished rendering the TextBox before focusing
                    t.DispatcherQueue.TryEnqueue(() =>
                    {
                        t.Focus(FocusState.Programmatic);
                        t.SelectAll();
                    });
                }
            });
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitRename((TextBox)sender);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelRename((TextBox)sender);
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename((TextBox)sender);

    private void CancelRename(TextBox textBox)
    {
        if (textBox.DataContext is TreeViewNode node && node.Content is TreeItemData data && data.IsEditing)
        {
            data.IsEditing = false;
        }
    }

    private void CommitRename(TextBox textBox)
    {
        // Cast DataContext to TreeViewNode, then check its Content
        if (!(textBox.DataContext is TreeViewNode treeNode) || !(treeNode.Content is TreeItemData data) || !data.IsEditing) return;

        data.IsEditing = false;

        string newName = textBox.Text.Trim().Replace("/", "").Replace("\\", "");
        if (string.IsNullOrWhiteSpace(newName) || newName == data.Name) return;

        // We already have the treeNode, so we can look it up in _nodeTags directly
        if (!_nodeTags.TryGetValue(treeNode, out var tag)) return;

        if (tag is ServerConfig server)
        {
            server.Name = newName;
            Persist();
            Log($"Renamed server to '{newName}'");
        }
        else if (tag is string oldFolder)
        {
            string parentPath = oldFolder.Contains('/') ? oldFolder.Substring(0, oldFolder.LastIndexOf('/')) : "";
            string newFolder = string.IsNullOrEmpty(parentPath) ? newName : $"{parentPath}/{newName}";

            for (int i = 0; i < _settings.Folders.Count; i++)
            {
                if (_settings.Folders[i] == oldFolder) _settings.Folders[i] = newFolder;
                else if (_settings.Folders[i].StartsWith(oldFolder + "/"))
                    _settings.Folders[i] = newFolder + _settings.Folders[i].Substring(oldFolder.Length);
            }
            SettingsService.Save(_settings);

            foreach (var srv in _servers)
            {
                if (srv.Folder == oldFolder) srv.Folder = newFolder;
                else if (srv.Folder != null && srv.Folder.StartsWith(oldFolder + "/"))
                    srv.Folder = newFolder + srv.Folder.Substring(oldFolder.Length);
            }
            Persist();
            Log($"Renamed folder to '{newName}'");
        }
        RebuildTree();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedNodes.Count == 0)
        {
            Log("Select an item to delete first.");
            return;
        }

        var selectedNode = Tree.SelectedNodes[0];
        if (!_nodeTags.TryGetValue(selectedNode, out var tag)) return;

        string itemName = "";
        string message = "";
        bool isFolder = false;

        if (tag is ServerConfig server)
        {
            itemName = server.Name;
            message = $"Are you sure you want to delete the server '{itemName}'?";
        }
        else if (tag is string folderPath)
        {
            if (folderPath == AppPaths.RootFolder)
            {
                Log("Cannot delete the root folder.");
                return;
            }
            isFolder = true;
            itemName = folderPath.Contains('/') ? folderPath.Substring(folderPath.LastIndexOf('/') + 1) : folderPath;
            message = $"Are you sure you want to delete the folder '{itemName}' and ALL servers inside it?";
        }
        else return;

        var dialog = new ContentDialog
        {
            Title = "Confirm Deletion",
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (isFolder)
        {
            string folderPath = (string)tag;
            _settings.Folders.RemoveAll(f => f == folderPath || f.StartsWith(folderPath + "/"));
            SettingsService.Save(_settings);

            _servers.RemoveAll(s => s.Folder == folderPath || (s.Folder != null && s.Folder.StartsWith(folderPath + "/")));
            Persist();
            Log($"Deleted folder '{itemName}' and its contents.");
        }
        else
        {
            var srv = (ServerConfig)tag;
            _servers.Remove(srv);
            Persist();
            Log($"Deleted server '{itemName}'.");
        }
        RebuildTree();
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var inputBox = new TextBox { PlaceholderText = "Enter folder name", AcceptsReturn = false };
        var dialog = new ContentDialog
        {
            Title = "Create Folder",
            Content = inputBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string newName = inputBox.Text.Trim().Replace("/", "").Replace("\\", "");
            if (string.IsNullOrWhiteSpace(newName)) { Log("Folder creation cancelled."); return; }

            string? parentFolder = SelectedFolder();
            if (parentFolder == AppPaths.RootFolder) parentFolder = null;
            string fullPath = string.IsNullOrEmpty(parentFolder) ? newName : $"{parentFolder}/{newName}";

            if (!_settings.Folders.Contains(fullPath))
            {
                _settings.Folders.Add(fullPath);
                SettingsService.Save(_settings);
                RebuildTree();
                Log($"Created folder '{fullPath}'.");
            }
            else { Log($"Folder '{fullPath}' already exists."); }
        }
    }

    private void ChangePassphrase_Click(object sender, RoutedEventArgs e) { Log("Passphrase Dialog needs porting."); }
    private void ForgetPassphrase_Click(object sender, RoutedEventArgs e) { Log("Forgot passphrase."); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            var now = DateTime.Now;
            if (_lastClickedNode == node)
            {
                var elapsed = (now - _lastClickTime).TotalMilliseconds;

                if (elapsed > 500 && elapsed < 3000)
                {
                    // Delay the rename slightly to see if this is actually the start of a fast double-click
                    _renameTimer?.Stop();
                    _renameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _renameTimer.Tick += (s, e) =>
                    {
                        _renameTimer.Stop();
                        if (node.Content is TreeItemData data && data.Name != AppPaths.RootFolder)
                        {
                            data.IsEditing = true;
                        }
                    };
                    _renameTimer.Start();
                }
            }

            _lastClickedNode = node;
            _lastClickTime = now;
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        await ShowAlertAsync("About", "Scarpa Connection Manager\nNative WinUI 3 Edition");
    }

    // --- Async File Pickers ---
    private async Task<string?> PickFileAsync(string filterExtension)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(filterExtension);
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void ImportScarpa_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".json");
        if (path != null) Log($"Import target selected: {path}");
    }

    private async void ImportPuttyFile_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".reg");
        if (path != null) Log($"Import target selected: {path}");
    }

    private async void ImportSecureCrt_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".xml");
        if (path != null) Log($"Import target selected: {path}");
    }

    private async void ImportMoba_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".mxtsessions");
        if (path != null) Log($"Import target selected: {path}");
    }

    private void ImportPuttyRegistry_Click(object sender, RoutedEventArgs e) { Log("Importing from registry."); }
    private void Export_Click(object sender, RoutedEventArgs e) { Log("Export File Picker needs porting."); }

    private void SortTreeNodes(TreeViewNode node)
    {
        if (node.Children.Count == 0) return;

        var sortedChildren = node.Children
            .OrderByDescending(n => _nodeTags.ContainsKey(n) && _nodeTags[n] is string)
            .ThenBy(n =>
            {
                if (_nodeTags.TryGetValue(n, out var tag))
                {
                    if (tag is string folderPath)
                        return folderPath.Contains("\\") ? folderPath.Substring(folderPath.LastIndexOf('\\') + 1) : folderPath;
                    if (tag is ServerConfig srv)
                        return srv.Name ?? "";
                }
                return "";
            })
            .ToList();

        node.Children.Clear();
        foreach (var child in sortedChildren)
        {
            SortTreeNodes(child);
            node.Children.Add(child);
        }
    }
}