using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScarpaConnectionManager.Models;

namespace ScarpaConnectionManager.Dialogs;

public partial class ServerDialog : Wpf.Ui.Controls.FluentWindow
{
    public ServerConfig Config { get; }

    public ServerDialog(ServerConfig? existing, IEnumerable<string> folders, string? preselectedFolder = null)
    {
        InitializeComponent();
        Config = existing?.Clone() ?? new ServerConfig { Folder = preselectedFolder ?? AppPaths.RootFolder };
        Title = existing == null ? "Add Server" : $"Edit — {existing.Name}";

        foreach (var f in folders.Distinct().OrderBy(f => f)) FolderBox.Items.Add(f);
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var c = Config;
        NameBox.Text = c.Name;
        FolderBox.Text = c.Folder;
        HostBox.Text = c.Host;
        PortBox.Text = c.Port.ToString();
        UserBox.Text = c.User;
        PassBox.Password = c.Password ?? "";
        KeyBox.Text = c.KeyFile ?? "";
        AuthBox.SelectedIndex = c.AuthMethod switch { "key_file" => 1, "ask" => 2, _ => 0 };

        foreach (var f in c.PortForwards) ForwardList.Items.Add(f);
        foreach (var s in c.AutoSequence) StepList.Items.Add(s);

        FontBox.Text = c.TermFont;
        FgBox.Text = c.TermForeground;
        BgBox.Text = c.TermBackground;
        ScrollbackBox.Text = c.TermScrollback.ToString();

        LogEnabled.IsChecked = c.LoggingEnabled;
        LogPathBox.Text = c.LogPath ?? "";
        LogModeBox.SelectedIndex = c.LogMode == "append" ? 1 : 0;

        AntiIdleEnabled.IsChecked = c.AntiIdleEnabled;
        AntiIdleInterval.Text = c.AntiIdleInterval.ToString();

        RdpEnabled.IsChecked = c.RdpEnabled;
        RdpPortBox.Text = c.RdpPort.ToString();
        RdpResBox.Text = c.RdpResolution;
        RdpAudio.IsChecked = c.RdpAudio;
        RdpClipboard.IsChecked = c.RdpClipboard;
        RdpCert.IsChecked = c.RdpIgnoreCert;
        RdpDrive.IsChecked = c.RdpRedirectDrive;
    }

    private string SelectedAuth() =>
        (AuthBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "password";

    private void Auth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordPanel == null || KeyPanel == null) return;
        var auth = SelectedAuth();
        PasswordPanel.Visibility = auth == "password" ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = auth == "key_file" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select private key",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) KeyBox.Text = dlg.FileName;
    }

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Log file",
            FileName = "%N_%Y-%M-%D.log",
            InitialDirectory = AppPaths.LogDir
        };
        if (dlg.ShowDialog(this) == true) LogPathBox.Text = dlg.FileName;
    }

    private void AddForward_Click(object sender, RoutedEventArgs e)
    {
        var type = (FwdType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Local";
        if (!int.TryParse(FwdSrc.Text, out var src)) { MessageBox.Show(this, "Source port must be a number."); return; }
        var rule = new PortForward { Type = type, SourcePort = src };
        if (type != "Dynamic")
        {
            if (!int.TryParse(FwdDest.Text, out var dst) || string.IsNullOrWhiteSpace(FwdHost.Text))
            { MessageBox.Show(this, "Destination host and port are required."); return; }
            rule.DestHost = FwdHost.Text.Trim();
            rule.DestPort = dst;
        }
        ForwardList.Items.Add(rule);
        FwdSrc.Clear(); FwdHost.Clear(); FwdDest.Clear();
    }

    private void RemoveForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardList.SelectedItem != null) ForwardList.Items.Remove(ForwardList.SelectedItem);
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        double.TryParse(SeqDelay.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var delay);
        StepList.Items.Add(new SequenceStep { Expect = SeqExpect.Text, Send = SeqSend.Text, Delay = delay });
        SeqExpect.Clear(); SeqSend.Clear(); SeqDelay.Text = "0";
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem != null) StepList.Items.Remove(StepList.SelectedItem);
    }

    private void MoveStep(int delta)
    {
        var i = StepList.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= StepList.Items.Count) return;
        var item = StepList.Items[i];
        StepList.Items.RemoveAt(i);
        StepList.Items.Insert(j, item);
        StepList.SelectedIndex = j;
    }

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { MessageBox.Show(this, "A display name is required."); return; }
        if (string.IsNullOrWhiteSpace(HostBox.Text)) { MessageBox.Show(this, "A host is required."); return; }

        var c = Config;
        c.Name = NameBox.Text.Trim();
        c.Folder = string.IsNullOrWhiteSpace(FolderBox.Text) ? AppPaths.RootFolder : FolderBox.Text.Trim();
        c.Host = HostBox.Text.Trim();
        c.Port = int.TryParse(PortBox.Text, out var p) ? p : 22;
        c.User = UserBox.Text.Trim();
        c.AuthMethod = SelectedAuth();
        c.Password = c.AuthMethod == "password" ? PassBox.Password : null;
        c.KeyFile = c.AuthMethod == "key_file" ? KeyBox.Text.Trim() : null;

        c.PortForwards = ForwardList.Items.Cast<PortForward>().ToList();
        c.AutoSequence = StepList.Items.Cast<SequenceStep>().ToList();

        c.TermFont = FontBox.Text;
        c.TermForeground = FgBox.Text;
        c.TermBackground = BgBox.Text;
        c.TermScrollback = int.TryParse(ScrollbackBox.Text, out var sb) ? sb : 10000;

        c.LoggingEnabled = LogEnabled.IsChecked == true;
        c.LogPath = string.IsNullOrWhiteSpace(LogPathBox.Text) ? null : LogPathBox.Text.Trim();
        c.LogMode = (LogModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "overwrite";

        c.AntiIdleEnabled = AntiIdleEnabled.IsChecked == true;
        c.AntiIdleInterval = int.TryParse(AntiIdleInterval.Text, out var ai) ? ai : 60;

        c.RdpEnabled = RdpEnabled.IsChecked == true;
        c.RdpPort = int.TryParse(RdpPortBox.Text, out var rp) ? rp : 3389;
        c.RdpResolution = string.IsNullOrWhiteSpace(RdpResBox.Text) ? "Full Screen" : RdpResBox.Text.Trim();
        c.RdpAudio = RdpAudio.IsChecked == true;
        c.RdpClipboard = RdpClipboard.IsChecked == true;
        c.RdpIgnoreCert = RdpCert.IsChecked == true;
        c.RdpRedirectDrive = RdpDrive.IsChecked == true;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}