// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Weims.Core;

/// <summary>
/// What Manager, the CLI and the broker share about Guest Tools: where a VM's broker keeps its state,
/// how to reach it, and how to start it. The broker itself is Weims.Broker.exe, one process per running VM.
/// </summary>
public static class Integration
{
    // 64 ports below Windows' dynamic range (49152 and up). Reserved port ranges live in the dynamic
    // range and move at every boot, so a fixed range outside it stays usable.
    public const int PortBase = 46400, PortCount = 64;
    public const string BrokerExecutableOverride = "WEIMS_BROKER_EXE";
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);

    public static string StateDirectory(VmConfig c) => Path.GetDirectoryName(c.ConfigPath)!;
    public static string StatePath(VmConfig c) => Path.Combine(StateDirectory(c), "integration.json");
    public static string SecretsPath(VmConfig c) => Path.Combine(StateDirectory(c), "integration-secrets.bin");

    /// <summary>Stable per-VM identifier derived from where the VM lives; safe in pipe and clipboard format names.</summary>
    public static string VmKey(VmConfig c)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(c.DirectoryPath).ToLowerInvariant())), 0, 8).ToLowerInvariant();
    public static string PipeName(VmConfig c) => "weims-broker-" + VmKey(c);
    public static int DefaultPort(VmConfig c) => PortBase + (int)(Convert.ToUInt32(VmKey(c)[..8], 16) % PortCount);
    public static bool InRange(int port) => port >= PortBase && port < PortBase + PortCount;

    public static string BrokerExecutable()
    {
        var custom = Environment.GetEnvironmentVariable(BrokerExecutableOverride);
        return string.IsNullOrWhiteSpace(custom) ? Path.Combine(AppContext.BaseDirectory, "Weims.Broker.exe") : Path.GetFullPath(custom);
    }

    /// <summary>
    /// Starts the broker for a running VM. It is started through the shell so that it inherits nothing
    /// from the caller: a script that reads the CLI's output through a pipe must not wait for the broker.
    /// </summary>
    public static void StartBroker(VmConfig c)
    {
        var exe = BrokerExecutable();
        if (!File.Exists(exe)) throw new FileNotFoundException(Text.Get("缺少集成组件：", "Missing integration component: ") + exe);
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--config"); info.ArgumentList.Add(c.ConfigPath);
        using var process = Process.Start(info);
    }

    /// <summary>True when this VM's QEMU was started by a version that puts QMP on a socket. Guest Tools need that.</summary>
    public static bool RuntimeSupported(JsonObject? runtime) => runtime?["qmp_socket"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0;

    // Counted per QEMU run: a VM that is started again begins with a clean count.
    static readonly Dictionary<string, (string Run, int Attempts, DateTime Last)> restarts = new(StringComparer.OrdinalIgnoreCase);
    public const int MaxRestartAttempts = 3;

    /// <summary>
    /// Called by whoever looks at a VM's state (Manager's poll, the CLI's status). There is no resident
    /// supervisor: if integration is on, the VM runs and its broker is gone, the caller starts it again.
    /// After three attempts in a row without the broker coming up, it stops trying until the VM is started again.
    /// </summary>
    public static BrokerState Supervise(VmConfig c, bool vmRunning, Func<DateTime>? clock = null, Action<VmConfig>? start = null)
    {
        var now = (clock ?? (() => DateTime.UtcNow))();
        if (!c.IntegrationEnabled || !vmRunning) { lock (restarts) restarts.Remove(c.ConfigPath); return BrokerState.Read(c, now); }
        var runtime = VmService.Runtime(c);
        if (!RuntimeSupported(runtime))
            return BrokerState.Offline(Text.Get("此虚拟机由旧版本启动。重启虚拟机后才能使用集成功能。", "This VM was started by an older version. Restart the VM to use integration."));
        var state = BrokerState.Read(c, now);
        lock (restarts)
        {
            if (state.Online) { restarts.Remove(c.ConfigPath); return state; }
            var run = $"{runtime!["pid"]}:{runtime["started_utc"]}";
            if (restarts.TryGetValue(c.ConfigPath, out var r) && r.Run != run) r = default;
            if (r.Attempts >= MaxRestartAttempts)
                return BrokerState.Offline(Text.Get("集成组件多次启动失败，已停止重试：", "The integration component failed to start repeatedly; no longer retrying: ") + (state.Error ?? Text.Get("没有心跳", "no heartbeat")));
            // Give a broker that was just started time to write its first heartbeat.
            if (r.Attempts > 0 && now - r.Last < HeartbeatTimeout) return state;
            restarts[c.ConfigPath] = (run, r.Attempts + 1, now);
        }
        try { (start ?? StartBroker)(c); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return BrokerState.Offline(e.Message); }
        return state;
    }

    /// <summary>Sends one command to the VM's broker over its named pipe, which only this Windows user can open.</summary>
    public static async Task<JsonObject> Command(VmConfig c, JsonObject request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", PipeName(c), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(2000, timeout.Token); }
        catch (Exception e) when (e is TimeoutException or IOException) { throw new IOException(Text.Get("集成组件没有运行。", "The integration component is not running."), e); }
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, Encoding.UTF8);
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException(Text.Get("集成组件没有应答。", "The integration component did not answer."));
        var response = JsonNode.Parse(line)?.AsObject() ?? throw new IOException(Text.Get("集成组件应答无效。", "Invalid answer from the integration component."));
        if (response["ok"]?.GetValue<bool>() != true) throw new IOException(response["error"]?.GetValue<string>() ?? Text.Get("集成组件拒绝了请求。", "The integration component refused the request."));
        return response;
    }
    public static Task<JsonObject> Command(VmConfig c, string command, CancellationToken token = default) => Command(c, new JsonObject { ["command"] = command }, token);

    /// <summary>
    /// Stores a VM's shared folders and upload limit and has a running broker take them up. Nothing else in the
    /// configuration is written, so this is safe while the VM runs and beside unsaved edits. Without a broker
    /// there is nobody to tell: it reads the configuration when it starts.
    /// </summary>
    /// <summary>Stores whether files cross with the clipboard and has a running broker take that up, as <see cref="ApplyShares"/> does for the folders.</summary>
    public static async Task ApplyClipboardFiles(VmConfig c, bool on, CancellationToken token = default)
    {
        bool before = c.ClipboardFiles;
        c.ClipboardFiles = on;
        try { c.SaveOnly("clipboard_files"); } catch { c.ClipboardFiles = before; throw; }
        if (!BrokerState.Read(c).Online) return;
        try { await Command(c, "reload", token); } catch (IOException) { }
    }

    public static async Task ApplyShares(VmConfig c, IReadOnlyList<ShareConfig> shares, int uploadLimitMiB, CancellationToken token = default)
    {
        var (before, limit) = (c.Shares, c.UploadLimitMiB);
        c.Shares = shares; c.UploadLimitMiB = uploadLimitMiB;
        try { c.SaveOnly("shares", "upload_limit_mib"); } catch { c.Shares = before; c.UploadLimitMiB = limit; throw; }
        if (!BrokerState.Read(c).Online) return;
        try { await Command(c, "reload", token); } catch (IOException) { }
    }
}

