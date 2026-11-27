// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Weims.Core;

public sealed record LaunchPlan(List<string> Arguments, Dictionary<string, string> Environment, string Name, string QmpSocket)
{
    public string Preview => string.Join(System.Environment.NewLine, Arguments.Select(a => a.StartsWith("isa-applesmc,osk=") ? "isa-applesmc,osk=<redacted>" : a));
    public const string DefaultOsk = "ourhardworkbythesewordsguardedpleasedontsteal(c)AppleComputerInc";
    public static string ResolveOsk(string? value) => string.IsNullOrEmpty(value) ? DefaultOsk : value;
    public static string Q(string path) => path.Replace('\\', '/').Replace(",", ",,");
    /// <summary>
    /// The command line follows the one upstream's reims-ctl gives an x86-64 guest on Windows (its qemu.rs):
    /// guest RAM is QEMU's shared RAM backend, which the library maps views of, and sound is two virtio
    /// devices with an HDA codec for OpenCore's chime. <paramref name="soundOutput"/> says whether Windows
    /// has an output device; unset, the host is asked.
    /// </summary>
    public static LaunchPlan Build(VmConfig c, EngineSettings engine, string run, string osk, string name, bool? soundOutput = null)
    {
        c.Validate(); engine.Validate();
        osk = ResolveOsk(osk);
        if (osk.Length != 64 || osk.Any(ch => ch <= 32 || ch > 126 || ch == ',')) throw new InvalidDataException(Text.Get("WEIMS_OSK 覆盖值应为 64 个 ASCII 字符，不能包含空格或换行。移除该环境变量即可使用内置默认值。", "WEIMS_OSK must contain 64 ASCII characters without spaces or line breaks. Remove the override to use the built-in default."));
        if (c.FreePointer && !engine.SupportsFreePointer) throw new InvalidOperationException(Text.Get("当前运行组件不支持自由鼠标。请用“一键准备运行环境”安装本项目的运行组件，或关闭此选项。", "This runtime does not support free pointer. Install this project's runtime with One-click setup, or disable the option."));
        // QMP must not listen on TCP: the guest reaches the host's loopback ports through user-mode networking.
        // There is no -no-shutdown: QEMU exits when the guest powers off. With it, QEMU stayed behind as a
        // black window that kept the VM locked and its forwarded port taken until someone closed it.
        var qmpSocket = QmpEndpoint.PathFor(name);
        var a = new List<string> {
            "-L", engine.Bios, "-name", name, "-machine", "q35,memory-backend=ram,max-ram-below-4g=1G",
            "-object", $"memory-backend-ram,id=ram,size={c.MemoryMiB}M,share=on", "-accel", "whpx", "-m", $"{c.MemoryMiB}M",
            "-cpu", c.Cpu, "-smp", $"{c.Cpus},sockets=1,cores={c.Cpus},threads=1",
            "-global", "ICH9-LPC.acpi-pci-hotplug-with-bridge-support=off", "-global", "ICH9-LPC.disable_s3=1", "-global", "ICH9-LPC.disable_s4=1",
            "-device", "pcie-root-port,bus=pcie.0,addr=0x1,slot=1,speed=16,width=32", "-device", "isa-applesmc,osk=" + osk,
            "-drive", $"if=pflash,format=raw,readonly=on,file={Q(c.FirmwareCode)}", "-drive", $"if=pflash,format=raw,file={Q(c.FirmwareVars)}", "-smbios", "type=2",
            "-device", "ich9-ahci,id=sata,addr=0x2",
            "-drive", $"id=opencore,if=none,format=raw,snapshot=on,file={Q(c.OpenCore)}", "-device", "ide-hd,bus=sata.2,drive=opencore,bootindex=1",
            "-drive", $"id=disk,if=none,format=qcow2,file={Q(c.Disk)}", "-device", "ide-hd,bus=sata.3,drive=disk,rotation_rate=1",
            "-netdev", $"user,id=net0,ipv6=off,hostfwd=tcp:127.0.0.1:{c.SshPort}-:22", "-device", $"virtio-net-pci,netdev=net0,id=net0,mac={c.Mac},addr=0x3",
            "-vga", "none", "-device", $"reims-graphics-pci,id=gfx,bus=pcie.0,addr=0x4,romfile={Q(c.Rom)},resolution={c.Resolution}",
            "-device", "qemu-xhci,id=xhci,addr=0x5,conditional-intr-mapping=on,p2=7,p3=7", "-device", "usb-kbd,id=kbd,bus=xhci.0", "-device", "usb-tablet,id=tablet,bus=xhci.0",
            // DirectSound ends QEMU at its start where Windows has no output device. The guest's input
            // records silence: the host's microphone is not given to the guest.
            "-audiodev", (soundOutput ?? SoundOutput() ? "dsound" : "none") + ",id=snd0", "-audiodev", "none,id=sndin",
            "-device", "virtio-sound-pci,id=audio,audiodev=snd0,addr=0x7,streams=1",
            "-device", "virtio-sound-pci,id=audioin,audiodev=sndin,addr=0x8,streams=1,input-streams=1",
            "-device", "ich9-intel-hda,id=hda,addr=0x1b", "-device", "hda-output,bus=hda.0,audiodev=snd0",
            "-display", "none", "-monitor", "none", "-serial", $"file:{Q(Path.Combine(run, "serial.log"))}", "-msg", "timestamp=on",
            "-qmp", $"unix:{Q(qmpSocket)},server=on,wait=off"
        };
        a.AddRange(UsbHost.Arguments(c.UsbDevices));
        if (c.RecoveryAttached) a.AddRange(new[] { "-drive", $"id=recovery,if=none,format=raw,snapshot=on,file={Q(c.Recovery)}", "-device", "ide-hd,bus=sata.4,drive=recovery" });
        var env = new Dictionary<string, string> {
            ["PATH"] = Path.GetDirectoryName(engine.Qemu) + ";" + engine.RuntimeBin + ";" + System.Environment.GetEnvironmentVariable("PATH"),
            ["VK_LOADER_LAYERS_DISABLE"] = "~implicit~", ["REIMS_VGPU_LOG"] = "info", ["REIMS_VGPU_WINDOW_BORDERLESS"] = c.Borderless ? "1" : "0",
            ["REIMS_VGPU_WINDOW_FREE_POINTER"] = c.FreePointer ? "1" : "0"
        };
        foreach (var layer in new[] { "DISABLE_RTSS_LAYER", "DISABLE_VULKAN_OW_OVERLAY_LAYER", "DISABLE_VULKAN_OW_OBS_CAPTURE", "DISABLE_VK_LAYER_VALVE_steam_overlay_1", "DISABLE_VK_LAYER_VALVE_steam_fossilize_1", "DISABLE_VULKAN_OBS_CAPTURE" }) env[layer] = "1";
        return new(a, env, name, qmpSocket);
    }
    public ProcessStartInfo StartInfo(string executable, string workdir)
    {
        var p = new ProcessStartInfo(executable) { WorkingDirectory = workdir, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Arguments) p.ArgumentList.Add(a);
        foreach (var pair in Environment) p.Environment[pair.Key] = pair.Value;
        foreach (var key in new[] { "REIMS_VGPU_WINDOW", "REIMS_WHPX_PF_INTERCEPT", "REIMS_WHPX_MMIO_BQL", "REIMS_WHPX_STATS", "WEIMS_OSK" }) p.Environment.Remove(key);
        return p;
    }
    static bool SoundOutput()
    {
        try { return waveOutGetNumDevs() > 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
}
