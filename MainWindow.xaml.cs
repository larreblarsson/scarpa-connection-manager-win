using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;

namespace scarpa_connection_manager_win;

// 1. Pure Data Model for the TreeView
public class TreeItemData
{
    public string Name { get; set; }
    public string IconGlyph { get; set; }
    public Brush IconColor { get; set; }
}

public sealed partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private List<ServerConfig> _servers = new();
    private string _passphrase = "";

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

        // Constructor is now 100% clean of layout hacks and inline events!
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

    // --- Drag and Drop Logic (Now a proper event handler) ---
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

    // --- TreeView Logic (Strict Data Binding) ---

    private void RebuildTree()
    {
        // 1. Capture the currently expanded folders before we destroy the tree
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

        // Run the scan
        foreach (var root in Tree.RootNodes) SaveExpandedState(root);

        // Now clear the tree safely
        Tree.RootNodes.Clear();
        _nodeTags.Clear();

        var rootData = new TreeItemData { Name = AppPaths.RootFolder, IconGlyph = "\uE8D5", IconColor = new SolidColorBrush(Microsoft.UI.Colors.Gold) };

        // Root is expanded on first load, or if it was previously expanded
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

                    // 2. Restore the expanded state, defaulting to false (collapsed) if it wasn't open
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
        if (SelectedServer() != null) Ssh_Click(sender, new RoutedEventArgs());
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
        // No more hwnd! We just pass this.Content.XamlRoot
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

        // No more hwnd! We just pass this.Content.XamlRoot
        var dlg = new Dialogs.ServerDialog(cfg, AllFolders(), this.Content.XamlRoot);

        if (!await dlg.ShowModalAsync()) return;

        _servers[_servers.IndexOf(cfg)] = dlg.Config;
        Persist();
        RebuildTree();
        Log($"Updated {dlg.Config.Name}");
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) { Log("Duplicate requires active servers."); }
    private void Rename_Click(object sender, RoutedEventArgs e) { Log("Rename Dialog needs porting."); }
    private void DeleteSelected_Click(object sender, RoutedEventArgs e) { Log("Delete confirmation Dialog needs porting."); }
    private void NewFolder_Click(object sender, RoutedEventArgs e) { Log("New Folder Dialog needs porting."); }
    private void ChangePassphrase_Click(object sender, RoutedEventArgs e) { Log("Passphrase Dialog needs porting."); }
    private void ForgetPassphrase_Click(object sender, RoutedEventArgs e) { Log("Forgot passphrase."); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && node.Children.Count > 0)
        {
            node.IsExpanded = !node.IsExpanded;
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
            .OrderByDescending(n => _nodeTags.ContainsKey(n) && _nodeTags[n] is string) // Folders first
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