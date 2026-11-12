// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Weims.Core;

public sealed record LaunchPlan(List<string> Arguments, Dictionary<string, string> Environment, string Name)
{
    public string Preview => string.Join(System.Environment.NewLine, Arguments.Select(a => a.StartsWith("isa-applesmc,osk=") ? "isa-applesmc,osk=<redacted>" : a));
    public const string DefaultOsk = "ourhardworkbythesewordsguardedpleasedontsteal(c)AppleComputerInc";
    public static string ResolveOsk(string? value) => string.IsNullOrEmpty(value) ? DefaultOsk : value;
    public static string Q(string path) => path.Replace('\\', '/').Replace(",", ",,");
    public static LaunchPlan Build(VmConfig c, EngineSettings engine, string run, string osk, string name)
    {
        c.Validate(); engine.Validate();
        osk = ResolveOsk(osk);
        if (osk.Length != 64 || osk.Any(ch => ch <= 32 || ch > 126 || ch == ',')) throw new InvalidDataException(Text.Get("WEIMS_OSK 覆盖值应为 64 个 ASCII 字符，不能包含空格或换行。移除该环境变量即可使用内置默认值。", "WEIMS_OSK must contain 64 ASCII characters without spaces or line breaks. Remove the override to use the built-in default."));
        if (c.FreePointer && !engine.SupportsFreePointer) throw new InvalidOperationException(Text.Get("当前显示后端不支持自由鼠标。请安装输入扩展版或关闭此选项。", "This display backend does not support free pointer. Install the input extension or disable this option."));
        var a = new List<string> {
            "-L", engine.Bios, "-name", name, "-machine", "q35,max-ram-below-4g=1G", "-accel", "whpx,hyperv=off",
            "-cpu", c.Cpu, "-smp", $"{c.Cpus},sockets=1,cores={c.Cpus},threads=1", "-m", $"{c.MemoryMiB}M",
            "-global", "ICH9-LPC.acpi-pci-hotplug-with-bridge-support=off", "-global", "ICH9-LPC.disable_s3=1", "-global", "ICH9-LPC.disable_s4=1",
            "-device", "pcie-root-port,bus=pcie.0,addr=0x1,slot=1,x-speed=16,x-width=32", "-device", "isa-applesmc,osk=" + osk,
            "-drive", $"if=pflash,format=raw,readonly=on,file={Q(c.FirmwareCode)}", "-drive", $"if=pflash,format=raw,file={Q(c.FirmwareVars)}", "-smbios", "type=2",
            "-device", "ich9-ahci,id=sata,addr=0x2",
            "-drive", $"id=opencore,if=none,format=raw,snapshot=on,file={Q(c.OpenCore)}", "-device", "ide-hd,bus=sata.2,drive=opencore,bootindex=1",
            "-drive", $"id=disk,if=none,format=qcow2,file={Q(c.Disk)}", "-device", "ide-hd,bus=sata.3,drive=disk,rotation_rate=1",
            "-netdev", $"user,id=net0,ipv6=off,hostfwd=tcp:127.0.0.1:{c.SshPort}-:22", "-device", $"virtio-net-pci,netdev=net0,id=net0,mac={c.Mac},addr=0x3",
            "-vga", "none", "-device", $"reims-graphics-pci,id=gfx,bus=pcie.0,addr=0x4,romfile={Q(c.Rom)},resolution={c.Resolution},guest-memory=on",
            "-device", "qemu-xhci,id=xhci,addr=0x5,p2=8,p3=6,conditional-intr-mapping=on", "-device", "usb-kbd,id=kbd,bus=xhci.0", "-device", "usb-tablet,id=tablet,bus=xhci.0",
            "-audiodev", "none,id=snd0,in.voices=0", "-device", "usb-audio,id=audio,audiodev=snd0,bus=xhci.0",
            "-display", "none", "-monitor", "none", "-serial", $"file:{Q(Path.Combine(run, "serial.log"))}", "-msg", "timestamp=on",
            "-qmp", $"tcp:127.0.0.1:{c.QmpPort},server=on,wait=off", "-no-shutdown"
        };
        if (c.RecoveryAttached) a.AddRange(new[] { "-drive", $"id=recovery,if=none,format=raw,readonly=on,file={Q(c.Recovery)}", "-device", "ide-hd,bus=sata.4,drive=recovery" });
        var env = new Dictionary<string, string> {
            ["PATH"] = Path.GetDirectoryName(engine.Qemu) + ";" + engine.RuntimeBin + ";" + System.Environment.GetEnvironmentVariable("PATH"),
            ["VK_LOADER_LAYERS_DISABLE"] = "~implicit~", ["REIMS_VGPU_LOG"] = "info", ["REIMS_VGPU_WINDOW_BORDERLESS"] = c.Borderless ? "1" : "0",
            ["REIMS_VGPU_WINDOW_FREE_POINTER"] = c.FreePointer ? "1" : "0"
        };
        foreach (var layer in new[] { "DISABLE_RTSS_LAYER", "DISABLE_VULKAN_OW_OVERLAY_LAYER", "DISABLE_VULKAN_OW_OBS_CAPTURE", "DISABLE_VK_LAYER_VALVE_steam_overlay_1", "DISABLE_VK_LAYER_VALVE_steam_fossilize_1", "DISABLE_VULKAN_OBS_CAPTURE" }) env[layer] = "1";
        return new(a, env, name);
    }
    public ProcessStartInfo StartInfo(string executable, string workdir)
    {
        var p = new ProcessStartInfo(executable) { WorkingDirectory = workdir, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Arguments) p.ArgumentList.Add(a);
        foreach (var pair in Environment) p.Environment[pair.Key] = pair.Value;
        foreach (var key in new[] { "REIMS_VGPU_WINDOW", "REIMS_WHPX_PF_INTERCEPT", "REIMS_WHPX_MMIO_BQL", "REIMS_WHPX_STATS", "WEIMS_OSK" }) p.Environment.Remove(key);
        return p;
    }
}
