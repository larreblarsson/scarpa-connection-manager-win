using Renci.SshNet;
using ScarpaConnectionManager.Models;
using System;
using System.Collections.Generic;
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

        return allServers.FirstOrDefault(s =>
            string.Equals(s.Id, target.JumpServerId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, target.JumpServerId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Connects to the Jump Host and establishes a local SSH port forward to the target destination host/port.
    /// </summary>
    public static async Task<JumpTunnel> EstablishTunnelAsync(ServerConfig jumpServer, string targetHost, int targetPort, string? jumpPassword = null)
    {
        return await Task.Run(() =>
        {
            System.Diagnostics.Debug.WriteLine($"[JumpServerService] Authenticating to jump server {jumpServer.Host}:{jumpServer.Port} as {jumpServer.Username}...");

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

            System.Diagnostics.Debug.WriteLine($"[JumpServerService] Successfully connected to jump server.");

            // Bind to an OS-assigned available local port (0)
            System.Diagnostics.Debug.WriteLine($"[JumpServerService] Creating port forward to target {targetHost}:{targetPort}...");
            var localPort = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);

            // --- NEW DEBUG HOOKS ---
            localPort.Exception += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[JumpTunnel ERROR] Tunnel threw an exception: {e.Exception.Message}");
            };
            localPort.RequestReceived += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[JumpTunnel] Incoming connection received on local port {localPort.BoundPort} from {e.OriginatorHost}:{e.OriginatorPort}");
            };
            // -----------------------

            client.AddForwardedPort(localPort);
            localPort.Start();

            System.Diagnostics.Debug.WriteLine($"[JumpServerService] Tunnel established! Listening locally on 127.0.0.1:{localPort.BoundPort}");

            return new JumpTunnel(client, localPort, "127.0.0.1");
        });
    }

    public static async Task<(ServerConfig TargetConfig, IDisposable? Tunnel)> PrepareConnectionAsync(ServerConfig cfg, IEnumerable<ServerConfig> allServers)
    {
        if (string.IsNullOrEmpty(cfg.JumpServerId))
        {
            return (cfg, null);
        }

        var jumpServer = ResolveJumpServer(cfg, allServers);
        if (jumpServer == null)
        {
            System.Diagnostics.Debug.WriteLine($"[JumpServerService] Warning: Jump server ID '{cfg.JumpServerId}' not found. Falling back to direct connection.");
            return (cfg, null);
        }

        System.Diagnostics.Debug.WriteLine($"[JumpServerService] Preparing jump connection for {cfg.Name} (Target: {cfg.Host}:{cfg.Port})...");

        int targetPort = cfg.Port > 0 ? cfg.Port : 22;
        var tunnel = await EstablishTunnelAsync(jumpServer, cfg.Host, targetPort);

        var targetConfig = cfg.Clone();

        targetConfig.Host = "127.0.0.1";
        targetConfig.Port = (int)tunnel.LocalPort;
        targetConfig.JumpServerId = null;

        System.Diagnostics.Debug.WriteLine($"[JumpServerService] Connection prepared. Terminal should now connect to {targetConfig.Host}:{targetConfig.Port}");

        return (targetConfig, tunnel);
    }
}