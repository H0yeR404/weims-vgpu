// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weims.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}

public sealed class VmConfig : Observable, IDataErrorInfo
{
    string? qmpInput, sshInput;
    public JsonObject Data { get; }
    public string ConfigPath { get; }
    public string DirectoryPath => Path.GetDirectoryName(Path.GetDirectoryName(ConfigPath))!;
    public string RuntimePath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "runtime.json");
    public string Name => Data["display_name"]?.GetValue<string>() ?? Path.GetFileName(DirectoryPath);
    public void RefreshLanguage() => Changed(nameof(Summary));
    public string Summary => Text.Get($"{Cpus} 核 · {MemoryMiB / 1024} GiB · {Resolution}", $"{Cpus} cores · {MemoryMiB / 1024} GiB · {Resolution}");
    public VmConfig(string path, JsonObject data) { ConfigPath = Path.GetFullPath(path); Data = data; }
    public static VmConfig Load(string path) => new(path, JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException(Text.Get("配置为空。", "The configuration is empty.")));
    string ReadText(string key, string fallback = "") => Data[key]?.GetValue<string>() ?? fallback;
    int Number(string key, int fallback) => Data[key]?.GetValue<int>() ?? fallback;
    void Write<T>(string key, T value, [CallerMemberName] string? property = null) { Data[key] = JsonSerializer.SerializeToNode(value); Changed(property); Changed(nameof(Summary)); }
    public int Cpus { get => Number("cpus", 8); set => Write("cpus", value); }
    public int MemoryMiB { get => Number("memory_mib", 8192); set => Write("memory_mib", value); }
    public string Resolution { get => ReadText("resolution", "1920x1080"); set => Write("resolution", value); }
    public string DisplayMode { get => ReadText("display_mode", "legacy"); set => Write("display_mode", value); }
    public string Disk { get => ReadText("active_disk"); set => Write("active_disk", value); }
    public string OpenCore { get => ReadText("opencore"); set => Write("opencore", value); }
    public string FirmwareCode { get => ReadText("firmware_code"); set => Write("firmware_code", value); }
    public string FirmwareVars { get => ReadText("firmware_vars"); set => Write("firmware_vars", value); }
    public string Rom { get => ReadText("rom"); set => Write("rom", value); }
    public string Mac { get => ReadText("mac"); set => Write("mac", value); }
    public string Cpu => ReadText("cpu", "Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce");
    public int QmpPort { get => Number("qmp_port", 4444); set => Write("qmp_port", value); }
    public int SshPort { get => Number("ssh_port", 2222); set => Write("ssh_port", value); }
    public string QmpPortInput { get => qmpInput ?? QmpPort.ToString(); set { qmpInput = value; if (int.TryParse(value, out int n)) QmpPort = n; Changed(); } }
    public string SshPortInput { get => sshInput ?? SshPort.ToString(); set { sshInput = value; if (int.TryParse(value, out int n)) SshPort = n; Changed(); } }
    public string Error => "";
    public string this[string property] => property is nameof(QmpPortInput) or nameof(SshPortInput)
        && (!int.TryParse(property == nameof(QmpPortInput) ? QmpPortInput : SshPortInput, out var n) || n < 1024 || n > 65535) ? Text.Get("请输入 1024–65535 的整数端口。", "Enter an integer port between 1024 and 65535.") : "";
    public bool Borderless { get => Data["borderless"]?.GetValue<bool>() ?? false; set => Write("borderless", value); }
    public bool FreePointer { get => Data["free_pointer"]?.GetValue<bool>() ?? false; set => Write("free_pointer", value); }
    public string Recovery { get => ReadText("recovery_image"); set => Write("recovery_image", value); }
    public bool RecoveryAttached { get => Data["recovery_attached"]?.GetValue<bool>() ?? false; set => Write("recovery_attached", value); }

    public void Validate(bool files = true)
    {
        if (this[nameof(QmpPortInput)] != "" || this[nameof(SshPortInput)] != "") throw new InvalidDataException(Text.Get("端口输入无效，请返回硬件与网络页面修正。", "Invalid port input. Correct it on the Hardware & Network page."));
        if (Cpus < 2 || Cpus > 64) throw new InvalidDataException(Text.Get("CPU 核心数应在 2–64 之间。", "CPU cores must be between 2 and 64."));
        if (MemoryMiB < 4096 || MemoryMiB > 262144 || MemoryMiB % 1024 != 0) throw new InvalidDataException(Text.Get("内存应为 4096–262144 MiB，且为 1024 的整数倍。", "Memory must be 4096–262144 MiB in multiples of 1024."));
        var m = Regex.Match(Resolution, @"^(\d{3,4})x(\d{3,4})$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int w) || !int.TryParse(m.Groups[2].Value, out int h) || w < 800 || w > 4096 || h < 600 || h > 2160)
            throw new InvalidDataException(Text.Get("分辨率格式为 2560x1440；支持宽 800–4096、高 600–2160。", "Use a resolution such as 2560x1440. Supported width: 800–4096; height: 600–2160."));
        if (DisplayMode is not ("legacy" or "auto" or "fixed"))
            throw new InvalidDataException(Text.Get("显示模式应为 legacy、auto 或 fixed。", "Display mode must be legacy, auto or fixed."));
        if (QmpPort < 1024 || QmpPort > 65535 || SshPort < 1024 || SshPort > 65535 || QmpPort == SshPort)
            throw new InvalidDataException(Text.Get("QMP 与 SSH 端口应为 1024–65535，且不能相同。", "QMP and SSH ports must be distinct integers between 1024 and 65535."));
        if (!Regex.IsMatch(Mac, @"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")) throw new InvalidDataException(Text.Get("MAC 地址格式错误。", "Invalid MAC address format."));
        foreach (var path in new[] { Disk, OpenCore, FirmwareCode, FirmwareVars, Rom }.Concat(RecoveryAttached ? new[] { Recovery } : Array.Empty<string>()))
        {
            if (!Path.IsPathFullyQualified(path) || path.Any(c => c < 32)) throw new InvalidDataException(Text.Get("文件路径必须是完整的本地路径。", "File paths must be absolute local paths."));
            if (files && !File.Exists(path)) throw new FileNotFoundException(Text.Get("缺少文件：", "Missing file: ") + path);
        }
        if (Cpu.Any(c => c < 32)) throw new InvalidDataException(Text.Get("CPU 配置无效。", "Invalid CPU configuration."));
    }
    public void Save() { Validate(); JsonFiles.Write(ConfigPath, Data); }
}

