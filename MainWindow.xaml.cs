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
        if (this.Content is FrameworkElement root)
        {
            root.Loaded += OnLoaded;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();

        // TEMPORARY BYPASS: Since your custom Passphrase Dialogs haven't been ported 
        // to WinUI 3 yet, we will bypass the locking mechanism just to get the UI rendering.
        // We will wire up the asynchronous unlock logic in the next step!

        RebuildTree();
        Log("Application loaded successfully in WinUI 3.");
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
    private void AddServer_Click(object sender, RoutedEventArgs e) { Log("Server Dialog needs porting."); }
    private void EditServer_Click(object sender, RoutedEventArgs e) { Log("Server Dialog needs porting."); }
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