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
    string? sshInput;
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
    // No longer acted on: the window's size is the guest's mode, which upstream's window does by itself.
    // New VMs still get "auto" written, the name earlier versions know this behavior by.
    public string DisplayMode { get => ReadText("display_mode", "legacy"); set => Write("display_mode", value); }
    public string Disk { get => ReadText("active_disk"); set => Write("active_disk", value); }
    public string OpenCore { get => ReadText("opencore"); set => Write("opencore", value); }
    public string FirmwareCode { get => ReadText("firmware_code"); set => Write("firmware_code", value); }
    public string FirmwareVars { get => ReadText("firmware_vars"); set => Write("firmware_vars", value); }
    public string Rom { get => ReadText("rom"); set => Write("rom", value); }
    public string Mac { get => ReadText("mac"); set => Write("mac", value); }
    // Upstream's model and flags for an x86-64 macOS guest (reims-ctl's vm.rs and qemu.rs). EarlierCpu is what
    // the scripts of 1.2 and before wrote into configurations for their QEMU; it is read as the default.
    public const string DefaultCpu = "Skylake-Client-noTSX-IBRS,kvm=on,vendor=GenuineIntel,+invtsc,vmware-cpuid-freq=on,+ssse3,+sse4.2,+popcnt,+avx,+aes,+xsave,+xsaveopt,check";
    const string EarlierCpu = "Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce";
    public string Cpu => ReadText("cpu", DefaultCpu) is var cpu && cpu == EarlierCpu ? DefaultCpu : cpu;
    // Legacy key. QMP now uses a Unix domain socket (see QmpEndpoint); the value is only written so that
    // older versions, which validate it, can still open this configuration. It is never listened on.
    public int QmpPort { get => Number("qmp_port", 4444); set => Write("qmp_port", value); }
    public int SshPort { get => Number("ssh_port", 2222); set => Write("ssh_port", value); }
    public string SshPortInput { get => sshInput ?? SshPort.ToString(); set { sshInput = value; if (int.TryParse(value, out int n)) SshPort = n; Changed(); } }
    public string Error => "";
    public string this[string property] => property is nameof(SshPortInput)
        && (!int.TryParse(SshPortInput, out var n) || n < 1024 || n > 65535) ? Text.Get("请输入 1024–65535 的整数端口。", "Enter an integer port between 1024 and 65535.") : "";
    public bool Borderless { get => Data["borderless"]?.GetValue<bool>() ?? false; set => Write("borderless", value); }
    public bool FreePointer { get => Data["free_pointer"]?.GetValue<bool>() ?? false; set => Write("free_pointer", value); }
    public string Recovery { get => ReadText("recovery_image"); set => Write("recovery_image", value); }
    public bool RecoveryAttached { get => Data["recovery_attached"]?.GetValue<bool>() ?? false; set => Write("recovery_attached", value); }
    // Guest Tools. All of these keys are optional: a configuration without them behaves exactly as before,
    // and older versions keep these keys untouched because unknown keys in Data are written back as they are.
    public bool IntegrationEnabled { get => Data["integration_enabled"]?.GetValue<bool>() ?? false; set => Write("integration_enabled", value); }
    public int IntegrationPort { get => Number("integration_port", Integration.DefaultPort(this)); set => Write("integration_port", value); }
    public string ClipboardMode { get => ReadText("clipboard_mode", "text"); set => Write("clipboard_mode", value); }
    /// <summary>
    /// Files copied on one side can be pasted on the other; on by default, and only while the clipboard is shared
    /// at all. A value that is no boolean reads as the default here and is refused by Validate.
    /// </summary>
    public bool ClipboardFiles { get => Data["clipboard_files"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on; set => Write("clipboard_files", value); }
    /// <summary>The Windows folders the guest may mount. Entries that cannot be read are left out here and refused by Validate.</summary>
    public IReadOnlyList<ShareConfig> Shares
    {
        get => Data["shares"] is JsonArray a ? [.. a.Select(ShareConfig.Read).OfType<ShareConfig>()] : [];
        set { Data["shares"] = new JsonArray([.. value.Select(s => (JsonNode)s.ToJson())]); Changed(); }
    }
    /// <summary>
    /// Most the guest may send to this machine, in MiB per second; 0, the default, for no limit. On the runtime
    /// of 1.2 and earlier a fast upload made the guest's picture stutter (a window drag fell from 32 frames per
    /// second to between 5 and 11). On the runtime built from upstream it did not (56 against 58 at 41 MiB/s,
    /// measured 2026-11-25), so nothing is limited unless the user asks for it.
    /// </summary>
    public const int DefaultUploadLimitMiB = 0, MaxUploadLimitMiB = 1024;
    public int UploadLimitMiB { get => Number("upload_limit_mib", DefaultUploadLimitMiB); set => Write("upload_limit_mib", value); }
    /// <summary>Host USB ports passed into the guest at the next start. Absent means none, as before this setting existed.</summary>
    public IReadOnlyList<UsbHost.Selection> UsbDevices
    {
        get => Data["usb_devices"] is JsonArray a ? [.. a.Select(UsbHost.Selection.Read).OfType<UsbHost.Selection>()] : [];
        set { Data["usb_devices"] = new JsonArray([.. value.Select(d => (JsonNode)d.ToJson())]); Changed(); }
    }
    // Which runtime's guest drivers and option ROM the VM's boot files hold (BootImage.SetId), or OwnBootFiles for
    // files the user supplied, which are never replaced. Absent in a VM made by a version before the upstream engine.
    public const string OwnBootFiles = "own";
    public string BootSet { get => ReadText("boot_set"); set => Write("boot_set", value); }

    public void Validate(bool files = true)
    {
        if (this[nameof(SshPortInput)] != "") throw new InvalidDataException(Text.Get("端口输入无效，请返回硬件与网络页面修正。", "Invalid port input. Correct it on the Hardware & Network page."));
        if (Cpus < 2 || Cpus > 64) throw new InvalidDataException(Text.Get("CPU 核心数应在 2–64 之间。", "CPU cores must be between 2 and 64."));
        if (MemoryMiB < 4096 || MemoryMiB > 262144 || MemoryMiB % 1024 != 0) throw new InvalidDataException(Text.Get("内存应为 4096–262144 MiB，且为 1024 的整数倍。", "Memory must be 4096–262144 MiB in multiples of 1024."));
        var m = Regex.Match(Resolution, @"^(\d{3,4})x(\d{3,4})$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int w) || !int.TryParse(m.Groups[2].Value, out int h) || w < 800 || w > 4096 || h < 600 || h > 2160)
            throw new InvalidDataException(Text.Get("分辨率格式为 2560x1440；支持宽 800–4096、高 600–2160。", "Use a resolution such as 2560x1440. Supported width: 800–4096; height: 600–2160."));
        if (DisplayMode is not ("legacy" or "auto" or "fixed"))
            throw new InvalidDataException(Text.Get("显示模式应为 legacy、auto 或 fixed。", "Display mode must be legacy, auto or fixed."));
        if (SshPort < 1024 || SshPort > 65535)
            throw new InvalidDataException(Text.Get("SSH 转发端口应为 1024–65535 之间的整数。", "The SSH forwarding port must be an integer between 1024 and 65535."));
        if (!Integration.InRange(IntegrationPort))
            throw new InvalidDataException(Text.Get($"集成端口应在 {Integration.PortBase}–{Integration.PortBase + Integration.PortCount - 1} 之间。", $"The integration port must be between {Integration.PortBase} and {Integration.PortBase + Integration.PortCount - 1}."));
        if (ClipboardMode is not ("off" or "text"))
            throw new InvalidDataException(Text.Get("剪贴板模式应为 off 或 text。", "Clipboard mode must be off or text."));
        if (Data["clipboard_files"] is { } pasteFiles && (pasteFiles is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new InvalidDataException(Text.Get("clipboard_files 应为 true 或 false。", "clipboard_files must be true or false."));
        if (Data["shares"] is { } shares && (shares is not JsonArray list || Shares.Count != list.Count))
            throw new InvalidDataException(Text.Get("共享文件夹设置无法读取。", "The shared folder settings cannot be read."));
        if (ShareConfig.Problem(Shares) is { } problem) throw new InvalidDataException(problem);
        if (UploadLimitMiB < 0 || UploadLimitMiB > MaxUploadLimitMiB)
            throw new InvalidDataException(Text.Get($"上传限速应为 0–{MaxUploadLimitMiB} MiB/s，0 表示不限。", $"The upload limit must be 0–{MaxUploadLimitMiB} MiB/s; 0 means no limit."));
        if (Data["usb_devices"] is { } usb && (usb is not JsonArray usbList || UsbDevices.Count != usbList.Count))
            throw new InvalidDataException(Text.Get("USB 设备设置无法读取。", "The USB device settings cannot be read."));
        if (UsbHost.Problem(UsbDevices) is { } usbProblem) throw new InvalidDataException(usbProblem);
        if (!Regex.IsMatch(Mac, @"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")) throw new InvalidDataException(Text.Get("MAC 地址格式错误。", "Invalid MAC address format."));
        foreach (var path in new[] { Disk, OpenCore, FirmwareCode, FirmwareVars, Rom }.Concat(RecoveryAttached ? new[] { Recovery } : Array.Empty<string>()))
        {
            if (!Path.IsPathFullyQualified(path) || path.Any(c => c < 32)) throw new InvalidDataException(Text.Get("文件路径必须是完整的本地路径。", "File paths must be absolute local paths."));
            if (files && !File.Exists(path)) throw new FileNotFoundException(Text.Get("缺少文件：", "Missing file: ") + path);
        }
        if (Cpu.Any(c => c < 32)) throw new InvalidDataException(Text.Get("CPU 配置无效。", "Invalid CPU configuration."));
    }
    public void Save() { Validate(); JsonFiles.Write(ConfigPath, Data); }
    /// <summary>
    /// Writes just the given keys into the stored configuration and leaves the rest of the file as it is.
    /// For settings that may change while the VM runs: edits to other fields that the user has not
    /// saved yet must not slip into the file along with them.
    /// </summary>
    public void SaveOnly(params string[] keys)
    {
        var stored = Load(ConfigPath);
        foreach (var key in keys) { if (Data[key] is { } value) stored.Data[key] = value.DeepClone(); else stored.Data.Remove(key); }
        stored.Validate(false);
        JsonFiles.Write(ConfigPath, stored.Data);
    }
}

public sealed class EngineSettings : Observable
{
    string qemu = "", runtimeBin = "", bios = "", powerShell = "pwsh.exe";
    public string PowerShell { get => powerShell; set => Set(ref powerShell, value); }
    public string Qemu { get => qemu; set => Set(ref qemu, value); }
    public string RuntimeBin { get => runtimeBin; set => Set(ref runtimeBin, value); }
    public string Bios { get => bios; set => Set(ref bios, value); }
    public string ImageTool => Path.Combine(Path.GetDirectoryName(Qemu) ?? "", "qemu-img.exe");
    public bool SupportsFreePointer => Has("free-pointer");
    /// <summary>
    /// Whether weims-runtime.json beside QEMU, the record of a runtime this project built (scripts/build.ps1),
    /// names the capability: something the build adds to upstream's. The library is linked into QEMU, so the
    /// record names QEMU's size, and one beside another QEMU counts for nothing. An upstream build of the
    /// user's own has no record; it runs VMs with upstream's behavior alone.
    /// </summary>
    bool Has(string capability)
    {
        try
        {
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Qemu)!, "weims-runtime.json")))!;
            return record["format"]?.GetValue<int>() == 1 && record["qemu_bytes"]?.GetValue<long>() == new FileInfo(Qemu).Length &&
                record["capabilities"] is JsonArray list && list.Any(item => item?.GetValue<string>() == capability);
        } catch { return false; }
    }
    public void Validate()
    {
        // reims-vgpu-compile.exe is the library's helper process; the library starts it from beside QEMU.
        foreach (var p in new[] { Qemu, ImageTool, Path.Combine(Path.GetDirectoryName(Qemu) ?? "", "reims-vgpu-compile.exe") })
            if (!File.Exists(p)) throw new FileNotFoundException(Text.Get("缺少运行组件：", "Missing runtime component: ") + p);
        if (!Directory.Exists(RuntimeBin) || !Directory.Exists(Bios)) throw new InvalidDataException(Text.Get("请选择运行库目录和 QEMU 固件目录。", "Select the runtime dependency directory and QEMU firmware directory."));
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