public sealed class EngineSettings : Observable
{
    string qemu = "", runtimeBin = "", bios = "", powerShell = "pwsh.exe";
    public string PowerShell { get => powerShell; set => Set(ref powerShell, value); }
    public string Qemu { get => qemu; set => Set(ref qemu, value); }
    public string RuntimeBin { get => runtimeBin; set => Set(ref runtimeBin, value); }
    public string Bios { get => bios; set => Set(ref bios, value); }
    public string ImageTool => Path.Combine(Path.GetDirectoryName(Qemu) ?? "", "qemu-img.exe");
    public bool SupportsFreePointer => File.Exists(Path.Combine(Path.GetDirectoryName(Qemu) ?? "", "weims-input-v1.json")) &&
        InputMarkerValid();
    public bool SupportsDisplayPolicy => MarkerValid("weims-display-v1.json", "adaptive-display-v1");
    public void ValidateDisplay(VmConfig c)
    {
        if (c.DisplayMode != "legacy" && !SupportsDisplayPolicy)
            throw new InvalidOperationException(Text.Get("自动适应与固定分辨率需要显示扩展运行包。请选用该运行包，或切回兼容模式。", "Auto fit and fixed resolution require the display extension runtime. Select that runtime or use Compatibility mode."));
    }
    bool MarkerValid(string file, string capability)
    {
        try
        {
            var directory = Path.GetDirectoryName(Qemu)!;
            var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, file)))!;
            using var dll = File.OpenRead(Path.Combine(directory, "reims_vgpu_c.dll"));
            return marker["capability"]?.GetValue<string>() == capability &&
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll)).Equals(marker["dll_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }
    bool InputMarkerValid()
    {
        try
        {
            var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Qemu)!, "weims-input-v1.json")))!;
            using var dll = File.OpenRead(Path.Combine(Path.GetDirectoryName(Qemu)!, "reims_vgpu_c.dll"));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll)).Equals(marker["dll_sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }
    public void Validate()
    {
        foreach (var p in new[] { Qemu, ImageTool, Path.Combine(Path.GetDirectoryName(Qemu) ?? "", "reims_vgpu_c.dll") })
            if (!File.Exists(p)) throw new FileNotFoundException(Text.Get("缺少运行组件：", "Missing runtime component: ") + p);
        if (!Directory.Exists(RuntimeBin) || !Directory.Exists(Bios)) throw new InvalidDataException(Text.Get("请选择 MSYS2 UCRT64 bin 和 QEMU pc-bios 目录。", "Select the runtime dependency directory and QEMU pc-bios directory."));
    }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        try { File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
