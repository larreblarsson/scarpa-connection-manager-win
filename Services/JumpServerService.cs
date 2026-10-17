using Renci.SshNet;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ScarpaConnectionManager.Services;

public class JumpTunnel : IDisposable
{
    public SshClient JumpClient { get; }
    public ForwardedPortLocal ForwardedPort { get; }
    public string LocalHost { get; }
    public uint LocalPort => ForwardedPort.BoundPort;

    public JumpTunnel(SshClient jumpClient, ForwardedPortLocal forwardedPort, string localHost)
    {
        JumpClient = jumpClient;
        ForwardedPort = forwardedPort;
        LocalHost = localHost;
    }

    public void Dispose()
    {
        try
        {
            if (ForwardedPort != null && ForwardedPort.IsStarted)
                ForwardedPort.Stop();

            if (JumpClient != null && JumpClient.IsConnected)
                JumpClient.Disconnect();

            JumpClient?.Dispose();
        }
        catch { /* Failsafe cleanup */ }
    }
}

public static class JumpServerService
{
    /// <summary>
    /// Resolves whether a target server has a valid Jump Server assigned.
    /// </summary>
    public static ServerConfig? ResolveJumpServer(ServerConfig target, IEnumerable<ServerConfig> allServers)
    {
        if (string.IsNullOrWhiteSpace(target.JumpServerId)) return null;

        var match = allServers.FirstOrDefault(s =>
            string.Equals(s.Id, target.JumpServerId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, target.JumpServerId, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            Debug.WriteLine($"[JumpServerService] Resolved referenced jump server ID: {match.Name}");
        }
        return match;
    }

    public static async Task<JumpTunnel> EstablishTunnelAsync(ServerConfig jumpServer, string targetHost, int targetPort, string? jumpPassword = null)
    {
        return await Task.Run(() =>
        {
            Debug.WriteLine($"[JumpServerService] Connecting to Jump Host {jumpServer.Host}:{jumpServer.Port} as '{jumpServer.Username}'...");

            AuthenticationMethod auth;
            if (!string.IsNullOrEmpty(jumpServer.KeyPath) && System.IO.File.Exists(jumpServer.KeyPath))
            {
                var keyFile = string.IsNullOrEmpty(jumpServer.KeyPassphrase)
                    ? new PrivateKeyFile(jumpServer.KeyPath)
                    : new PrivateKeyFile(jumpServer.KeyPath, jumpServer.KeyPassphrase);
                auth = new PrivateKeyAuthenticationMethod(jumpServer.Username ?? "root", keyFile);
            }
            else
            {
                auth = new PasswordAuthenticationMethod(jumpServer.Username ?? "root", jumpPassword ?? jumpServer.Password ?? "");
            }

            var connectionInfo = new ConnectionInfo(
                jumpServer.Host,
                jumpServer.Port > 0 ? jumpServer.Port : 22,
                jumpServer.Username ?? "root",
                auth
            )
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            var client = new SshClient(connectionInfo);
            client.Connect();

            Debug.WriteLine($"[JumpServerService] Jump Host connected! Creating port forward to {targetHost}:{targetPort}...");

            var localPort = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);

            localPort.Exception += (sender, e) =>
            {
                Debug.WriteLine($"[JumpTunnel ERROR] {e.Exception.Message}");
            };

            localPort.RequestReceived += (sender, e) =>
            {
                Debug.WriteLine($"[JumpTunnel] Forwarding traffic from local port {localPort.BoundPort} to target.");
            };

            client.AddForwardedPort(localPort);
            localPort.Start();

            Debug.WriteLine($"[JumpServerService] Tunnel active on 127.0.0.1:{localPort.BoundPort}");

            return new JumpTunnel(client, localPort, "127.0.0.1");
        });
    }

    public static async Task<(ServerConfig TargetConfig, IDisposable? Tunnel)> PrepareConnectionAsync(ServerConfig cfg, IEnumerable<ServerConfig> allServers)
    {
        Debug.WriteLine($"[JumpServerService] PrepareConnectionAsync called for server: '{cfg.Name}' ({cfg.Host})");

        var jumpServer = ResolveJumpServer(cfg, allServers);
        if (jumpServer == null)
        {
            Debug.WriteLine($"[JumpServerService] No jump host configured or resolved for '{cfg.Name}'. Connecting directly.");
            return (cfg, null);
        }

        int targetPort = cfg.Port > 0 ? cfg.Port : 22;
        var tunnel = await EstablishTunnelAsync(jumpServer, cfg.Host, targetPort);

        var targetConfig = cfg.Clone();

        // Force explicit IPv4 instead of tunnel.LocalHost 
        targetConfig.Host = "127.0.0.1";
        targetConfig.Port = (int)tunnel.LocalPort;
        targetConfig.JumpServerId = null; // Wipe ID so it doesn't try to double-tunnel

        Debug.WriteLine($"[JumpServerService] Target redirected to 127.0.0.1:{targetConfig.Port}");

        return (targetConfig, tunnel);
    }
}