/// <summary>
/// A Windows folder the guest may mount. The name is the SMB share's and becomes the volume's name in
/// macOS; a guest may only read a folder unless the configuration says otherwise.
/// </summary>
public sealed record ShareConfig(string Name, string Path, bool ReadOnly)
{
    public const int NameLimit = 32, CountLimit = 16;
    /// <summary>The share Guest Tools serves for itself, for the files that cross with the clipboard. No folder of the user's can have this name.</summary>
    public const string TransferName = "Weims Transfer";

    public static ShareConfig? Read(JsonNode? node)
    {
        if (node is not JsonObject o || o["name"] is not JsonValue n || !n.TryGetValue<string>(out var name) || o["path"] is not JsonValue p || !p.TryGetValue<string>(out var path)) return null;
        return new(name, path, o["read_only"] is not JsonValue r || !r.TryGetValue<bool>(out var readOnly) || readOnly);
    }
    public JsonObject ToJson() => new() { ["name"] = Name, ["path"] = Path, ["read_only"] = ReadOnly };

    /// <summary>Null for a name that can be an SMB share and a macOS volume; otherwise what is wrong with it.</summary>
    public static string? NameProblem(string name)
        => name.Length is > 0 and <= NameLimit && name == name.Trim() && name[0] != '.' && name[^1] != '.' && name.All(c => char.IsLetterOrDigit(c) || " -_.()".Contains(c)) ? null
            : Text.Get($"共享名称“{name}”无效：只能用文字、数字、空格和 - _ . ( )，最多 {NameLimit} 个字符，不能以点开头或结尾。", $"The share name \"{name}\" is not valid: use letters, digits, spaces and - _ . ( ), at most {NameLimit} characters, not starting or ending with a dot.");

