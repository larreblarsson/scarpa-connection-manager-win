using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class PassphraseDialog : ContentDialog
{
    public string Passphrase => PassphraseInput.Password;
    public bool RememberMe => RememberCheck.IsChecked == true;
    private bool _requireConfirm;

    public PassphraseDialog(string title, string message, bool requireConfirm = false, bool showRemember = false, bool rememberChecked = false, string defaultPassword = "")
    {
        this.InitializeComponent();
        this.Title = title;
        MessageText.Text = message;
        _requireConfirm = requireConfirm;

        if (requireConfirm) ConfirmInput.Visibility = Visibility.Visible;
        if (showRemember)
        {
            RememberCheck.Visibility = Visibility.Visible;
            RememberCheck.IsChecked = rememberChecked;
        }
        if (!string.IsNullOrEmpty(defaultPassword))
        {
            PassphraseInput.Password = defaultPassword;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrEmpty(PassphraseInput.Password))
        {
            args.Cancel = true; // Stops the dialog from closing
            ErrorText.Text = "Passphrase cannot be empty.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (_requireConfirm && PassphraseInput.Password != ConfirmInput.Password)
        {
            args.Cancel = true;
            ErrorText.Text = "Passphrases do not match.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
    }
}