using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Serialization;

namespace ScarpaConnectionManager.Models;

public class AndroidDevice
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "USB";

    // Saved Settings (Only one definition per property)
    public string CustomName { get; set; } = string.Empty;
    public string VideoBitrate { get; set; } = "Default";
    public string MaxResolution { get; set; } = "Default";
    public string WifiIpAddress { get; set; } = string.Empty;
    public bool TurnScreenOff { get; set; } = false;
    public bool EnableAudio { get; set; } = true;
    public bool HasSeenShortcutsPrompt { get; set; } = false;

    // UI Helpers (Ignored during save to prevent serialization crashes)
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName : $"Device: {Id}";

    [JsonIgnore]
    public bool IsOnline => State.ToLower() == "device";

    [JsonIgnore]
    public Brush StatusBrush => IsOnline
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Red);
}