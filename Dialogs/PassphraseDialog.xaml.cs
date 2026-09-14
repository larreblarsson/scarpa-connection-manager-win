using System;
using System.Windows;
using System.Windows.Controls;

namespace ScarpaConnectionManager.Dialogs;

public partial class PassphraseDialog : Wpf.Ui.Controls.FluentWindow
{
    // Point the property to the cleartext box to guarantee we always get the synced value
    public string Passphrase => Pass1Visible.Text;
    public bool RememberMe => RememberBox.IsChecked == true;

    private readonly bool _requireConfirm;
    private bool _isSyncing;

    public PassphraseDialog(string title, string prompt, bool requireConfirm = false,
        bool showRemember = false, bool rememberChecked = false, string defaultPassword = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _requireConfirm = requireConfirm;
        ConfirmPanel.Visibility = requireConfirm ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.Visibility = showRemember ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.IsChecked = rememberChecked;

        if (!string.IsNullOrEmpty(defaultPassword))
        {
            Pass1.Password = defaultPassword;
            // The Pass1_Changed event will automatically sync this to Pass1Visible
        }

        Loaded += (_, _) => Pass1.Focus();
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Passphrase)) { ShowError("Passphrase cannot be empty."); return; }
        if (_requireConfirm && Passphrase != Pass2Visible.Text) { ShowError("Passphrases do not match."); return; }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // --- Password Visibility & Sync Logic ---

    private void SyncPasswords(PasswordBox pb, TextBox tb, bool fromPasswordBox)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        if (fromPasswordBox) tb.Text = pb.Password;
        else pb.Password = tb.Text;
        _isSyncing = false;
    }

    private void Pass1_Changed(object sender, RoutedEventArgs e) => SyncPasswords(Pass1, Pass1Visible, true);
    private void Pass1Visible_Changed(object sender, TextChangedEventArgs e) => SyncPasswords(Pass1, Pass1Visible, false);
    
    private void Pass2_Changed(object sender, RoutedEventArgs e) => SyncPasswords(Pass2, Pass2Visible, true);
    private void Pass2Visible_Changed(object sender, TextChangedEventArgs e) => SyncPasswords(Pass2, Pass2Visible, false);

    private void TogglePass1_Click(object sender, RoutedEventArgs e) => ToggleVisibility(Pass1, Pass1Visible);
    private void TogglePass2_Click(object sender, RoutedEventArgs e) => ToggleVisibility(Pass2, Pass2Visible);

    private void ToggleVisibility(PasswordBox pb, TextBox tb)
    {
        if (pb.Visibility == Visibility.Visible)
        {
            pb.Visibility = Visibility.Collapsed;
            tb.Visibility = Visibility.Visible;
            tb.Focus();
            tb.Select(tb.Text.Length, 0); // Keep cursor at the end
        }
        else
        {
            tb.Visibility = Visibility.Collapsed;
            pb.Visibility = Visibility.Visible;
            pb.Focus();
        }
    }

	private void RememberBox_Checked(object sender, RoutedEventArgs e)
	{
		// Do not prompt if the window is simply initializing saved settings
		if (!IsLoaded) return;
	
		var message = "Security Warning:\n\n" +
					"Saving this passphrase means anyone with access to your Windows user account " +
					"can open this application, access all your sessions, and view your master password in clear text.\n\n" +
					"Are you sure you want to accept this risk?";
	
		var result = MessageBox.Show(this, message, "Security Risk Acceptance", 
			MessageBoxButton.YesNo, MessageBoxImage.Warning);
		
		if (result == MessageBoxResult.No)
		{
			RememberBox.IsChecked = false;
		}
	}
}