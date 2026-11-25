// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Win32;
using Utilities;
using Weims.Core;

namespace Weims.Broker;

/// <summary>
/// Serves a VM's shared folders to its guest over SMB, from inside the broker: no Windows share is made,
/// no administrator is needed and the firewall is not touched. It listens on a loopback port of the
/// integration range, which the guest reaches at 10.0.2.2, and only while a paired helper is connected
/// or has been gone for less than <see cref="Grace"/>. The name and password are random for each time it
/// listens and reach the helper over the control connection; the guest does not store them.
/// Any local process and any running VM can connect to the port, so nothing is served before NTLMv2
/// authentication with that password.
///
/// Beside the user's folders it serves the share behind file copy and paste (<see cref="TransferStore"/>),
/// when it is given one and the setting is on, to a helper that knows what to do with it.
/// </summary>
public sealed class ShareServer(BrokerStatus status, RateLimit limit, Action<string> log, Func<int> controlPort, TransferStore? transfer = null)
{
    public const string Capability = "shares";
    /// <summary>How long the folders stay served after the helper's connection ended, for it to come back to its mounts.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The library's server on a port of ours: 445 belongs to Windows. SMB 2 and 3 only.</summary>
    sealed class PortServer(SMBShareCollection shares, GSSProvider security) : SMBServer(shares, security)
    {
        public void Start(int port) => Start(IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port, false, true, true, null);
    }
    /// <summary>For finding out what a client asks: with WEIMS_SMB_TRACE=1 every request is logged, file names included.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("WEIMS_SMB_TRACE") == "1";
    /// <param name="Transfer">The transfer share is served too.</param>
    sealed record Listening(PortServer Server, int Port, string User, string Password, IReadOnlyList<ShareConfig> Served, bool Transfer);

    readonly object gate = new();
    IReadOnlyList<ShareConfig> configured = [];
    bool transferOn;
    string? configProblem;
    Listening? listening;
    HelperSession? helper;
    CancellationTokenSource? leaving;
    // What cannot be served, and what the helper said about its mounts: share name to error, or to null for mounted.
    readonly Dictionary<string, string> unserved = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders to share and whether files cross with the clipboard, from the configuration: at the start and whenever it changed.</summary>
    public void Configure(IReadOnlyList<ShareConfig> shares, bool transferFiles = false)
    {
        lock (gate)
        {
            configProblem = ShareConfig.Problem(shares);
            var wanted = configProblem == null ? shares : [];
            bool transferWanted = transferFiles && transfer != null;
            if (wanted.SequenceEqual(configured) && transferWanted == transferOn) { Publish(); return; }
            configured = wanted; transferOn = transferWanted;
            // Folders that changed are served anew, under a new password: whoever holds the old one loses its mounts.
            Close("the shared folders changed");
            if (helper != null) Serve(helper);
            Publish();
        }
    }

    public void HelperConnected(HelperSession session)
    {
        lock (gate)
        {
            leaving?.Cancel(); leaving = null;
            reported.Clear();
            helper = session.Capabilities.Contains(Capability) ? session : null;
            if (helper != null) Serve(helper);
            // A helper from before shared folders existed took the place of one that had mounts: those are not coming back.
            else Close("the connected helper cannot mount shared folders");
            Publish();
        }
    }

