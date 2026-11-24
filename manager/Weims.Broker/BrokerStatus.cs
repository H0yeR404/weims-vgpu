// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Weims.Core;

namespace Weims.Broker;

/// <summary>
/// What the broker publishes about itself: to state\integration.json every two seconds, and to the
/// command pipe on request. It never contains tokens, codes, keys or the shared folders' password.
/// </summary>
public sealed class BrokerStatus(int qemuPid)
{
    public static readonly string Version = typeof(BrokerStatus).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
    public sealed class Fields
    {
        public int Port; public string? Fingerprint, HelperUser, HelperVersion, LastError, Note, PairingDigits, PairingUser;
        public DateTime? HelperSinceUtc, PairingUntilUtc, InstallUntilUtc;
        public IReadOnlyList<string> PairedUsers = [];
        public string ClipboardMode = "text";
        public bool Stopped, InstallMediaEjected;
        // Shared folders: what each one's state is, the port they are served on (0 when not), whether the
        // connected helper can mount them, and what is wrong with the configured list if anything is.
        public IReadOnlyList<ShareState> Shares = [];
        public int SharePort; public bool HelperMounts; public string? ShareProblem;
    }
    readonly object gate = new();
    readonly Fields fields = new();
    readonly DateTime started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    readonly SemaphoreSlim changed = new(0, 1);

    public void Update(Action<Fields> change)
    {
        lock (gate) change(fields);
        try { changed.Release(); } catch (SemaphoreFullException) { }
    }
    public T Read<T>(Func<Fields, T> read) { lock (gate) return read(fields); }

    public JsonObject Snapshot()
    {
        lock (gate)
            return new JsonObject
            {
                ["version"] = Version, ["protocol"] = ControlServer.ProtocolVersion,
                ["pid"] = Environment.ProcessId, ["started_utc"] = started.ToString("o"), ["qemu_pid"] = qemuPid,
                ["heartbeat_utc"] = DateTime.UtcNow.ToString("o"), ["status"] = fields.Stopped ? "stopped" : "running",
                ["port"] = fields.Port, ["fingerprint"] = fields.Fingerprint,
                ["helper_user"] = fields.HelperUser, ["helper_version"] = fields.HelperVersion, ["helper_since_utc"] = fields.HelperSinceUtc?.ToString("o"),
                ["paired_users"] = new JsonArray([.. fields.PairedUsers.Select(u => (JsonNode)u)]),
                ["pairing_until_utc"] = fields.PairingUntilUtc?.ToString("o"),
                // The number is shown on both screens for the user to compare; it is not a secret.
                ["pairing_digits"] = fields.PairingDigits, ["pairing_user"] = fields.PairingUser,
                // While an install code is good, and whether the guest has let go of the install volume. Never the code itself.
                ["install_until_utc"] = fields.InstallUntilUtc?.ToString("o"), ["install_media_ejected"] = fields.InstallMediaEjected,
                ["clipboard_mode"] = fields.ClipboardMode, ["last_error"] = fields.LastError, ["note"] = fields.Note,
                ["share_port"] = fields.SharePort, ["helper_mounts"] = fields.HelperMounts, ["share_problem"] = fields.ShareProblem,
                ["shares"] = new JsonArray([.. fields.Shares.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["read_only"] = s.ReadOnly, ["state"] = s.State, ["error"] = s.Error })])
            };
    }

    /// <summary>Writes the state file now, then on every change and at least every two seconds, until cancelled.</summary>
    public async Task Publish(string path, Action<string> log, CancellationToken stop)
    {
        while (true)
        {
            Write(path, log);
            try { await changed.WaitAsync(Integration.HeartbeatInterval, stop); }
            catch (OperationCanceledException) { return; }
        }
    }
    public void Write(string path, Action<string> log)
    {
        try { JsonFiles.Write(path, Snapshot()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("state file not written: " + e.Message); }
    }
}
