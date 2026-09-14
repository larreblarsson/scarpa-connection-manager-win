using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ScarpaConnectionManager.Dialogs;
using ScarpaConnectionManager.Models;
using ScarpaConnectionManager.Services;

namespace ScarpaConnectionManager.Views;

public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public string SizeText => IsDirectory ? "<DIR>" : FormatSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

public partial class SftpWindow : Window
{
    private readonly SftpService _sftp = new();
    private readonly ServerConfig _cfg;
    private readonly ObservableCollection<FileEntry> _local = new();
    private readonly ObservableCollection<FileEntry> _remote = new();

    public SftpWindow(ServerConfig cfg, string? password)
    {
        InitializeComponent();
        _cfg = cfg;
        Title = $"SFTP — {cfg.Name}";
        RemoteTitle.Text = $"Remote — {cfg.User}@{cfg.Host}";
        LocalList.ItemsSource = _local;
        RemoteList.ItemsSource = _remote;

        LoadLocal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        try
        {
            _sftp.Connect(cfg, password);
            LoadRemote(_sftp.WorkingDirectory);
            Status($"Connected to {cfg.Host}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Loaded += (_, _) => Close();
        }

        Closed += (_, _) => _sftp.Dispose();
    }

    private void Status(string s) => StatusText.Text = s;

    // ---------- local pane ----------
    private void LoadLocal(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            _local.Clear();
            if (dir.Parent != null)
                _local.Add(new FileEntry { Name = "..", FullPath = dir.Parent.FullName, IsDirectory = true });
            foreach (var d in dir.GetDirectories().OrderBy(d => d.Name))
                _local.Add(new FileEntry { Name = d.Name, FullPath = d.FullName, IsDirectory = true, Modified = d.LastWriteTime });
            foreach (var f in dir.GetFiles().OrderBy(f => f.Name))
                _local.Add(new FileEntry { Name = f.Name, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime });
            LocalPath.Text = dir.FullName;
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void LocalList_DoubleClick(object sender, PointerRoutedEventArgs e)
    {
        if (LocalList.SelectedItem is FileEntry { IsDirectory: true } d) LoadLocal(d.FullPath);
    }

    private void LocalGo_Click(object sender, RoutedEventArgs e) => LoadLocal(LocalPath.Text);
    private void LocalPath_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Key.Enter) LoadLocal(LocalPath.Text); }

    private void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(LocalPath.Text);
        if (parent != null) LoadLocal(parent.FullName);
    }

    // ---------- remote pane ----------
    private void LoadRemote(string path)
    {
        try
        {
            _remote.Clear();
            foreach (var f in _sftp.List(path).OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name))
                _remote.Add(new FileEntry
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsDirectory = f.IsDirectory,
                    Size = f.IsDirectory ? 0 : f.Length,
                    Modified = f.LastWriteTime
                });
            RemotePath.Text = path;
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void RemoteList_DoubleClick(object sender, PointerRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is FileEntry { IsDirectory: true } d) LoadRemote(d.FullPath);
    }

    private void RemoteGo_Click(object sender, RoutedEventArgs e) => LoadRemote(RemotePath.Text);
    private void RemotePath_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Key.Enter) LoadRemote(RemotePath.Text); }

    private void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        var p = RemotePath.Text.TrimEnd('/');
        var idx = p.LastIndexOf('/');
        LoadRemote(idx <= 0 ? "/" : p[..idx]);
    }

    // ---------- transfers ----------
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var items = LocalList.SelectedItems.Cast<FileEntry>().Where(i => !i.IsDirectory).ToList();
        if (items.Count == 0) { Status("Select one or more local files."); return; }
        var dest = RemotePath.Text.TrimEnd('/');

        foreach (var item in items)
        {
            Status($"Uploading {item.Name}…");
            Progress.Maximum = Math.Max(1, item.Size);
            await Task.Run(() => _sftp.Upload(item.FullPath, $"{dest}/{item.Name}",
                sent => Dispatcher.Invoke(() => Progress.Value = sent)));
        }
        Progress.Value = 0;
        Status($"Uploaded {items.Count} file(s).");
        LoadRemote(RemotePath.Text);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var items = RemoteList.SelectedItems.Cast<FileEntry>().Where(i => !i.IsDirectory).ToList();
        if (items.Count == 0) { Status("Select one or more remote files."); return; }
        var dest = LocalPath.Text;

        foreach (var item in items)
        {
            Status($"Downloading {item.Name}…");
            Progress.Maximum = Math.Max(1, item.Size);
            await Task.Run(() => _sftp.Download(item.FullPath, Path.Combine(dest, item.Name),
                got => Dispatcher.Invoke(() => Progress.Value = got)));
        }
        Progress.Value = 0;
        Status($"Downloaded {items.Count} file(s).");
        LoadLocal(dest);
    }

    private bool RemoteFocused => RemoteList.IsKeyboardFocusWithin || RemoteList.SelectedItem != null && !LocalList.IsKeyboardFocusWithin;

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLocal(LocalPath.Text);
        if (_sftp.IsConnected) LoadRemote(RemotePath.Text);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextInputDialog("New folder", "Folder name:", "New folder") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (RemoteFocused) { _sftp.CreateDirectory($"{RemotePath.Text.TrimEnd('/')}/{dlg.Value}"); LoadRemote(RemotePath.Text); }
            else { Directory.CreateDirectory(Path.Combine(LocalPath.Text, dlg.Value)); LoadLocal(LocalPath.Text); }
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var remote = RemoteFocused;
        if ((remote ? RemoteList.SelectedItem : LocalList.SelectedItem) is not FileEntry item) return;

        var dlg = new TextInputDialog("Rename", $"New name for “{item.Name}”:", item.Name) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (remote)
            {
                _sftp.Rename(item.FullPath, $"{RemotePath.Text.TrimEnd('/')}/{dlg.Value}");
                LoadRemote(RemotePath.Text);
            }
            else
            {
                var target = Path.Combine(LocalPath.Text, dlg.Value);
                if (item.IsDirectory) Directory.Move(item.FullPath, target); else File.Move(item.FullPath, target);
                LoadLocal(LocalPath.Text);
            }
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var remote = RemoteFocused;
        var items = (remote ? RemoteList.SelectedItems : LocalList.SelectedItems).Cast<FileEntry>()
            .Where(i => i.Name != "..").ToList();
        if (items.Count == 0) return;

        if (MessageBox.Show(this, $"Permanently delete {items.Count} item(s)?", "Confirm delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var item in items)
        {
            try
            {
                if (remote) _sftp.Delete(item.FullPath, item.IsDirectory);
                else if (item.IsDirectory) Directory.Delete(item.FullPath, true);
                else File.Delete(item.FullPath);
            }
            catch (Exception ex) { Status(ex.Message); }
        }
        if (remote) LoadRemote(RemotePath.Text); else LoadLocal(LocalPath.Text);
    }
}