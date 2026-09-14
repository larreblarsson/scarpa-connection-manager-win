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

public sealed partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private List<ServerConfig> _servers = new();
    private string _passphrase = "";

    // WinUI 3 TreeViewNodes don't have a Tag property, so we map the data here
    private Dictionary<TreeViewNode, object> _nodeTags = new();

    public MainWindow()
    {
        this.InitializeComponent();

        // 1. Set the Title
        this.Title = "Scarpa Connection Manager";

        // 2. Access the WinUI 3 AppWindow API to resize the window
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(700, 600));

        if (this.Content is FrameworkElement root)
        {
            root.Loaded += OnLoaded;
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

    private async Task<bool> CreateMasterPassphraseAsync()
    {
        var dlg = new Dialogs.PassphraseDialog("Set master passphrase",
            "Your connections are encrypted with AES-256. Choose a master passphrase — it cannot be recovered if lost.",
            requireConfirm: true, showRemember: true)
        {
            XamlRoot = this.Content.XamlRoot // Required in WinUI 3
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

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

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

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

    // --- UI Helpers ---

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
        LogBox.SelectionStart = LogBox.Text.Length; // Scrolls to bottom
    }

    // --- TreeView Logic ---

    private void RebuildTree()
    {
        Tree.RootNodes.Clear();
        _nodeTags.Clear();

        Border CreateHeader(string iconGlyph, string text, Brush iconColor)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = iconGlyph, Foreground = iconColor, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

            return new Border
            {
                Child = sp,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2)
            };
        }

        var rootNode = new TreeViewNode { Content = CreateHeader("\uE8D5", AppPaths.RootFolder, new SolidColorBrush(Microsoft.UI.Colors.Gold)), IsExpanded = true };
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
                    found = new TreeViewNode { Content = CreateHeader("\uE8D5", part, new SolidColorBrush(Microsoft.UI.Colors.Gold)), IsExpanded = true };
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
            var serverNode = new TreeViewNode { Content = CreateHeader("\uE7F4", s.Name, new SolidColorBrush(Microsoft.UI.Colors.SteelBlue)) };
            _nodeTags[serverNode] = s;
            parent.Children.Add(serverNode);
        }

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

    // --- Connections & Stubs (Awaiting Dialog Migrations) ---

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
        var dlg = new Dialogs.ServerDialog(null, AllFolders(), SelectedFolder() ?? SelectedServer()?.Folder)
        {
            XamlRoot = this.Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _servers.Add(dlg.Config);
        Persist();
        RebuildTree();
        Log($"Added server {dlg.Config.Name}");
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        var cfg = SelectedServer();
        if (cfg == null) { Log("Select a server first."); return; }

        var dlg = new Dialogs.ServerDialog(cfg, AllFolders())
        {
            XamlRoot = this.Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

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

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        await ShowAlertAsync("About", "Scarpa Connection Manager\nNative WinUI 3 Edition");
    }

    // --- Async File Pickers ---

    private async Task<string?> PickFileAsync(string filterExtension)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        // WinUI 3 requires linking the Picker to the Window Handle explicitly
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
}