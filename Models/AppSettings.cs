using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace ScarpaConnectionManager.Models;

public sealed class AppSettings
{
    [JsonPropertyName("master_passphrase_salt")] public string? MasterSaltHex { get; set; }
    [JsonPropertyName("master_passphrase_hash")] public string? MasterHashHex { get; set; }
    [JsonPropertyName("disclaimer_accepted")] public bool DisclaimerAccepted { get; set; }
    [JsonPropertyName("remember_master")] public bool RememberMaster { get; set; }

    /// <summary>Terminal host used for SSH/SFTP CLI sessions.</summary>
    [JsonPropertyName("terminal_host")] public string TerminalHost { get; set; } = "auto"; // auto | wt | conhost

    [JsonPropertyName("default_log_dir")] public string? DefaultLogDir { get; set; }
    [JsonPropertyName("expanded_folders")] public List<string> ExpandedFolders { get; set; } = new();
    [JsonPropertyName("folders")] public List<string> Folders { get; set; } = new();
}