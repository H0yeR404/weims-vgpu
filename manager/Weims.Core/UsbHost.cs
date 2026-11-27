// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Weims.Core;

/// <summary>
/// USB devices of this PC handed to the guest through QEMU's usb-host, on the xHCI controller the VM
/// already has. A device is remembered by the physical port (bus and port path), not by its product id:
/// an iPhone changes product id when it re-enumerates, and the port does not. QEMU attaches whatever is
/// in that port when the VM starts, and again if it is plugged in later, so a missing device does not
/// stop the VM from starting.
///
/// Windows only gives QEMU the device when the UsbDk driver is installed. Listing uses the same libusb
/// UsbDk backend QEMU does, and never opens a device, so looking at the list does not take it from Windows.
/// </summary>
public static class UsbHost
{
    public const int Limit = 8;
    public sealed record Selection(int Bus, string Port, string Label)
    {
        public static Selection? Read(JsonNode? node)
        {
            if (node is not JsonObject o || o["bus"] is not JsonValue bus || !bus.TryGetValue<int>(out var number) || o["port"] is not JsonValue port || !port.TryGetValue<string>(out var path)) return null;
            var label = o["label"] is JsonValue text && text.TryGetValue<string>(out var name) ? name : "";
            return Problem(number, path, label) == null ? new Selection(number, path, label) : null;
        }
        public JsonObject ToJson() => new() { ["bus"] = Bus, ["port"] = Port, ["label"] = Label };
        public string Device(int index) => $"usb-host,id=usbhost{index},bus=xhci.0,hostbus={Bus},hostport={Port}";
    }

    public sealed record Seen(int Bus, string Port, int Vendor, int Product, string Name);
    public sealed record ScanResult(IReadOnlyList<Seen> Devices, string? Problem);

    public static string? Problem(IReadOnlyList<Selection> devices)
    {
        if (devices.Count > Limit) return Text.Get($"最多把 {Limit} 个 USB 设备传进虚拟机。", $"At most {Limit} USB devices can be passed into the VM.");
        var ports = new HashSet<string>();
        foreach (var device in devices)
        {
            if (Problem(device.Bus, device.Port, device.Label) is { } problem) return problem;
            if (!ports.Add(device.Bus + ":" + device.Port)) return Text.Get($"USB 插口 {device.Bus}/{device.Port} 选了两次。", $"USB port {device.Bus}/{device.Port} is selected twice.");
        }
        return null;
    }

    /// <summary>A name safe to store: no control characters, at most 80, never empty.</summary>
    public static string Label(string name)
    {
        var text = new string(name.Where(c => c >= 32).ToArray()).Trim();
        if (text.Length == 0) text = "USB";
        return text.Length > 80 ? text[..80] : text;
    }

    public static List<string> Arguments(IReadOnlyList<Selection> devices)
    {
        var args = new List<string>(devices.Count * 2);
        for (var i = 0; i < devices.Count; i++) { args.Add("-device"); args.Add(devices[i].Device(i)); }
        return args;
    }

    /// <summary>Fails when this QEMU was built without usb-host. A device that is not plugged in is not an error.</summary>
    public static async Task RequireSupport(EngineSettings engine, CancellationToken token)
    {
        var env = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(engine.Qemu) + ";" + engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH") };
        try { await ProcessRunner.Run(engine.Qemu, ["-device", "usb-host,help"], env, token); }
        catch (IOException e) when (e.Message.Contains("usb-host", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Text.Get("当前运行组件不能把 USB 设备传进虚拟机（没有 usb-host）。取消勾选这些设备后可以启动。", "This runtime cannot pass USB devices into the VM (it has no usb-host). Clear the selection and the VM can start."), e);
        }
    }

