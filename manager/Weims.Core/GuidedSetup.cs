// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Weims.Core;

public static class GuidedSetup
{
    public static string Helper(EngineSettings engine) => Path.Combine(Path.GetDirectoryName(engine.Qemu) ?? "", "reims-util.exe");
    public static string BootFiles(EngineSettings engine) => Path.Combine(Path.GetDirectoryName(engine.Qemu) ?? "", "boot");
    public static bool Available(EngineSettings engine) => engine.SupportsDisplayPolicy && File.Exists(Helper(engine)) && File.Exists(Path.Combine(BootFiles(engine), "ReimsGraphics.rom"));

    public static async Task EnsureRuntime(EngineSettings engine, string data, IProgress<string> progress, CancellationToken token)
    {
        if (Available(engine)) { await EnvironmentSetup.ValidateRuntime(engine, token); await EnvironmentSetup.CheckPowerShell(engine.PowerShell, token); return; }
        var prepared = await EnvironmentSetup.Prepare(data, progress, token);
        if (!Available(prepared)) throw new IOException(Text.Get("运行包不含自动安装组件，请更新预发布版。", "Runtime lacks guided setup. Update the prerelease."));
        JsonFiles.Write(Path.Combine(data, "engine.json"), prepared);
        engine.Qemu = prepared.Qemu; engine.RuntimeBin = prepared.RuntimeBin; engine.Bios = prepared.Bios; engine.PowerShell = prepared.PowerShell;
    }

    public static (int Qmp, int Ssh) FreePorts()
    {
        var a = new TcpListener(IPAddress.Loopback, 0); var b = new TcpListener(IPAddress.Loopback, 0);
        try { a.Start(); b.Start(); return (((IPEndPoint)a.LocalEndpoint).Port, ((IPEndPoint)b.LocalEndpoint).Port); }
        finally { a.Stop(); b.Stop(); }
    }

    public static async Task<VmConfig> Create(string name, string destination, string? image, int diskGiB,
        EngineSettings engine, string data, IProgress<string> progress, CancellationToken token)
    {
        // Validate user choices before any network or runtime installation.
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}$") || diskGiB < 64 || diskGiB > 2048)
            throw new InvalidDataException(Text.Get("名称须为 1–48 个英文字母、数字、短横线或下划线；容量为 64–2048 GiB。", "Use a 1–48 character name (letters, digits, hyphen, underscore) and 64–2048 GiB capacity."));
        if (!Path.IsPathFullyQualified(destination) || Directory.Exists(destination) || File.Exists(destination))
            throw new IOException(Text.Get("请选择尚不存在的完整目标路径。", "Choose a new absolute destination path."));
        if (!string.IsNullOrWhiteSpace(image) && (!File.Exists(image) || !new[] { ".dmg", ".img" }.Contains(Path.GetExtension(image).ToLowerInvariant())))
            throw new InvalidDataException(Text.Get("请选择现有 BaseSystem.dmg / .img。", "Select an existing BaseSystem.dmg / .img."));
        await EnsureRuntime(engine, data, progress, token);
        var work = Path.Combine(data, "preparation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        // One helper uses the shared download cache at a time, including across CLI/GUI processes.
        using var preparationLock = new FileStream(Path.Combine(data, "preparation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string boot = BootFiles(engine);
        var args = new List<string> { "--json", "opencore", "-o", Path.Combine(work, "OpenCore.img"), "--show-picker", "--text-picker", "--disable-sip" };
        foreach (string kext in new[] { "Graft.kext", "ReimsGraphics.kext", "RgfxGate.kext" })
        { args.Add("--add-kext"); args.Add(Path.Combine(boot, kext) + ",20.0.0"); }
        progress.Report(Text.Get("生成独立 OpenCore 并准备固件…", "Generating independent OpenCore and preparing firmware…"));
        await RunHelper(engine, args, data, progress, token);
        if (string.IsNullOrWhiteSpace(image))
        {
            progress.Report(Text.Get("从 Apple 下载并校验 macOS Tahoe 恢复镜像…", "Downloading and verifying macOS Tahoe recovery from Apple…"));
            var recovery = Path.Combine(data, "recovery"); Directory.CreateDirectory(recovery);
            var result = await RunHelper(engine, ["--json", "online-installer", "tahoe", "-o", recovery], data, progress, token);
            // The helper's verified output is authoritative, not an arbitrary cached DMG.
            image = result.GetProperty("result").GetProperty("image").GetString();
        }
        var ports = FreePorts();
        var request = new RecoveryRequest(name, destination, image!, diskGiB, Path.Combine(work, "OpenCore.img"),
            Path.Combine(work, "OVMF_CODE.fd"), Path.Combine(work, "OVMF_VARS.fd"), Path.Combine(boot, "ReimsGraphics.rom"), ports.Qmp, ports.Ssh);
        var config = await RecoveryService.Create(request, engine, progress, token);
        config.FreePointer = true; config.DisplayMode = "auto"; config.Save();
        foreach (var license in Directory.EnumerateFiles(work, "*License*")) File.Copy(license, Path.Combine(config.DirectoryPath, "firmware", Path.GetFileName(license)));
        return config;
    }

    public static async Task<JsonElement> RunHelper(EngineSettings engine, IEnumerable<string> args, string data, IProgress<string> progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromHours(1)); token = deadline.Token;
        var info = new ProcessStartInfo(Helper(engine)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        info.Environment["XDG_CACHE_HOME"] = Path.Combine(data, "cache");
        info.Environment["PATH"] = engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(info) ?? throw new IOException("Cannot start reims-util.");
        var error = process.StandardError.ReadToEndAsync(token); JsonElement done = default; string? helperError = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
            {
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
                if (e.TryGetProperty("event", out var type) && type.GetString() == "done") done = e.Clone();
                else if (e.TryGetProperty("message", out var message))
                {
                    if (type.GetString() == "error") helperError = message.GetString();
                    progress.Report(message.GetString() ?? "");
                }
                else if (type.GetString() == "progress" && e.TryGetProperty("done", out var bytes))
                    progress.Report(Text.Get("下载 / 校验：", "Download / verification: ") + bytes.GetInt64() / 1048576 + " MiB");
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException("reims-util: " + helperError + " " + await error);
            if (done.ValueKind == JsonValueKind.Undefined) throw new IOException("reims-util returned no completion record.");
            return done;
        }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
    }
}
