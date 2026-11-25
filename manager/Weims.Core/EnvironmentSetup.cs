// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Weims.Core;

/// <summary>Required: VMs cannot start on a managed runtime of another version (this Manager's command line does not fit it).</summary>
public sealed record SetupPackage(string Version, string Url, string Sha256, long MaxBytes, bool Required = false);
/// <summary>The runtime runs, but is one of 1.2.0-pre.1 or earlier, which this version's command line does not fit.</summary>
public sealed class RuntimeIncompatibleException(string message) : IOException(message);
public sealed record SetupCatalog(SetupPackage Runtime, SetupPackage PowerShell);
public sealed record EnvironmentReport(bool SupportedHost, bool HypervisorReady, bool VulkanReady, bool RuntimeReady, bool PowerShellReady, string Details, bool RuntimeOutdated = false, bool RuntimeUpdateRequired = false);

public static class EnvironmentSetup
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };
    public static SetupCatalog Catalog() => JsonSerializer.Deserialize<SetupCatalog>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime-catalog.json"))) ?? throw new InvalidDataException("Invalid runtime catalog.");

    // A runtime that Manager installed lives in <data>\runtimes\<catalog version>-<32 hex digits>. It is outdated when this
    // Manager's catalog names another version. A runtime the user chose elsewhere is theirs and is never called outdated.
    public static bool RuntimeOutdated(EngineSettings engine, string dataDirectory, string catalogVersion)
    {
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(engine.Qemu));
            if (folder == null || !string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(Path.Combine(dataDirectory, "runtimes")), StringComparison.OrdinalIgnoreCase)) return false;
            var name = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(folder), "^(.+)-[0-9a-f]{32}$");
            return name.Success && name.Groups[1].Value != catalogVersion;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool RuntimeOutdated(EngineSettings engine, string dataDirectory)
    {
        try { return RuntimeOutdated(engine, dataDirectory, Catalog().Runtime.Version); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { return false; } // a build from source may carry no catalog
    }

    /// <summary>
    /// Whether VMs must not be started before the runtime is replaced: the installed one is a managed runtime of
    /// another version, and the catalog says its own is required.
    /// </summary>
    public static bool RuntimeUpdateRequired(EngineSettings engine, string dataDirectory)
    {
        try { return RuntimeUpdateRequired(engine, dataDirectory, Catalog().Runtime); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { return false; }
    }
    public static bool RuntimeUpdateRequired(EngineSettings engine, string dataDirectory, SetupPackage catalogRuntime) =>
        catalogRuntime.Required && RuntimeOutdated(engine, dataDirectory, catalogRuntime.Version);
    /// <summary>Whether the runtime in use is one this version cannot start VMs with, wherever it is installed.</summary>
    public static async Task<bool> RuntimeIncompatible(EngineSettings engine, CancellationToken token)
    {
        try { await ValidateRuntime(engine, token); return false; }
        catch (RuntimeIncompatibleException) { return true; }
        catch (Exception e) when (e is not OperationCanceledException) { return false; } // missing or broken: setup's usual path
    }
    public static string RuntimeUpdateRequiredText() => Text.Get(
        "这个版本的管理器必须使用新的运行组件，现在安装的是旧的，虚拟机无法启动。请先更新运行组件。",
        "This version of Manager needs its newer runtime. The installed one is older and VMs cannot start on it. Update the runtime first.");

    public static async Task<EnvironmentReport> Detect(EngineSettings engine, CancellationToken token, string? dataDirectory = null)
    {
        bool host = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && RuntimeInformation.OSArchitecture == Architecture.X64;
        var lines = new List<string> { $"Windows: {Environment.OSVersion.Version}; {RuntimeInformation.OSArchitecture}" };
        bool whpx = false, vulkan = false, runtime = false, ps = false, outdated = false, required = false, incompatible = false;
        try { whpx = WHvGetCapability(0, out int present, 4, out _) == 0 && present != 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        lines.Add(Text.Get("固件虚拟化：", "Firmware virtualization: ") + (IsProcessorFeaturePresent(21) || whpx ? "OK" : Text.Get("请在 BIOS/UEFI 启用，或检查嵌套虚拟化。", "Enable in BIOS/UEFI, or check nested virtualization.")));
        lines.Add("WHPX: " + (whpx ? "OK" : Text.Get("未就绪；在 Windows 功能中启用 Windows 虚拟机监控程序平台，然后重启。", "Not ready; enable Windows Hypervisor Platform in Windows Features and restart.")));
        try { vulkan = VulkanProbe.Check(out string detail); lines.Add("Vulkan: " + detail); }
        catch (Exception e) { lines.Add("Vulkan: " + e.Message); }
        try { await ValidateRuntime(engine, token); runtime = true; lines.Add("QEMU / Reims: OK"); }
        catch (Exception e) when (e is not OperationCanceledException) { lines.Add("QEMU / Reims: " + e.Message); incompatible = e is RuntimeIncompatibleException; }
        // Asked whether the runtime passed or not: the one of 1.2.0-pre.1 and earlier does not pass, and is the one to replace.
        if (dataDirectory != null) { outdated = RuntimeOutdated(engine, dataDirectory); required = RuntimeUpdateRequired(engine, dataDirectory); }
        // A runtime the user chose is never called outdated, but one that cannot work has to go all the same.
        if (incompatible) outdated = required = true;
        if (required)
            lines.Add(Text.Get("运行组件：", "Runtime: ") + RuntimeUpdateRequiredText());
        else if (outdated)
            lines.Add(Text.Get("运行组件：此版本配套的运行组件比已安装的新，点击“一键准备运行环境”即可更换。", "Runtime: this version comes with a newer runtime than the installed one. Click One-click setup to switch to it."));
        try { await CheckPowerShell(engine.PowerShell, token); ps = true; lines.Add("PowerShell 7+: OK"); }
        catch (Exception e) when (e is not OperationCanceledException) { lines.Add("PowerShell: " + e.Message); }
        if (!host) lines.Add(Text.Get("此版本需要 Windows 10 2004 或更新的 x64 系统。", "This release requires Windows 10 2004 or newer on x64."));
        lines.Add(Text.Get("恢复镜像、OpenCore、OVMF 和 ROM 在导入或创建虚拟机时单独检查。", "Recovery, OpenCore, OVMF and ROM files are checked when importing or creating a VM."));
        return new(host, whpx, vulkan, runtime, ps, string.Join(Environment.NewLine, lines), outdated, required);
    }

    public static async Task CheckPowerShell(string executable, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await ProcessRunner.Run(executable, ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"], token: timeout.Token);
        if (!int.TryParse(result.Trim(), out int major) || major < 7) throw new IOException("PowerShell 7+ required.");
    }

    static string IncompatibleText() => Text.Get("此运行组件属于 1.2.0-pre.1 及更早版本，与当前版本不兼容。", "This runtime belongs to version 1.2.0-pre.1 or earlier and does not work with this version.");
    public static async Task ValidateRuntime(EngineSettings engine, CancellationToken token)
    {
        // The runtime of 1.2.0-pre.1 and earlier: its graphics library is a DLL beside QEMU, and it has no helper process.
        // Named for what it is, before the check for the helper calls it incomplete.
        string? beside = string.IsNullOrWhiteSpace(engine.Qemu) ? null : Path.GetDirectoryName(engine.Qemu);
        if (beside != null && File.Exists(engine.Qemu) && File.Exists(Path.Combine(beside, "reims_vgpu_c.dll")) && !File.Exists(Path.Combine(beside, "reims-vgpu-compile.exe")))
            throw new RuntimeIncompatibleException(IncompatibleText());
        engine.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var env = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(engine.Qemu) + ";" + engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH") };
        await ProcessRunner.Run(engine.Qemu, ["--version"], env, timeout.Token);
        await ProcessRunner.Run(engine.ImageTool, ["--version"], env, timeout.Token);
        var device = await ProcessRunner.Run(engine.Qemu, ["-device", "reims-graphics-pci,help"], env, timeout.Token);
        var accel = await ProcessRunner.Run(engine.Qemu, ["-accel", "help"], env, timeout.Token);
        // The QEMU of 1.2 and before had a guest-memory switch on the device and took another command line.
        if (device.Contains("guest-memory"))
            throw new RuntimeIncompatibleException(IncompatibleText());
        if (!accel.Contains("whpx")) throw new IOException(Text.Get("运行组件缺少 WHPX。", "Runtime lacks WHPX."));
    }

    // Reuse a verified custom or managed runtime; never download again just because the user clicks setup.
    // The exception, only where the caller asks for it (the setup button, not a VM start, which must keep working offline):
    // a managed runtime of another version than this Manager's catalog names (RuntimeOutdated). The catalog's is
    // installed beside it, and the settings change only once that one is verified.
    public static async Task<EngineSettings> Ensure(EngineSettings current, string dataDirectory, IProgress<string> progress, CancellationToken token, bool replaceOutdated = false)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            progress.Report(Text.Get("正在检查现有运行组件…", "Checking existing runtime components…"));
            await ValidateRuntime(current, token);
            await CheckPowerShell(current.PowerShell, token);
            if (!replaceOutdated || !RuntimeOutdated(current, dataDirectory)) return current;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return await Prepare(dataDirectory, progress, token);
        }
        // The PowerShell in use was checked just now and stays.
        return await Prepare(dataDirectory, progress, token, current.PowerShell);
    }

    public static async Task<EngineSettings> Prepare(string dataDirectory, IProgress<string> progress, CancellationToken token, string? powerShell = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(Text.Get("自动准备仅支持 Windows x64。", "Automatic setup requires Windows x64."));
        Directory.CreateDirectory(dataDirectory);
        using var setupLock = new FileStream(Path.Combine(dataDirectory, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var catalog = Catalog();
        string root = Path.Combine(dataDirectory, "runtimes"); Directory.CreateDirectory(root);
        string ps = powerShell ?? "pwsh.exe";
        try { await CheckPowerShell(ps, token); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            string psDir = await InstallPackage(catalog.PowerShell, root, progress, token);
            ps = Path.Combine(psDir, "pwsh.exe"); await CheckPowerShell(ps, token);
        }
        string dir = await InstallPackage(catalog.Runtime, root, progress, token);
        var engine = new EngineSettings { Qemu = Path.Combine(dir, "qemu-system-x86_64.exe"), RuntimeBin = dir, Bios = Path.Combine(dir, "pc-bios"), PowerShell = ps };
        progress.Report(Text.Get("正在验证运行组件…", "Validating runtime components…"));
        await ValidateRuntime(engine, token); token.ThrowIfCancellationRequested();
        return engine;
    }

    static string DownloadFailed(SetupPackage package, string reason) => Text.Get(
        $"无法下载 {package.Version}：{reason}。原有设置没有改动。请检查网络后重试；下载地址：{package.Url}",
        $"Could not download {package.Version}: {reason}. Your settings are unchanged. Check the connection and try again. Download address: {package.Url}");

    public static async Task<string> InstallPackage(SetupPackage package, string root, IProgress<string> progress, CancellationToken token, HttpClient? transport = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(20)); token = deadline.Token;
        if (!System.Text.RegularExpressions.Regex.IsMatch(package.Version, @"^[a-zA-Z0-9._-]+$") || package.Version is "." or "..") throw new InvalidDataException("Invalid package version.");
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com") throw new InvalidDataException("Package must use HTTPS on github.com.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(package.Sha256, "^[a-fA-F0-9]{64}$") || package.MaxBytes <= 0 || package.MaxBytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid package hash or size.");
        // Unique installs allow a repair without ever overwriting an in-use runtime.
        string stage = Path.Combine(root, ".setup-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, package.Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string zip = Path.Combine(stage, "download.zip"), unpack = Path.Combine(stage, "content");
            progress.Report(Text.Get("下载 ", "Downloading ") + package.Version);
            HttpResponseMessage response;
            try { response = await (transport ?? Http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException e) { throw new IOException(DownloadFailed(package, e.Message), e); }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
                throw new IOException(DownloadFailed(package, response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? Text.Get("发布页上没有这个文件（404）。这个版本可能还没有发布，或文件已被移走", "the file is not on the release page (404). This version may not be published yet, or the file was moved")
                    : Text.Get("服务器返回 ", "the server answered ") + (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > package.MaxBytes) throw new IOException("Package exceeds download limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0, last = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += read; if (total > package.MaxBytes) throw new IOException("Package exceeds download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (total - last > 2 * 1024 * 1024) { last = total; progress.Report(package.Version + $": {total / 1048576} MiB"); }
                }
            }
            progress.Report(Text.Get("校验与解压 ", "Verifying and extracting ") + package.Version);
            await Task.Run(() => ExtractVerified(zip, unpack, package.Sha256, token), token);
            token.ThrowIfCancellationRequested(); Directory.Move(unpack, target); return target;
        }
        finally { try { Directory.Delete(stage, true); } catch (IOException) { } }
    }

    public static void ExtractVerified(string zip, string destination, string sha256, CancellationToken token)
    {
        using (var stream = File.OpenRead(zip))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Text.Get("下载校验失败，请重试。", "Download checksum mismatch. Please retry."));
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Extraction destination already exists.");
        using var archive = ZipFile.OpenRead(zip);
        string prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (name.Contains(':') || name.Split('/').Any(p => p is ".." || p.EndsWith(' ') || p.EndsWith('.')) || Path.IsPathRooted(name) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Unsafe archive entry.");
            string path = Path.GetFullPath(Path.Combine(destination, name));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes destination.");
            total = checked(total + entry.Length);
            if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive exceeds extraction limit.");
        }
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested(); string path = Path.Combine(destination, entry.FullName.Replace('\\', '/'));
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; int read; while ((read = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
        }
    }

    [DllImport("WinHvPlatform.dll")] static extern int WHvGetCapability(int code, out int value, uint size, out uint written);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsProcessorFeaturePresent(uint feature);
}
