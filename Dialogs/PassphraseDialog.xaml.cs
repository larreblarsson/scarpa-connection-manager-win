using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class PassphraseDialog : ContentDialog
{
    public string Passphrase => PassBox.Password;
    public bool RememberMe => RememberCheck.IsChecked == true;

    public bool IsSuccess { get; private set; } = false;

    public PassphraseDialog(string title, string message, bool requireConfirm = false, bool showRemember = false, bool rememberChecked = false, string defaultPassword = "")
    {
        this.InitializeComponent();

        MessageText.Text = message;
        PassBox.Password = defaultPassword;

        if (showRemember)
        {
            RememberCheck.Visibility = Visibility.Visible;
            RememberCheck.IsChecked = rememberChecked;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Input validation: Don't let them click OK if it is blank
        if (string.IsNullOrWhiteSpace(PassBox.Password))
        {
            args.Cancel = true;
            return;
        }

        IsSuccess = true;
    }
}