    public static ScanResult Scan(EngineSettings engine)
    {
        if (!OperatingSystem.IsWindows()) return new([], Text.Get("只能在 Windows 上列出 USB 设备。", "USB devices can only be listed on Windows."));
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UsbDk");
        if (service == null) return new([], Text.Get("需要先安装 UsbDk，QEMU 才能从 Windows 接手 USB 设备：https://github.com/daynix/UsbDk/releases", "Install UsbDk first, so QEMU can take a USB device over from Windows: https://github.com/daynix/UsbDk/releases"));
        var dll = LibraryPath(engine);
        if (dll == null) return new([], Text.Get("运行组件目录里没有 libusb-1.0.dll，无法列出 USB 设备。", "libusb-1.0.dll is not in the runtime directory, so USB devices cannot be listed."));
        try { lock (scan) return List(dll, Names()); }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return new([], Text.Get("无法加载 libusb：", "Could not load libusb: ") + e.Message);
        }
    }

    static readonly object scan = new();

    static string? Problem(int bus, string port, string label)
    {
        if (bus is < 1 or > 255) return Text.Get("USB 总线号应在 1–255。", "The USB bus number must be from 1 to 255.");
        if (!Regex.IsMatch(port, @"^[1-9][0-9]{0,2}(\.[1-9][0-9]{0,2}){0,6}$")) return Text.Get($"USB 端口“{port}”无效。", $"The USB port \"{port}\" is not valid.");
        if (label.Length > 80 || label.Any(c => c < 32)) return Text.Get("USB 设备名称无效。", "The USB device name is not valid.");
        return null;
    }

    static string? LibraryPath(EngineSettings engine)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(engine.Qemu), engine.RuntimeBin })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var name in new[] { "libusb-1.0.dll", "libusb-1.0-0.dll" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    static ScanResult List(string dll, IReadOnlyDictionary<(int Vendor, int Product), string> names)
    {
        var native = Native.Load(dll);
        // QEMU sets this before libusb_init. The same backend is what makes the bus and port numbers match.
        if (native.SetOption(IntPtr.Zero, Native.UseUsbDk) != 0)
            return new([], Text.Get("这份 libusb 不支持 UsbDk。", "This libusb was built without UsbDk support."));
        if (native.Init(out var ctx) != 0 || ctx == IntPtr.Zero)
            return new([], Text.Get("UsbDk 没有就绪。确认它已安装后点刷新。", "UsbDk is not ready. Install it and click Refresh."));
        IntPtr list = IntPtr.Zero;
        try
        {
            var count = native.DeviceList(ctx, out list);
            if (count < 0) return new([], Text.Get($"列出 USB 设备失败（libusb {count}）。", $"Listing USB devices failed (libusb {count})."));
            var found = new List<Seen>();
            var desc = Marshal.AllocHGlobal(32);
            var ports = new byte[8];
            try
            {
                for (nint i = 0; i < count; i++)
                {
                    var dev = Marshal.ReadIntPtr(list, (int)(i * IntPtr.Size));
                    if (native.Descriptor(dev, desc) != 0) continue;
                    if (Marshal.ReadByte(desc, 4) == 9) continue; // hub
                    int vendor = U16(desc, 8), product = U16(desc, 10);
                    int bus = native.Bus(dev);
                    if (vendor == 0 || bus is < 1 or > 255) continue;
                    int depth = native.Ports(dev, ports, ports.Length);
                    if (depth is < 1 or > 7 || ports.Take(depth).Any(p => p == 0)) continue;
                    var port = string.Join('.', ports.Take(depth));
                    var name = Label(names.TryGetValue((vendor, product), out var known) ? known : $"USB {vendor:X4}:{product:X4}");
                    found.Add(new Seen(bus, port, vendor, product, name));
                }
            }
            finally { Marshal.FreeHGlobal(desc); }
            found.Sort((a, b) => a.Bus != b.Bus ? a.Bus.CompareTo(b.Bus) : string.CompareOrdinal(a.Port, b.Port));
            return new(found, null);
        }
        finally
        {
            if (list != IntPtr.Zero) native.FreeList(list, 1);
            native.Exit(ctx);
        }
    }

    static int U16(IntPtr desc, int offset) => Marshal.ReadByte(desc, offset) | (Marshal.ReadByte(desc, offset + 1) << 8);

    static Dictionary<(int, int), string> Names()
    {
        var names = new Dictionary<(int, int), string>();
        var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04); // present, all classes
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return names;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var id = Property(set, ref info, 1); // hardware id, VID_xxxx&PID_xxxx
                var match = Regex.Match(id ?? "", @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!match.Success) continue;
                var key = (Convert.ToInt32(match.Groups[1].Value, 16), Convert.ToInt32(match.Groups[2].Value, 16));
                if (names.ContainsKey(key)) continue;
                var name = Property(set, ref info, 12) ?? Property(set, ref info, 0); // friendly name, else description
                if (!string.IsNullOrWhiteSpace(name)) names[key] = name.Trim();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return names;
    }

    static string? Property(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)buffer.Length, out _)) return null;
        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    sealed class Native
    {
        public const int UseUsbDk = 1;
        public readonly InitDelegate Init;
        public readonly SetOptionDelegate SetOption;
        public readonly ExitDelegate Exit;
        public readonly DeviceListDelegate DeviceList;
        public readonly FreeListDelegate FreeList;
        public readonly DescriptorDelegate Descriptor;
        public readonly BusDelegate Bus;
        public readonly PortsDelegate Ports;
        Native(IntPtr module)
        {
            Init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(NativeLibrary.GetExport(module, "libusb_init"));
            SetOption = Marshal.GetDelegateForFunctionPointer<SetOptionDelegate>(NativeLibrary.GetExport(module, "libusb_set_option"));
            Exit = Marshal.GetDelegateForFunctionPointer<ExitDelegate>(NativeLibrary.GetExport(module, "libusb_exit"));
            DeviceList = Marshal.GetDelegateForFunctionPointer<DeviceListDelegate>(NativeLibrary.GetExport(module, "libusb_get_device_list"));
            FreeList = Marshal.GetDelegateForFunctionPointer<FreeListDelegate>(NativeLibrary.GetExport(module, "libusb_free_device_list"));
            Descriptor = Marshal.GetDelegateForFunctionPointer<DescriptorDelegate>(NativeLibrary.GetExport(module, "libusb_get_device_descriptor"));
            Bus = Marshal.GetDelegateForFunctionPointer<BusDelegate>(NativeLibrary.GetExport(module, "libusb_get_bus_number"));
            Ports = Marshal.GetDelegateForFunctionPointer<PortsDelegate>(NativeLibrary.GetExport(module, "libusb_get_port_numbers"));
        }
        static Native? loaded;
        static string? loadedFrom;
        public static Native Load(string dll)
        {
            if (loaded != null && loadedFrom == dll) return loaded;
            loaded = new Native(NativeLibrary.Load(dll));
            loadedFrom = dll;
            return loaded;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int InitDelegate(out IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetOptionDelegate(IntPtr ctx, int option);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ExitDelegate(IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint DeviceListDelegate(IntPtr ctx, out IntPtr list);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FreeListDelegate(IntPtr list, int unref);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DescriptorDelegate(IntPtr dev, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate byte BusDelegate(IntPtr dev);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int PortsDelegate(IntPtr dev, byte[] ports, int len);
    }
}
