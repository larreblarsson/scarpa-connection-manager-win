using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace ScarpaConnectionManager.Models;

/// <summary>Port forwarding rule (ssh -L / -R / -D).</summary>
public sealed class PortForward
{
    [JsonPropertyName("type")] public string Type { get; set; } = "Local"; // Local | Remote | Dynamic
    [JsonPropertyName("source_port")] public int SourcePort { get; set; }
    [JsonPropertyName("dest_host")] public string DestHost { get; set; } = "";
    [JsonPropertyName("dest_port")] public int DestPort { get; set; }

    public override string ToString() => Type == "Dynamic"
        ? $"Dynamic  :{SourcePort}"
        : $"{Type}  :{SourcePort} -> {DestHost}:{DestPort}";
}

/// <summary>One step of the post-login automation sequence (expect/send).</summary>
public sealed class SequenceStep
{
    [JsonPropertyName("expect")] public string Expect { get; set; } = "";
    [JsonPropertyName("send")] public string Send { get; set; } = "";
    [JsonPropertyName("delay")] public double Delay { get; set; }

    public override string ToString() => $"expect \"{Expect}\"  ->  send \"{Send}\"";
}

/// <summary>A saved connection. Field names match the Linux/GTK JSON format so files interoperate.</summary>
public sealed class ServerConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 22;
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("auth_method")] public string AuthMethod { get; set; } = "password"; // password | key_file | ask
    [JsonPropertyName("key_file")] public string? KeyFile { get; set; }
    [JsonPropertyName("folder")] public string Folder { get; set; } = AppPaths.RootFolder;

    [JsonPropertyName("port_forwards")] public List<PortForward> PortForwards { get; set; } = new();
    [JsonPropertyName("auto_sequence")] public List<SequenceStep> AutoSequence { get; set; } = new();

    // Terminal appearance
    [JsonPropertyName("term_scheme")] public string TermScheme { get; set; } = "Default";
    [JsonPropertyName("term_font")] public string TermFont { get; set; } = "Cascadia Mono 11";
    [JsonPropertyName("term_fg")] public string TermForeground { get; set; } = "#D0D0D0";
    [JsonPropertyName("term_bg")] public string TermBackground { get; set; } = "#101010";
    [JsonPropertyName("term_scrollback")] public int TermScrollback { get; set; } = 10000;

    // Session logging
    [JsonPropertyName("logging_enabled")] public bool LoggingEnabled { get; set; }
    [JsonPropertyName("log_path")] public string? LogPath { get; set; }
    [JsonPropertyName("log_mode")] public string LogMode { get; set; } = "overwrite"; // overwrite | append
    [JsonPropertyName("log_timestamps")] public bool LogTimestamps { get; set; }

    // NEW: Append Data to Log settings (Mapped to snake_case to maintain Linux/GTK JSON compatibility)
    [JsonPropertyName("append_data_to_log")] public bool AppendDataToLog { get; set; }
    [JsonPropertyName("log_connect_string")] public string? LogConnectString { get; set; }
    [JsonPropertyName("log_disconnect_string")] public string? LogDisconnectString { get; set; }
    [JsonPropertyName("log_each_line_string")] public string? LogEachLineString { get; set; }

    // Keep-alive
    [JsonPropertyName("anti_idle_enabled")] public bool AntiIdleEnabled { get; set; }
    [JsonPropertyName("anti_idle_int")] public int AntiIdleInterval { get; set; } = 60;
    [JsonPropertyName("anti_idle_str")] public string AntiIdleString { get; set; } = "\\n";

    // Startup Command File
    [JsonPropertyName("startup_cmd_enabled")] public bool StartupCmdEnabled { get; set; }
    [JsonPropertyName("startup_cmd_path")] public string? StartupCmdPath { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("login_actions")]
    public System.Collections.Generic.List<LoginActionStep> LoginActions { get; set; } = new();

    // RDP
    [JsonPropertyName("rdp_enabled")] public bool RdpEnabled { get; set; }
    [JsonPropertyName("rdp_port")] public int RdpPort { get; set; } = 3389;
    [JsonPropertyName("rdp_res")] public string RdpResolution { get; set; } = "Full Screen";
    [JsonPropertyName("rdp_audio")] public bool RdpAudio { get; set; } = true;
    [JsonPropertyName("rdp_clipboard")] public bool RdpClipboard { get; set; } = true;
    [JsonPropertyName("rdp_cert_ignore")] public bool RdpIgnoreCert { get; set; } = true;
    [JsonPropertyName("rdp_drive")] public bool RdpRedirectDrive { get; set; }
    [JsonPropertyName("rdp_drive_path")] public string? RdpDrivePath { get; set; }

    public ServerConfig Clone()
    {
        // Automatically copies all basic properties (strings, ints, bools)
        var clone = (ServerConfig)this.MemberwiseClone();

        // Deep copy the lists so editing them doesn't affect the original until we hit Save
        if (this.LoginActions != null)
        {
            clone.LoginActions = new List<LoginActionStep>();
            foreach (var action in this.LoginActions)
            {
                clone.LoginActions.Add(new LoginActionStep { Expect = action.Expect, Send = action.Send, Timeout = action.Timeout });
            }
        }

        return clone;
    }
}

public class ScarpaExportRoot
{
    public System.Collections.Generic.List<ServerConfig> Servers { get; set; } = new();
    public System.Collections.Generic.List<string> Folders { get; set; } = new();
}

public class LoginActionStep
{
    [System.Text.Json.Serialization.JsonPropertyName("expect")]
    public string Expect { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("send")]
    public string Send { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 5;
}