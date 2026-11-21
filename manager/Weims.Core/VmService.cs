// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Weims.Core;

public sealed record VmState(bool CanEdit, bool Running, string Label);

public static class VmService
{
    public static JsonObject? Runtime(VmConfig c) => File.Exists(c.RuntimePath) ? JsonNode.Parse(File.ReadAllText(c.RuntimePath))?.AsObject() : null;
    public static bool ProcessMatches(JsonObject rt)
    {
        try
        {
            using var p = Process.GetProcessById(rt["pid"]!.GetValue<int>());
            if (p.HasExited) return false;
            if (!string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(rt["executable"]!.GetValue<string>()), StringComparison.OrdinalIgnoreCase)) return false;
            return rt["started_utc"] is null || Math.Abs((p.StartTime.ToUniversalTime() - DateTime.Parse(rt["started_utc"]!.GetValue<string>()).ToUniversalTime()).TotalSeconds) < 2;
        } catch (ArgumentException) { return false; }
    }
    public static string RuntimeName(VmConfig c, JsonObject rt) => rt["qmp_name"]?.GetValue<string>() ?? "reims-" + Path.GetFileName(c.DirectoryPath);
    static string? RuntimeSocket(JsonObject? rt) => rt?["qmp_socket"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Length > 0 ? path : null;
    // A VM started by this version records its Unix socket. A record without one comes from an older
    // version whose QEMU listens on loopback TCP; keep managing that VM until it is restarted.
    // QEMU serves one QMP client at a time. Inside this process the users of one VM (the status poll,
    // shutdown, the install image) therefore take turns instead of one timing out behind another.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> qmpTurns = new(StringComparer.OrdinalIgnoreCase);
    static async Task<QmpClient> ConnectQmp(VmConfig c, JsonObject rt, CancellationToken token)
    {
        var turn = qmpTurns.GetOrAdd(c.ConfigPath, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(token);
        try
        {
            var qmp = await (RuntimeSocket(rt) is { } socket ? QmpClient.Connect(socket, token) : QmpClient.Connect(rt["qmp_port"]?.GetValue<int>() ?? c.QmpPort, token));
            qmp.Released = () => turn.Release();
            return qmp;
        }
        catch { turn.Release(); throw; }
    }
    /// <summary>A QMP connection to this VM's running QEMU, checked to be the one this VM started.</summary>
    internal static async Task<QmpClient> OpenQmp(VmConfig c, CancellationToken token)
    {
        var rt = Runtime(c) ?? throw new IOException(Text.Get("没有运行记录。", "No runtime record."));
        if (!ProcessMatches(rt)) throw new IOException(Text.Get("虚拟机已停止。", "The VM has stopped."));
        var qmp = await ConnectQmp(c, rt, token);
        try { await qmp.VerifyName(RuntimeName(c, rt), token); return qmp; }
        catch { qmp.Dispose(); throw; }
    }
    public static async Task<VmState> State(VmConfig c)
    {
        try
        {
            var rt = Runtime(c);
            if (rt == null || !ProcessMatches(rt)) return new(true, false, Text.Get("已停止", "Stopped"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var qmp = await ConnectQmp(c, rt, timeout.Token);
            await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
            var status = (await qmp.Execute("query-status", timeout.Token))?["status"]?.GetValue<string>();
            return new(false, true, status == "running" ? Text.Get("运行中", "Running") : status == "shutdown" ? Text.Get("系统已关机 · 可停止进程", "Guest shut down · Process can be stopped") : "QEMU：" + status);
        } catch { return new(false, false, Text.Get("状态未知 · 为保护磁盘已锁定操作", "Unknown state · Operations locked to protect the disk")); }
    }
    public static async Task RequireStopped(VmConfig c)
    {
        if (!(await State(c)).CanEdit) throw new InvalidOperationException(Text.Get("请先让虚拟机关机，再修改或启动。", "Shut down the VM before editing or starting."));
    }
    public static async Task Power(VmConfig c, string command)
    {
        if (command is not ("system_powerdown" or "quit")) throw new ArgumentException(Text.Get("不支持的电源操作。", "Unsupported power operation."));
        var rt = Runtime(c) ?? throw new IOException(Text.Get("没有运行记录。", "No runtime record."));
        if (!ProcessMatches(rt)) throw new IOException(Text.Get("虚拟机已停止。", "The VM has stopped."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var qmp = await ConnectQmp(c, rt, timeout.Token);
        await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
        await qmp.Execute(command, timeout.Token);
    }
    /// <summary>
    /// Asks macOS to quit all apps and shut down, as its Apple menu does (it asks about unsaved documents).
    /// QMP's system_powerdown cannot do this: it presses and releases the ACPI power button within a few
    /// milliseconds, and macOS ignores a press shorter than 0.35 s as a bounce. A key on the emulated
    /// keyboard can be held, so the request is the chord macOS documents for this:
    /// Control-Option-Command-Power.
    /// </summary>
    public static async Task Shutdown(VmConfig c)
    {
        var rt = Runtime(c) ?? throw new IOException(Text.Get("没有运行记录。", "No runtime record."));
        if (!ProcessMatches(rt)) throw new IOException(Text.Get("虚拟机已停止。", "The VM has stopped."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var qmp = await ConnectQmp(c, rt, timeout.Token);
        await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
        await qmp.Execute("input-send-event", Keys(true, "ctrl", "alt", "meta_l"), timeout.Token);
        try
        {
            await qmp.Execute("input-send-event", Keys(true, "power"), timeout.Token);
            await Task.Delay(ShutdownKeyHold, timeout.Token);
        }
        finally
        {
            // Always let the keys go; a guest left with modifiers held is hard to use.
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await qmp.Execute("input-send-event", Keys(false, "power", "meta_l", "alt", "ctrl"), release.Token);
        }
    }
    // Longer than macOS's 0.35 s debounce, shorter than the 1.5 s after which a held power key means something else.
    static readonly TimeSpan ShutdownKeyHold = TimeSpan.FromSeconds(1);
    static JsonObject Keys(bool down, params string[] qcodes)
    {
        var events = new JsonArray();
        foreach (var q in qcodes)
            events.Add(new JsonObject { ["type"] = "key", ["data"] = new JsonObject { ["down"] = down, ["key"] = new JsonObject { ["type"] = "qcode", ["data"] = q } } });
        return new JsonObject { ["events"] = events };
    }
    public static async Task StopAfterShutdown(VmConfig c)
    {
        var rt = Runtime(c);
        if (rt == null || !ProcessMatches(rt)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var qmp = await ConnectQmp(c, rt, timeout.Token);
        await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
        var status = (await qmp.Execute("query-status", timeout.Token))?["status"]?.GetValue<string>();
        if (status != "shutdown") throw new IOException(Text.Get("请先在 macOS 中正常关机，确认关机后再停止进程。", "Shut down macOS first. Stop only releases a guest confirmed shut down."));
        await qmp.Execute("quit", timeout.Token);
    }
    public static void CheckPorts(VmConfig c)
    {
        // SSH forwarding is the only TCP listener left. The qmp_port key is kept for older versions and is not probed.
        int port = c.SshPort;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Server.ExclusiveAddressUse = true; listener.Start(); }
        catch (SocketException) { throw new IOException(Text.Get($"本机端口 {port} 已被占用，请修改网络设置。", $"Local port {port} is in use. Change the network settings.")); }
        finally { listener.Stop(); }
    }
    public static async Task<string> Start(VmConfig c, EngineSettings engine, string osk, string launcher)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath)!);
        using var launchLock = new FileStream(Path.Combine(Path.GetDirectoryName(c.ConfigPath)!, "manager-launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        osk = LaunchPlan.ResolveOsk(osk);
        await RequireStopped(c); CheckPorts(c);
        var run = Path.Combine(c.DirectoryPath, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var name = "weims-" + Guid.NewGuid().ToString("N");
        var plan = LaunchPlan.Build(c, engine, run, osk, name);
        // RequireStopped has confirmed the previous QEMU is gone, so its leftover socket file can be removed.
        QmpEndpoint.Prepare(plan.QmpSocket, RuntimeSocket(Runtime(c)));
        c.Save(); Directory.CreateDirectory(run);
        var args = plan.Arguments.Select(a => a.StartsWith("isa-applesmc,osk=") ? "isa-applesmc,osk=<redacted>" : a).ToArray();
        var file = Path.Combine(run, "command.json");
        JsonFiles.Write(file, new { executable = engine.Qemu, arguments = args, runtime = c.RuntimePath, run_directory = run, qmp_socket = plan.QmpSocket, qmp_name = name });
        var env = new Dictionary<string,string>(plan.Environment) { ["WEIMS_OSK"] = osk };
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await ProcessRunner.Run(engine.PowerShell, new[] { "-NoProfile", "-NonInteractive", "-File", launcher, "-Plan", file }, env, startupTimeout.Token, captureOutput: false); }
        catch (IOException e) { throw new IOException(Text.Get("虚拟机启动失败，请查看：", "VM startup failed. See: ") + Path.Combine(run, "stderr.log"), e); }
        // Guest Tools are optional. The VM is already running here, and a broker that cannot be started must
        // not turn a successful start into a failure; whoever polls the state will try again.
        if (c.IntegrationEnabled)
            try { Integration.StartBroker(c); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { try { File.AppendAllText(Path.Combine(run, "broker.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} not started: {e.Message}{Environment.NewLine}"); } catch (IOException) { } }
        return run;
    }
}

public static class ProcessRunner
{
    public static async Task<string> Run(string exe, IEnumerable<string> args, IReadOnlyDictionary<string,string>? environment = null, CancellationToken token = default, bool captureOutput = true)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = captureOutput, RedirectStandardError = captureOutput };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (environment != null) foreach (var v in environment) info.Environment[v.Key] = v.Value;
        using var process = Process.Start(info) ?? throw new IOException(Text.Get("无法运行：", "Cannot run: ") + exe);
        var output = captureOutput ? process.StandardOutput.ReadToEndAsync(token) : Task.FromResult("");
        var error = captureOutput ? process.StandardError.ReadToEndAsync(token) : Task.FromResult("");
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(true); await process.WaitForExitAsync(); } catch { } throw; }
        string o = await output, e = await error;
        if (process.ExitCode != 0) throw new IOException(Text.Get($"{Path.GetFileName(exe)} 退出码 {process.ExitCode}\n", $"{Path.GetFileName(exe)} exited with code {process.ExitCode}\n") + e + o);
        return o;
    }
}