    /// <summary>Null for a folder on a local drive, written out in full; otherwise what is wrong with it.</summary>
    public static string? PathProblem(string path)
    {
        bool ok = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && !path.Any(c => c < 32);
        try { ok = ok && System.IO.Path.GetFullPath(path) == path; } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { ok = false; }
        return ok ? null : Text.Get($"共享文件夹“{path}”应为本机磁盘上的完整路径，例如 D:\\Shared。", $"The shared folder \"{path}\" must be a full path on a local drive, such as D:\\Shared.");
    }

    public static string? Problem(IReadOnlyList<ShareConfig> shares)
    {
        if (shares.Count > CountLimit) return Text.Get($"最多共享 {CountLimit} 个文件夹。", $"At most {CountLimit} folders can be shared.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var share in shares)
        {
            if ((NameProblem(share.Name) ?? PathProblem(share.Path)) is { } problem) return problem;
            if (share.Name.Equals(TransferName, StringComparison.OrdinalIgnoreCase)) return Text.Get($"共享名称“{share.Name}”由 Guest Tools 自己使用，请换一个。", $"The share name \"{share.Name}\" is used by Guest Tools itself. Choose another.");
            if (!names.Add(share.Name)) return Text.Get($"共享名称“{share.Name}”重复。", $"The share name \"{share.Name}\" is used twice.");
        }
        return null;
    }

    /// <summary>A valid name for a folder that none of the others uses: the folder's own name where that will do.</summary>
    public static string SuggestName(string path, IEnumerable<string> taken)
    {
        var leaf = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (leaf.Length == 0) leaf = path.Length > 0 && char.IsAsciiLetter(path[0]) ? path[..1].ToUpperInvariant() : "";
        var cleaned = new string(leaf.Select(c => char.IsLetterOrDigit(c) || " -_.()".Contains(c) ? c : '-').ToArray()).Trim(' ', '.');
        if (cleaned.Length == 0) cleaned = "Shared";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase) { TransferName };
        for (int n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : " " + n;
            var name = cleaned[..Math.Min(cleaned.Length, NameLimit - suffix.Length)].TrimEnd(' ', '.') + suffix;
            if (!used.Contains(name)) return name;
        }
    }
}

/// <summary>What the broker says about one shared folder: "offline", "ready", "mounted" or "failed", and why.</summary>
public sealed record ShareState(string Name, bool ReadOnly, string State, string? Error);

