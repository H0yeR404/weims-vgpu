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
    static int RuntimePort(VmConfig c, JsonObject rt) => rt["qmp_port"]?.GetValue<int>() ?? c.QmpPort;
    public static async Task<VmState> State(VmConfig c)
    {
        try
        {
            var rt = Runtime(c);
            if (rt == null || !ProcessMatches(rt)) return new(true, false, Text.Get("已停止", "Stopped"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var qmp = await QmpClient.Connect(RuntimePort(c, rt), timeout.Token);
            await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
            var status = (await qmp.Execute("query-status", timeout.Token))?["status"]?.GetValue<string>();
            return new(false, true, status == "running" ? Text.Get("运行中", "Running") : status == "shutdown" ? Text.Get("系统已关机 · 可停止进程", "Guest shut down · Process can be stopped") : "QEMU：" + status);
        } catch { return new(false, false, Text.Get("状态未知 · 为保护磁盘已锁定操作", "Unknown state · Operations locked to protect the disk")); }
    }
    public static async Task RequireStopped(VmConfig c)
    {
        if (!(await State(c)).CanEdit) throw new InvalidOperationException(Text.Get("请先在 macOS 中关机并停止 QEMU，再修改或启动。", "Shut down macOS and stop QEMU before editing or starting."));
    }
    public static async Task Power(VmConfig c, string command)
    {
        if (command is not ("system_powerdown" or "quit")) throw new ArgumentException(Text.Get("不支持的电源操作。", "Unsupported power operation."));
        var rt = Runtime(c) ?? throw new IOException(Text.Get("没有运行记录。", "No runtime record."));
        if (!ProcessMatches(rt)) throw new IOException(Text.Get("虚拟机已停止。", "The VM has stopped."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var qmp = await QmpClient.Connect(RuntimePort(c, rt), timeout.Token);
        await qmp.VerifyName(RuntimeName(c, rt), timeout.Token);
        await qmp.Execute(command, timeout.Token);
    }
    public static void CheckPorts(VmConfig c)
    {
        foreach (var port in new[] { c.QmpPort, c.SshPort })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Server.ExclusiveAddressUse = true; listener.Start(); }
            catch (SocketException) { throw new IOException(Text.Get($"本机端口 {port} 已被占用，请修改网络设置。", $"Local port {port} is in use. Change the network settings.")); }
            finally { listener.Stop(); }
        }
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
        c.Save(); Directory.CreateDirectory(run);
        var args = plan.Arguments.Select(a => a.StartsWith("isa-applesmc,osk=") ? "isa-applesmc,osk=<redacted>" : a).ToArray();
        var file = Path.Combine(run, "command.json");
        JsonFiles.Write(file, new { executable = engine.Qemu, arguments = args, runtime = c.RuntimePath, run_directory = run, qmp_port = c.QmpPort, qmp_name = name });
        var env = new Dictionary<string,string>(plan.Environment) { ["WEIMS_OSK"] = osk };
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ProcessRunner.Run(engine.PowerShell, new[] { "-NoProfile", "-NonInteractive", "-File", launcher, "-Plan", file }, env, startupTimeout.Token, captureOutput: false);
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
