using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace ScarpaConnectionManager.Models;

/// <summary>
/// Windows equivalent of the XDG data dir used by the Linux version:
/// %LOCALAPPDATA%\ScarpaConnectionManager
/// </summary>
public static class AppPaths
{
    public const string RootFolder = "Session";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScarpaConnectionManager");

    public static string ServerFile => Path.Combine(DataDir, "ssh_servers.json.enc");
    public static string SettingsFile => Path.Combine(DataDir, "scarpa_cm_settings.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}