/// <summary>
/// The broker's last published state. A killed broker cannot write "offline", so the reader decides:
/// a heartbeat older than ten seconds or a process that is gone means offline, whatever the file says.
/// </summary>
public sealed record BrokerState(bool Online, int Port, string? Fingerprint, string? HelperUser, string? HelperVersion, IReadOnlyList<string> PairedUsers, string? Error)
{
    public static BrokerState Offline(string? error = null) => new(false, 0, null, null, null, Array.Empty<string>(), error);
    public bool Connected => Online && HelperUser != null;
    /// <summary>A pairing window is open: a helper in the guest may ask to pair.</summary>
    public bool PairingOpen { get; init; }
    /// <summary>The four digits a helper is showing in the guest, for the user to compare; null while no helper has asked.</summary>
    public string? PairingDigits { get; init; }
    public string? PairingUser { get; init; }
    /// <summary>An install code is outstanding: the install image is waiting to be used in the guest.</summary>
    public bool InstallOpen { get; init; }
    /// <summary>The helper has unmounted the install volume; the stick can be unplugged.</summary>
    public bool InstallMediaEjected { get; init; }
    /// <summary>The shared folders as the broker serves them; empty when none are configured.</summary>
    public IReadOnlyList<ShareState> Shares { get; init; } = [];
    /// <summary>The connected helper can mount shared folders. False for a helper from before they existed.</summary>
    public bool HelperMounts { get; init; }
    /// <summary>What the broker has to say about the clipboard right now: something that did not cross and why, or how far a transfer of files is.</summary>
    public string? Note { get; init; }
    public string Label => !Online ? Text.Get("集成：离线", "Integration: offline") + (Error is null ? "" : " · " + Error)
        : Connected ? Text.Get($"集成：已连接（{HelperUser}）", $"Integration: connected ({HelperUser})")
        : PairedUsers.Count == 0 ? Text.Get("集成：等待配对", "Integration: waiting for pairing")
        : Text.Get("集成：等待客户机助手连接", "Integration: waiting for the guest helper");

    public static BrokerState Read(VmConfig c, DateTime? nowUtc = null)
    {
        try
        {
            var path = Integration.StatePath(c);
            if (!File.Exists(path)) return Offline();
            var j = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string? Str(string key) => j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var error = Str("last_error");
            var heartbeat = DateTime.Parse(Str("heartbeat_utc")!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            if ((nowUtc ?? DateTime.UtcNow) - heartbeat > Integration.HeartbeatTimeout || Str("status") == "stopped" || !Alive(j["pid"]!.GetValue<int>(), Str("started_utc")!)) return Offline(error);
            var paired = j["paired_users"] is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToArray() : Array.Empty<string>();
            bool Future(string key) => Str(key) is { } until && DateTime.Parse(until, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() > (nowUtc ?? DateTime.UtcNow);
            bool pairingOpen = Future("pairing_until_utc");
            return new(true, j["port"]!.GetValue<int>(), Str("fingerprint"), Str("helper_user"), Str("helper_version"), paired, error)
            {
                PairingOpen = pairingOpen || Str("pairing_digits") != null, PairingDigits = Str("pairing_digits"), PairingUser = Str("pairing_user"),
                InstallOpen = Future("install_until_utc"), InstallMediaEjected = j["install_media_ejected"] is JsonValue e && e.TryGetValue<bool>(out var ejected) && ejected,
                HelperMounts = j["helper_mounts"] is JsonValue m && m.TryGetValue<bool>(out var mounts) && mounts, Note = Str("note"),
                Shares = j["shares"] is JsonArray shares
                    ? [.. shares.OfType<JsonObject>().Select(s => new ShareState(s["name"]!.GetValue<string>(), s["read_only"]!.GetValue<bool>(), s["state"]!.GetValue<string>(), s["error"]?.GetValue<string>()))] : []
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException) { return Offline(); }
    }
    static bool Alive(int pid, string startedUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var started = DateTime.Parse(startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            return !p.HasExited && Math.Abs((p.StartTime.ToUniversalTime() - started).TotalSeconds) < 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
