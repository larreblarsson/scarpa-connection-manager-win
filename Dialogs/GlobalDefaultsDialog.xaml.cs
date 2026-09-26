using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace scarpa_connection_manager_win.Dialogs;

public sealed partial class GlobalDefaultsDialog : ContentDialog
{
    private bool _isColorUpdating = false;
    private ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;

    public GlobalDefaultsDialog()
    {
        this.InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        // 1. MUTE EVENTS while loading so the text boxes don't trigger the "Custom" dropdown switch!
        _isColorUpdating = true;

        // Load settings with fallback values
        PaletteBox.SelectedIndex = (int)(_localSettings.Values["GlobalDefaultPalette"] ?? 0);
        ColorSchemeBox.SelectedIndex = (int)(_localSettings.Values["GlobalDefaultScheme"] ?? 0);

        TermFgBox.Text = _localSettings.Values["GlobalDefaultFg"] as string ?? "#000000";
        TermBgBox.Text = _localSettings.Values["GlobalDefaultBg"] as string ?? "#FFFFDD";
        TermFontBox.Text = _localSettings.Values["GlobalDefaultFont"] as string ?? "Cascadia Mono 11";

        TermScrollbackBox.Value = (double)(_localSettings.Values["GlobalDefaultScrollback"] ?? 10000.0);
        LogPathBox.Text = _localSettings.Values["GlobalDefaultLogPath"] as string ?? "";

        // 2. UNMUTE EVENTS
        _isColorUpdating = false;

        // 3. Manually refresh the color preview squares since we muted the events
        UpdateColorPreview(TermFgBox.Text, TermFgPreview);
        UpdateColorPreview(TermBgBox.Text, TermBgPreview);
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Save settings
        _localSettings.Values["GlobalDefaultPalette"] = PaletteBox.SelectedIndex;
        _localSettings.Values["GlobalDefaultScheme"] = ColorSchemeBox.SelectedIndex;
        _localSettings.Values["GlobalDefaultFg"] = TermFgBox.Text;
        _localSettings.Values["GlobalDefaultBg"] = TermBgBox.Text;
        _localSettings.Values["GlobalDefaultFont"] = TermFontBox.Text;
        _localSettings.Values["GlobalDefaultScrollback"] = TermScrollbackBox.Value;
        _localSettings.Values["GlobalDefaultLogPath"] = LogPathBox.Text;
    }

    private void ColorSchemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isColorUpdating) return;

        if (ColorSchemeBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            _isColorUpdating = true;
            string scheme = item.Content.ToString() ?? "";

            switch (scheme)
            {
                case "Black on light yellow":
                    TermFgBox.Text = "#000000"; TermBgBox.Text = "#FFFFDD"; break;
                case "Black on white":
                    TermFgBox.Text = "#000000"; TermBgBox.Text = "#FFFFFF"; break;
                case "Gray on black":
                    TermFgBox.Text = "#AAAAAA"; TermBgBox.Text = "#000000"; break;
                case "Green on black":
                    TermFgBox.Text = "#00FF00"; TermBgBox.Text = "#000000"; break;
                case "White on black":
                    TermFgBox.Text = "#FFFFFF"; TermBgBox.Text = "#000000"; break;
            }

            // CRITICAL FIX: Force the color bars to redraw instantly, bypassing the hidden text boxes
            UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            _isColorUpdating = false;
        }
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            if (tb.Equals(TermFgBox)) UpdateColorPreview(TermFgBox.Text, TermFgPreview);
            if (tb.Equals(TermBgBox)) UpdateColorPreview(TermBgBox.Text, TermBgPreview);

            // CRITICAL FIX: Only switch to "Custom" if the user is actively typing in the box
            if (!_isColorUpdating && ColorSchemeBox.SelectedIndex != 5 && tb.FocusState != FocusState.Unfocused)
            {
                _isColorUpdating = true;
                ColorSchemeBox.SelectedIndex = 5;
                _isColorUpdating = false;
            }
        }
    }

    private void UpdateColorPreview(string hex, Microsoft.UI.Xaml.Shapes.Rectangle preview)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith("#"))
            {
                hex = hex.TrimStart('#');
                byte a = 255;
                int offset = 0;

                // Support both #RRGGBB and #AARRGGBB hex formats
                if (hex.Length == 8)
                {
                    a = Convert.ToByte(hex.Substring(0, 2), 16);
                    offset = 2;
                }

                if (hex.Length == 6 || hex.Length == 8)
                {
                    byte r = Convert.ToByte(hex.Substring(offset, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);

                    preview.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
                }
            }
        }
        catch { } // Silently ignore malformed colors
    }

    private void TermFgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        TermFgBox.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        ColorSchemeBox.SelectedIndex = 5;
    }

    private void TermBgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        TermBgBox.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        ColorSchemeBox.SelectedIndex = 5;
    }

    private async void BrowseLogPath_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        folderPicker.FileTypeFilter.Add("*");

        // Retrieve the window handle (HWND) of the current WinUI 3 window to attach the picker
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null) LogPathBox.Text = folder.Path;
    }
}