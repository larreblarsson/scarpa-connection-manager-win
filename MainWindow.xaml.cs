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
using System.Text.RegularExpressions;

namespace scarpa_connection_manager_win;

public class TreeItemData : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private bool _isEditing;
    private bool _isSelected;

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

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(IndicatorVisibility));
        }
    }

    // Controls the blue vertical rectangle on the left
    public Visibility IndicatorVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    // Classic Windows 11 subtle grey full-row highlight (works in both light & dark mode)
    public Brush BackgroundBrush => IsSelected
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128))
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

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

    private List<TreeViewNode> _selectedNodes = new();
    private List<object>? _clipboardData = null;
    private bool _isCutOperation = false;

    private TreeViewNode? _lastClickedNode;
    private DateTime _lastClickTime = DateTime.MinValue;
    private DispatcherTimer? _renameTimer;
    private Dictionary<TreeViewNode, object> _nodeTags = new();

    public MainWindow()
    {
        this.InitializeComponent();
        this.Activated += (s, e) => Log($"[DEBUG] MainWindow Focus State: {e.WindowActivationState}");
        this.Title = "Scarpa Connection Manager";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        var windowWidth = 650;
        var windowHeight = 800;

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

    // --- Custom Classic Multiple Selection Logic ---
    private void HandleSelection(TreeViewNode clickedNode, bool isCtrlDown, bool isShiftDown)
    {
        if (isCtrlDown)
        {
            ToggleNodeSelection(clickedNode);
        }
        else if (isShiftDown && _lastClickedNode != null)
        {
            SelectRange(_lastClickedNode, clickedNode);
        }
        else
        {
            ClearSelection();
            SetNodeSelection(clickedNode, true);
        }
    }

    private void SetNodeSelection(TreeViewNode node, bool isSelected)
    {
        if (node.Content is TreeItemData data)
        {
            data.IsSelected = isSelected;
            if (isSelected && !_selectedNodes.Contains(node)) _selectedNodes.Add(node);
            else if (!isSelected) _selectedNodes.Remove(node);
        }
    }

    private void ToggleNodeSelection(TreeViewNode node)
    {
        if (node.Content is TreeItemData data)
        {
            SetNodeSelection(node, !data.IsSelected);
        }
    }

    private void ClearSelection()
    {
        foreach (var node in _selectedNodes.ToList()) SetNodeSelection(node, false);
        _selectedNodes.Clear();
    }

    private void SelectRange(TreeViewNode startNode, TreeViewNode endNode)
    {
        var visibleNodes = GetVisibleNodes(Tree.RootNodes);
        int startIndex = visibleNodes.IndexOf(startNode);
        int endIndex = visibleNodes.IndexOf(endNode);

        if (startIndex == -1 || endIndex == -1) return;

        int min = Math.Min(startIndex, endIndex);
        int max = Math.Max(startIndex, endIndex);

        ClearSelection();
        for (int i = min; i <= max; i++)
        {
            SetNodeSelection(visibleNodes[i], true);
        }
    }

    private List<TreeViewNode> GetVisibleNodes(IList<TreeViewNode> nodes)
    {
        var list = new List<TreeViewNode>();
        foreach (var node in nodes)
        {
            list.Add(node);
            if (node.IsExpanded)
            {
                list.AddRange(GetVisibleNodes(node.Children));
            }
        }
        return list;
    }

    // --- Clipboard & Iterative Naming Logic ---

    private string GetUniqueServerName(string baseName, string? targetFolder, ServerConfig? serverToIgnore = null, bool isCopy = false)
    {
        string normTarget = (targetFolder == AppPaths.RootFolder || string.IsNullOrWhiteSpace(targetFolder)) ? "" : targetFolder;

        var existingNames = _servers
            .Where(s => s != serverToIgnore)
            .Where(s => (s.Folder == AppPaths.RootFolder || string.IsNullOrWhiteSpace(s.Folder) ? "" : s.Folder) == normTarget)
            .Select(s => s.Name ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (isCopy) baseName = Regex.Replace(baseName, @" \(\d+\)$", "");

        if (!existingNames.Contains(baseName)) return baseName;

        int counter = 1;
        while (existingNames.Contains($"{baseName} ({counter})")) counter++;
        return $"{baseName} ({counter})";
    }

    private string GetUniqueFolderPath(string baseFolderPath, bool isCopy = false)
    {
        var existingFolders = AllFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (isCopy) baseFolderPath = Regex.Replace(baseFolderPath, @" \(\d+\)$", "");

        if (!existingFolders.Contains(baseFolderPath)) return baseFolderPath;

        int counter = 1;
        while (existingFolders.Contains($"{baseFolderPath} ({counter})")) counter++;
        return $"{baseFolderPath} ({counter})";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Tree.ContextFlyout?.Hide();
        ExecuteCopyCut(false);
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        Tree.ContextFlyout?.Hide();
        ExecuteCopyCut(true);
    }

    private void ExecuteCopyCut(bool isCut)
    {
        if (_selectedNodes.Count == 0) return;

        var itemsToCopy = new List<object>();
        foreach (var node in _selectedNodes)
        {
            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is string path && path == AppPaths.RootFolder) continue;
                itemsToCopy.Add(tag);
            }
        }

        if (itemsToCopy.Count == 0)
        {
            Log("Cannot cut or copy the root folder.");
            return;
        }

        _isCutOperation = isCut;
        _clipboardData = itemsToCopy;
        Log($"{(isCut ? "Cut" : "Copied")} {itemsToCopy.Count} item(s).");
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        Tree.ContextFlyout?.Hide();

        if (_clipboardData == null || _clipboardData.Count == 0)
        {
            Log("Clipboard is empty.");
            return;
        }

        string targetFolder = "";
        if (_selectedNodes.Count == 1 && _nodeTags.TryGetValue(_selectedNodes[0], out var tag))
        {
            targetFolder = tag is string path ? path : ((tag as ServerConfig)?.Folder ?? "");
        }

        if (targetFolder == AppPaths.RootFolder) targetFolder = "";

        foreach (var clipboardItem in _clipboardData)
        {
            if (clipboardItem is ServerConfig sourceServer)
            {
                if (_isCutOperation)
                {
                    if ((sourceServer.Folder ?? "") != targetFolder)
                    {
                        sourceServer.Name = GetUniqueServerName(sourceServer.Name ?? "", targetFolder, sourceServer, isCopy: false);
                        sourceServer.Folder = targetFolder;
                    }
                    Log($"Moved server '{sourceServer.Name}'.");
                }
                else
                {
                    var newServer = sourceServer.Clone();
                    newServer.Folder = targetFolder;
                    newServer.Name = GetUniqueServerName(sourceServer.Name ?? "", targetFolder, null, isCopy: true);
                    _servers.Add(newServer);
                    Log($"Pasted copied server '{newServer.Name}'.");
                }
            }
            else if (clipboardItem is string sourceFolder)
            {
                if (_isCutOperation)
                {
                    if (targetFolder == sourceFolder || (!string.IsNullOrEmpty(targetFolder) && targetFolder.StartsWith(sourceFolder + "/")))
                    {
                        Log($"Error: Cannot move '{sourceFolder}' into itself.");
                        continue;
                    }

                    string folderName = sourceFolder.Contains('/') ? sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1) : sourceFolder;
                    string newFolderPath = string.IsNullOrEmpty(targetFolder) ? folderName : $"{targetFolder}/{folderName}";

                    if (newFolderPath == sourceFolder)
                    {
                        Log($"Target is the same as the source for '{folderName}'.");
                        continue;
                    }

                    newFolderPath = GetUniqueFolderPath(newFolderPath, isCopy: false);

                    for (int i = 0; i < _settings.Folders.Count; i++)
                    {
                        if (_settings.Folders[i] == sourceFolder) _settings.Folders[i] = newFolderPath;
                        else if (_settings.Folders[i].StartsWith(sourceFolder + "/"))
                            _settings.Folders[i] = newFolderPath + _settings.Folders[i].Substring(sourceFolder.Length);
                    }

                    if (!_settings.Folders.Contains(newFolderPath)) _settings.Folders.Add(newFolderPath);

                    SettingsService.Save(_settings);

                    foreach (var srv in _servers)
                    {
                        if (srv.Folder == sourceFolder) srv.Folder = newFolderPath;
                        else if (srv.Folder != null && srv.Folder.StartsWith(sourceFolder + "/"))
                            srv.Folder = newFolderPath + srv.Folder.Substring(sourceFolder.Length);
                    }
                    Log($"Moved folder '{folderName}'.");
                }
                else
                {
                    string finalTarget = targetFolder;
                    if (finalTarget == sourceFolder)
                    {
                        finalTarget = sourceFolder.Contains('/') ? sourceFolder.Substring(0, sourceFolder.LastIndexOf('/')) : "";
                    }
                    else if (!string.IsNullOrEmpty(finalTarget) && finalTarget.StartsWith(sourceFolder + "/"))
                    {
                        Log($"Error: Cannot copy '{sourceFolder}' into its own subfolder.");
                        continue;
                    }

                    string folderName = sourceFolder.Contains('/') ? sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1) : sourceFolder;
                    string newFolderPath = string.IsNullOrEmpty(finalTarget) ? folderName : $"{finalTarget}/{folderName}";

                    newFolderPath = GetUniqueFolderPath(newFolderPath, isCopy: true);

                    var foldersToAdd = new List<string> { newFolderPath };
                    foreach (var f in _settings.Folders)
                    {
                        if (f.StartsWith(sourceFolder + "/")) foldersToAdd.Add(newFolderPath + f.Substring(sourceFolder.Length));
                    }
                    _settings.Folders.AddRange(foldersToAdd.Distinct());
                    SettingsService.Save(_settings);

                    var serversToAdd = new List<ServerConfig>();
                    foreach (var srv in _servers)
                    {
                        if (srv.Folder == sourceFolder || (srv.Folder != null && srv.Folder.StartsWith(sourceFolder + "/")))
                        {
                            var clone = srv.Clone();
                            clone.Folder = clone.Folder == sourceFolder ? newFolderPath : newFolderPath + clone.Folder!.Substring(sourceFolder.Length);
                            serversToAdd.Add(clone);
                        }
                    }
                    _servers.AddRange(serversToAdd);
                    Log($"Pasted copied folder '{folderName}'.");
                }
            }
        }

        if (_isCutOperation)
        {
            _clipboardData = null;
            _isCutOperation = false;
        }

        Persist();
        RebuildTree();
    }

    private void Tree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            string newFolderPath = "";
            if (args.NewParentItem is TreeViewNode parentNode && _nodeTags.TryGetValue(parentNode, out var parentTag))
            {
                if (parentTag is string path) newFolderPath = path == AppPaths.RootFolder ? "" : path;
            }

            bool modified = false;
            foreach (var item in args.Items)
            {
                if (item is TreeViewNode draggedNode && _nodeTags.TryGetValue(draggedNode, out var draggedObj))
                {
                    if (draggedObj is ServerConfig server)
                    {
                        server.Name = GetUniqueServerName(server.Name ?? "", newFolderPath, server, isCopy: false);
                        server.Folder = newFolderPath;
                        modified = true;
                    }
                    else if (draggedObj is string oldFolder)
                    {
                        string folderName = oldFolder.Contains('/') ? oldFolder.Substring(oldFolder.LastIndexOf('/') + 1) : oldFolder;
                        string newPath = string.IsNullOrEmpty(newFolderPath) ? folderName : $"{newFolderPath}/{folderName}";
                        newPath = GetUniqueFolderPath(newPath, isCopy: false);

                        foreach (var srv in _servers.Where(x => x.Folder == oldFolder || (x.Folder != null && x.Folder.StartsWith(oldFolder + "/"))))
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
        ClearSelection();

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
            var serverData = new TreeItemData { Name = s.Name ?? "", IconGlyph = "\uE7F4", IconColor = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue) };
            var serverNode = new TreeViewNode { Content = serverData };
            _nodeTags[serverNode] = s;
            parent.Children.Add(serverNode);
        }

        SortTreeNodes(rootNode);
        Tree.RootNodes.Add(rootNode);
    }

    private void Tree_DoubleClick(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _renameTimer?.Stop();

        if (_selectedNodes.Count > 0)
        {
            var node = _selectedNodes[0];

            // NEW: Block double-click actions (launching or expanding) if renaming
            if (node.Content is TreeItemData data && data.IsEditing)
                return;

            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is ServerConfig)
                {
                    Ssh_Click(sender, new RoutedEventArgs());
                }
                else if (tag is string)
                {
                    node.IsExpanded = !node.IsExpanded;
                }
            }
        }
        _lastClickedNode = null;
    }

    private void Tree_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element)
        {
            if (FindParent<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(element) != null)
                return;

            if (FindParent<TreeViewItem>(element) == null)
            {
                ClearSelection();
                // FIX: User clicked empty space, reset the rename "memory"
                _lastClickedNode = null;
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
                var node = Tree.NodeFromContainer(treeViewItem);
                if (node != null && !_selectedNodes.Contains(node))
                {
                    ClearSelection();
                    SetNodeSelection(node, true);
                    _lastClickedNode = node;
                }
            }
            else
            {
                ClearSelection();
                // FIX: User right-clicked empty space, reset the rename "memory"
                _lastClickedNode = null;
            }
        }
    }

    private void ContextMenu_AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var targetNode = GetTargetParentNode();
        targetNode.IsExpanded = true;
        NewFolder_Click(sender, e);
    }

    private async void ContextMenu_AddServer_Click(object sender, RoutedEventArgs e)
    {
        var targetNode = GetTargetParentNode();
        targetNode.IsExpanded = true;

        string? targetFolder = null;
        if (_nodeTags.TryGetValue(targetNode, out var tag) && tag is string path)
        {
            targetFolder = path == AppPaths.RootFolder ? null : path;
        }

        var dlg = new Dialogs.ServerDialog(null, AllFolders(), this.Content.XamlRoot, targetFolder);
        if (!await dlg.ShowModalAsync()) return;

        _servers.Add(dlg.Config);
        Persist();
        RebuildTree();

        string locationName = string.IsNullOrEmpty(targetFolder) ? "Root" : targetFolder;
        Log($"Added server '{dlg.Config.Name}' to {locationName}");
    }

    private TreeViewNode GetTargetParentNode()
    {
        if (_selectedNodes.Count > 0)
        {
            var node = _selectedNodes[0];
            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is string) return node;
                else if (tag is ServerConfig) return node.Parent;
            }
        }
        return Tree.RootNodes[0];
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        if (parent == null) return null;
        if (parent is T typedParent) return typedParent;
        return FindParent<T>(parent);
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool isCtrlDown = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (isCtrlDown)
        {
            if (e.Key == VirtualKey.C) { ExecuteCopyCut(false); e.Handled = true; return; }
            if (e.Key == VirtualKey.X) { ExecuteCopyCut(true); e.Handled = true; return; }
            if (e.Key == VirtualKey.V) { Paste_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        }

        switch (e.Key)
        {
            case VirtualKey.Enter: Ssh_Click(sender, new RoutedEventArgs()); break;
            case VirtualKey.Delete: DeleteSelected_Click(sender, new RoutedEventArgs()); break;
        }
    }

    private async void Ssh_Click(object sender, RoutedEventArgs e)
    {
        var nodes = _selectedNodes.ToList();
        if (nodes.Count == 0) { Log("Select a server first."); return; }

        foreach (var node in nodes)
        {
            if (node.Content is TreeItemData data && data.IsEditing) return;

            if (_nodeTags.TryGetValue(node, out var tag) && tag is ServerConfig cfg)
            {
                Log($"Launching SSH: {cfg.Name}");

                // Wait for the physical mouse button release (PointerReleased) 
                // BEFORE creating the window, so MainWindow doesn't steal focus back.
                await Task.Delay(250);

                ConnectionLauncher.LaunchSsh(cfg, _settings);
            }
        }
    }

    private async void SftpCli_Click(object sender, RoutedEventArgs e)
    {
        var nodes = _selectedNodes.ToList();
        if (nodes.Count == 0) { Log("Select a server first."); return; }

        foreach (var node in nodes)
        {
            if (_nodeTags.TryGetValue(node, out var tag) && tag is ServerConfig cfg)
            {
                Log($"Launching SFTP CLI: {cfg.Name}");

                // Wait for the physical mouse button release (PointerReleased)
                await Task.Delay(250);

                ConnectionLauncher.LaunchSftpCli(cfg, _settings);
            }
        }
    }

    private async void SftpGui_Click(object sender, RoutedEventArgs e)
    {
        var nodes = _selectedNodes.ToList();
        if (nodes.Count == 0) { Log("Select a server first."); return; }

        foreach (var node in nodes)
        {
            if (_nodeTags.TryGetValue(node, out var tag) && tag is ServerConfig cfg)
            {
                Log($"Opening SFTP GUI: {cfg.Name}");

                await Task.Delay(250); // Avoid focus-stealing

                // Launch the new graphical SFTP Window
                var sftpWindow = new Dialogs.SftpWindow(cfg);
                sftpWindow.Activate();
            }
        }
    }

    public IEnumerable<string> AllFolders() =>
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
        string? target = null;
        if (_selectedNodes.Count == 1 && _nodeTags.TryGetValue(_selectedNodes[0], out var tag))
        {
            target = tag is string path ? path : ((tag as ServerConfig)?.Folder ?? "");
        }

        var dlg = new Dialogs.ServerDialog(null, AllFolders(), this.Content.XamlRoot, target);
        if (!await dlg.ShowModalAsync()) return;

        _servers.Add(dlg.Config);
        Persist();
        RebuildTree();
        Log($"Added server {dlg.Config.Name}");
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNodes.Count != 1) { Log("Select exactly ONE server to edit."); return; }

        if (_nodeTags.TryGetValue(_selectedNodes[0], out var tag) && tag is ServerConfig cfg)
        {
            var dlg = new Dialogs.ServerDialog(cfg, AllFolders(), this.Content.XamlRoot);
            if (!await dlg.ShowModalAsync()) return;

            _servers[_servers.IndexOf(cfg)] = dlg.Config;
            Persist();
            RebuildTree();
            Log($"Updated {dlg.Config.Name}");
        }
    }

    // --- Inline Rename Logic ---
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        Tree.ContextFlyout?.Hide();

        if (_selectedNodes.Count != 1)
        {
            Log("Select exactly one item to rename.");
            return;
        }

        var node = _selectedNodes[0];
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
            if (tb.Visibility == Visibility.Visible)
            {
                tb.Focus(FocusState.Programmatic);
                tb.SelectAll();
            }

            tb.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (s, dp) =>
            {
                if (s is TextBox t && t.Visibility == Visibility.Visible)
                {
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
        if (!(textBox.DataContext is TreeViewNode treeNode) || !(treeNode.Content is TreeItemData data) || !data.IsEditing) return;

        data.IsEditing = false;

        string newName = textBox.Text.Trim().Replace("/", "").Replace("\\", "");
        if (string.IsNullOrWhiteSpace(newName) || newName == data.Name) return;

        if (!_nodeTags.TryGetValue(treeNode, out var tag)) return;

        if (tag is ServerConfig server)
        {
            server.Name = GetUniqueServerName(newName, server.Folder, server, isCopy: false);
            Persist();
            Log($"Renamed server to '{server.Name}'");
        }
        else if (tag is string oldFolder)
        {
            string parentPath = oldFolder.Contains('/') ? oldFolder.Substring(0, oldFolder.LastIndexOf('/')) : "";
            string newFolder = string.IsNullOrEmpty(parentPath) ? newName : $"{parentPath}/{newName}";
            newFolder = GetUniqueFolderPath(newFolder, isCopy: false);

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
            Log($"Renamed folder to '{newFolder}'");
        }
        RebuildTree();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        Tree.ContextFlyout?.Hide();

        var nodesToDelete = _selectedNodes.ToList();
        if (nodesToDelete.Count == 0)
        {
            Log("Select an item to delete first.");
            return;
        }

        var itemsToDelete = new List<object>();
        foreach (var node in nodesToDelete)
        {
            if (_nodeTags.TryGetValue(node, out var tag))
            {
                if (tag is string path && path == AppPaths.RootFolder) continue;
                itemsToDelete.Add(tag);
            }
        }

        if (itemsToDelete.Count == 0) return;

        string message = itemsToDelete.Count == 1
            ? $"Are you sure you want to delete this {(itemsToDelete[0] is ServerConfig ? "server" : "folder and ALL servers inside it")}?"
            : $"Are you sure you want to delete {itemsToDelete.Count} items (and their contents)?";

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

        foreach (var tag in itemsToDelete)
        {
            if (tag is string folderPath)
            {
                _settings.Folders.RemoveAll(f => f == folderPath || f.StartsWith(folderPath + "/"));
                _servers.RemoveAll(s => s.Folder == folderPath || (s.Folder != null && s.Folder.StartsWith(folderPath + "/")));
            }
            else if (tag is ServerConfig srv)
            {
                _servers.Remove(srv);
            }
        }

        SettingsService.Save(_settings);
        Persist();
        Log($"Deleted {itemsToDelete.Count} item(s).");
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

            string targetFolder = "";
            if (_selectedNodes.Count == 1 && _nodeTags.TryGetValue(_selectedNodes[0], out var tag))
            {
                targetFolder = tag is string path ? path : ((tag as ServerConfig)?.Folder ?? "");
            }
            if (targetFolder == AppPaths.RootFolder) targetFolder = "";

            string fullPath = string.IsNullOrEmpty(targetFolder) ? newName : $"{targetFolder}/{newName}";
            fullPath = GetUniqueFolderPath(fullPath, isCopy: false);

            _settings.Folders.Add(fullPath);
            SettingsService.Save(_settings);
            RebuildTree();
            Log($"Created folder '{fullPath}'.");
        }
    }

    private void ChangePassphrase_Click(object sender, RoutedEventArgs e) { Log("Passphrase Dialog needs porting."); }
    private void ForgetPassphrase_Click(object sender, RoutedEventArgs e) { Log("Forgot passphrase."); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            // NEW: Block further actions if this node is currently being renamed
            if (node.Content is TreeItemData nodeData && nodeData.IsEditing)
                return;

            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            bool isCtrlDown = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool isShiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            HandleSelection(node, isCtrlDown, isShiftDown);

            var now = DateTime.Now;
            if (_lastClickedNode == node && !isCtrlDown && !isShiftDown)
            {
                var elapsed = (now - _lastClickTime).TotalMilliseconds;

                if (elapsed > 500 && elapsed < 3000)
                {
                    _renameTimer?.Stop();
                    _renameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    _renameTimer.Tick += (s, ev) =>
                    {
                        _renameTimer.Stop();
                        if (_selectedNodes.Count == 1 && node.Content is TreeItemData data && data.Name != AppPaths.RootFolder)
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