    public void HelperDisconnected(HelperSession session)
    {
        lock (gate)
        {
            if (helper != session) return;
            helper = null; reported.Clear();
            if (listening != null)
            {
                var timer = leaving = new CancellationTokenSource();
                _ = Task.Delay(Grace, timer.Token).ContinueWith(_ => { lock (gate) { if (leaving == timer) { leaving = null; Close("no helper for " + (int)Grace.TotalSeconds + " s"); Publish(); } } }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            Publish();
        }
    }

    /// <summary>The helper's report of what it mounted: <c>shares_state</c> with "mounted" names and "failed" entries.</summary>
    public void FromGuest(HelperSession session, Frame frame)
    {
        lock (gate)
        {
            if (helper != session) return;
            reported.Clear();
            if (frame.Header["mounted"] is JsonArray mounted)
                foreach (var name in mounted.Take(ShareConfig.CountLimit)) if (name is JsonValue v && v.TryGetValue<string>(out var s)) reported[s] = null;
            if (frame.Header["failed"] is JsonArray failed)
                foreach (var entry in failed.Take(ShareConfig.CountLimit))
                    if (entry is JsonObject o && o["name"] is JsonValue n && n.TryGetValue<string>(out var s))
                        reported[s] = new string(((o["error"] is JsonValue e && e.TryGetValue<string>(out var text) ? text : null) ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
            Publish();
        }
    }

    public void Stop() { lock (gate) { leaving?.Cancel(); leaving = null; helper = null; Close("the broker stops"); Publish(); } }

    /// <summary>Starts listening if it is not, and tells the helper what there is to mount and how.</summary>
    void Serve(HelperSession session)
    {
        listening ??= Open();
        var offered = (listening?.Served ?? []).Select(s => new JsonObject { ["name"] = s.Name, ["read_only"] = s.ReadOnly }).ToList();
        // Hidden: mounted, but shown nowhere. A helper from before file copy and paste would show it, so it is not told.
        if (listening?.Transfer == true && session.Capabilities.Contains(FileTransfer.Capability)) offered.Add(new JsonObject { ["name"] = ShareConfig.TransferName, ["read_only"] = false, ["hidden"] = true });
        var message = new JsonObject { ["type"] = "shares", ["shares"] = new JsonArray([.. offered]) };
        if (offered.Count == 0) { _ = session.Send(message); return; }
        if (listening != null) { message["port"] = listening.Port; message["user"] = listening.User; message["password"] = listening.Password; }
        _ = session.Send(message);
    }

    Listening? Open()
    {
        unserved.Clear();
        var collection = new SMBShareCollection(); var served = new List<ShareConfig>();
        foreach (var share in configured)
        {
            try
            {
                var store = new GuardedStore(new NTDirectoryFileSystem(share.Path), new PathGuard(share.Path), share.ReadOnly, limit, log);
                var smb = new FileSystemShare(share.Name, store);
                bool readOnly = share.ReadOnly;
                smb.AccessRequested += (_, e) => e.Allow = !readOnly || (e.RequestedAccess & FileAccess.Write) == 0;
                collection.Add(smb); served.Add(share);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { unserved[share.Name] = e.Message; log($"shared folder \"{share.Name}\" is not served: {e.Message}"); }
        }
        if (transferOn)
        {
            var smb = new FileSystemShare(ShareConfig.TransferName, transfer!);
            smb.AccessRequested += (_, e) => e.Allow = (e.RequestedAccess & FileAccess.Write) == 0 || TransferStore.Writable(e.Path);
            collection.Add(smb);
        }
        if (collection.Count == 0) return null;

        string user = "weims-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var accounts = new IndependentNTLMAuthenticationProvider(name => string.Equals(name, user, StringComparison.OrdinalIgnoreCase) ? password : null);
        int first = controlPort() + 1 - Integration.PortBase;
        for (int i = 0; i < Integration.PortCount - 1; i++)
        {
            int port = Integration.PortBase + (first + i) % Integration.PortCount;
            var server = new PortServer(collection, new GSSProvider(accounts));
            server.LogEntryAdded += (_, entry) => { if (entry.Severity <= (Trace ? Severity.Debug : Severity.Warning)) log("smb: " + entry.Message); };
            try
            {
                server.Start(port);
                log($"shared folders: serving {string.Join(", ", served.Select(s => $"\"{s.Name}\"{(s.ReadOnly ? " (read-only)" : "")}").Concat(transferOn ? ["the share for copied files"] : []))} on 127.0.0.1:{port}");
                return new Listening(server, port, user, password, served, transferOn);
            }
            catch (SocketException) { server.Stop(); }
        }
        foreach (var share in served) unserved[share.Name] = Text.Get("集成端口全部被占用。", "All integration ports are in use.");
        log("shared folders: no free port in the integration range");
        return null;
    }

    void Close(string why)
    {
        if (listening == null) return;
        try { listening.Server.Stop(); } catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { }
        log("shared folders: stopped serving, " + why);
        listening = null;
    }

    void Publish()
    {
        var served = listening?.Served ?? [];
        var states = configured.Select(share =>
        {
            if (!served.Contains(share)) return new ShareState(share.Name, share.ReadOnly, "offline", unserved.GetValueOrDefault(share.Name));
            if (!reported.TryGetValue(share.Name, out var error)) return new ShareState(share.Name, share.ReadOnly, "ready", null);
            return new ShareState(share.Name, share.ReadOnly, error == null ? "mounted" : "failed", error);
        }).ToArray();
        int port = listening?.Port ?? 0; bool mounts = helper != null; var problem = configProblem;
        status.Update(s => { s.Shares = states; s.SharePort = port; s.HelperMounts = mounts; s.ShareProblem = problem; });
    }
}
