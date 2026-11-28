// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weims.Core;

/// <summary>
/// USB devices of this PC handed to the guest through QEMU's usb-host, on the xHCI controller the VM
/// already has. A chosen device is remembered by its vendor id and product id. An iPhone changes product
/// id between 12A8 and 12AB when it reconnects, so those two ids are one device and the guest matches the
/// vendor only. Listing reads Plug and Play and never opens a device.
///
/// Windows keeps its own driver until that driver is replaced. <see cref="Handoff"/> binds the inbox
/// WinUSB driver for the selection, puts the previous driver back when a device is unchecked, and does
/// the same when QEMU exits, including after Manager itself has quit. Keyboard, mouse and Bluetooth
/// devices are left out of the list.
/// </summary>
public static class UsbHost
{
    public const int Limit = 8;
    /// <summary>Apple's vendor id. An iPhone uses 12A8 or 12AB; other Apple devices keep their own product id.</summary>
    public const int AppleVendor = 0x05AC;
    public static bool Iphone(int vendor, int product) => vendor == AppleVendor && product is 0x12A8 or 0x12AB;
    public sealed record Selection(int Bus, string Port, string Label, int Vendor = 0, int Product = 0, int Slot = -1)
    {
        public static Selection? Read(JsonNode? node)
        {
            if (node is not JsonObject o || o["bus"] is not JsonValue bus || !bus.TryGetValue<int>(out var number) || o["port"] is not JsonValue port || !port.TryGetValue<string>(out var path)) return null;
            var label = o["label"] is JsonValue text && text.TryGetValue<string>(out var name) ? name : "";
            int vendor = o["vendor"] is JsonValue vendorValue && vendorValue.TryGetValue<int>(out var vendorNumber) ? vendorNumber : 0;
            int product = o["product"] is JsonValue productValue && productValue.TryGetValue<int>(out var productNumber) ? productNumber : 0;
            int slot = o["slot"] is JsonValue slotValue && slotValue.TryGetValue<int>(out var slotNumber) ? slotNumber : -1;
            var selection = new Selection(number, path, label, vendor, product, slot);
            return Problem([selection]) == null ? selection : null;
        }
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["bus"] = Bus, ["port"] = Port, ["label"] = Label };
            if (Vendor != 0) { o["vendor"] = Vendor; o["product"] = Product; }
            if (Slot >= 0) o["slot"] = Slot;
            return o;
        }
        /// <summary>The QEMU device id. A remembered slot survives adding and removing other devices while the VM runs.</summary>
        public string QemuId(int index) => $"usbhost{(Slot >= 0 ? Slot : index)}";
        public string Device(int index)
        {
            var id = $"usb-host,id={QemuId(index)},bus=xhci.0";
            if (Vendor is < 1 or > 0xffff) return id + $",hostbus={Bus},hostport={Port}";
            id += $",vendorid=0x{Vendor:x4}";
            if (!Iphone(Vendor, Product) && Product is >= 1 and <= 0xffff) id += $",productid=0x{Product:x4}";
            return id;
        }
        public JsonObject Plug(int index)
        {
            var o = new JsonObject { ["driver"] = "usb-host", ["id"] = QemuId(index), ["bus"] = "xhci.0" };
            if (Vendor is >= 1 and <= 0xffff)
            {
                o["vendorid"] = Vendor;
                if (!Iphone(Vendor, Product) && Product is >= 1 and <= 0xffff) o["productid"] = Product;
            }
            else { o["hostbus"] = Bus; o["hostport"] = Port; }
            return o;
        }
        public bool Matches(Selection other) => Vendor != 0 && other.Vendor != 0
            ? Vendor == other.Vendor && (Iphone(Vendor, Product) || Iphone(other.Vendor, other.Product) ? Iphone(Vendor, Product) && Iphone(other.Vendor, other.Product) : Product == other.Product)
            : Bus == other.Bus && Port == other.Port;
        /// <summary>One claim. An iPhone covers both of its product ids; anything else is one product.</summary>
        public string Rule() => Iphone(Vendor, Product) ? $"{Vendor:X4} *" : $"{Vendor:X4} {Product:X4}";
    }

    public sealed record Seen(int Bus, string Port, int Vendor, int Product, string Name);
    public sealed record ScanResult(IReadOnlyList<Seen> Devices, string? Problem);

    public static string? Problem(IReadOnlyList<Selection> devices)
    {
        if (devices.Count > Limit) return Text.Get($"最多把 {Limit} 个 USB 设备传进虚拟机。", $"At most {Limit} USB devices can be passed into the VM.");
        var ports = new HashSet<string>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new HashSet<int>();
        foreach (var device in devices)
        {
            if (Problem(device.Bus, device.Port, device.Label) is { } problem) return problem;
            if (device.Vendor is < 0 or > 0xffff || device.Product is < 0 or > 0xffff || (device.Vendor == 0 && device.Product != 0))
                return Text.Get("USB 设备编号无效。", "The USB device id is not valid.");
            if (device.Slot is < -1 or >= Limit) return Text.Get("USB 设备槽位无效。", "The USB device slot is not valid.");
            if (device.Slot >= 0 && !slots.Add(device.Slot)) return Text.Get("两个 USB 设备用了同一个槽位。", "Two USB devices use the same slot.");
            if (!ports.Add(device.Bus + ":" + device.Port)) return Text.Get($"USB 插口 {device.Bus}/{device.Port} 选了两次。", $"USB port {device.Bus}/{device.Port} is selected twice.");
            if (device.Vendor != 0 && !identities.Add(device.Rule())) return Text.Get("两个要传入的 USB 设备是同一台。", "Two USB devices selected for the VM are the same device.");
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

    /// <summary>Gives every device a free QEMU slot, keeping slots that were already handed out.</summary>
    public static List<Selection> AssignSlots(IReadOnlyList<Selection> devices)
    {
        var used = devices.Where(d => d.Slot >= 0).Select(d => d.Slot).ToHashSet();
        var assigned = new List<Selection>(devices.Count);
        foreach (var device in devices)
        {
            if (device.Slot >= 0) { assigned.Add(device); continue; }
            var slot = 0;
            while (used.Contains(slot)) slot++;
            used.Add(slot);
            assigned.Add(device with { Slot = slot });
        }
        return assigned;
    }

    /// <summary>
    /// Fills in the vendor id from the devices plugged in now. A saved iPhone whose port has changed is
    /// recognized by its two product ids when it is the only iPhone present.
    /// </summary>
    public static List<Selection> Resolve(IReadOnlyList<Selection> selected, IReadOnlyList<Seen> present)
    {
        var resolved = new List<Selection>(selected.Count);
        foreach (var device in selected)
        {
            Seen? found = null;
            if (device.Vendor != 0)
                found = present.FirstOrDefault(d => d.Vendor == device.Vendor && (Iphone(device.Vendor, device.Product) ? Iphone(d.Vendor, d.Product) : d.Product == device.Product));
            else
            {
                var atPort = present.FirstOrDefault(d => d.Bus == device.Bus && d.Port == device.Port);
                // A saved iPhone must not follow a port that another device has taken over.
                if (atPort != null && (!AppleName(device.Label) || Iphone(atPort.Vendor, atPort.Product))) found = atPort;
                else if (AppleName(device.Label) && present.Count(d => Iphone(d.Vendor, d.Product)) == 1) found = present.First(d => Iphone(d.Vendor, d.Product));
            }
            resolved.Add(found == null ? device : device with { Bus = found.Bus, Port = found.Port, Vendor = found.Vendor, Product = found.Product });
        }
        return resolved;
    }

    static bool AppleName(string label) => label.Contains("Apple", StringComparison.OrdinalIgnoreCase) || label.Contains("iPhone", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An elevated process that binds WinUSB for one VM and restores the previous drivers when QEMU exits.
    /// </summary>
    public sealed class Handoff : IDisposable
    {
        readonly Process? process;
        readonly string directory;
        bool handed, disposed;
        Handoff(Process? process, string directory) { this.process = process; this.directory = directory; }

        public static async Task<Handoff?> Open(EngineSettings engine, IReadOnlyList<Selection> devices, string runDirectory)
        {
            var rules = devices.Where(d => d.Vendor is >= 1 and <= 0xffff).Select(d => d.Rule()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rules.Count == 0) return null;
            var script = Path.Combine(AppContext.BaseDirectory, "usb-claim.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException(Text.Get("缺少 usb-claim.ps1，无法把 USB 设备交给虚拟机。", "usb-claim.ps1 is missing, so the USB device cannot be given to the VM."), script);
            WriteRules(runDirectory, rules);
            WriteText(Path.Combine(runDirectory, "usb-rules.gen"), "1");
            foreach (var name in new[] { "usb-ready.txt", "usb-error.txt", "usb-rules.ack", "usb-pid.txt" })
            {
                var path = Path.Combine(runDirectory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            var info = new ProcessStartInfo(engine.PowerShell)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Directory \"{runDirectory}\""
            };
            Process? process;
            try { process = Process.Start(info); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (e is System.ComponentModel.Win32Exception win && win.NativeErrorCode == 1223)
                    throw new IOException(Text.Get("没有获得管理员权限，USB 设备仍留在 Windows。", "Administrator permission was not granted, so the USB device stayed with Windows."), e);
                throw new IOException(Text.Get("无法启动 USB 交接：", "Could not start the USB handoff: ") + e.Message, e);
            }
            var ready = Path.Combine(runDirectory, "usb-ready.txt");
            var error = Path.Combine(runDirectory, "usb-error.txt");
            var deadline = DateTime.UtcNow.AddSeconds(120);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(error)) throw new IOException(Text.Get("Windows 没有放开 USB 设备：", "Windows did not release the USB device: ") + File.ReadAllText(error).Trim());
                    if (File.Exists(ready)) return new Handoff(process, runDirectory);
                    var exited = false;
                    try { exited = process != null && process.HasExited; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    if (exited) throw new IOException(Text.Get("USB 交接提前退出了。详见 usb-claim.log。", "The USB handoff exited early. See usb-claim.log."));
                    await Task.Delay(100);
                }
                throw new IOException(Text.Get("USB 交接没有就绪。详见 usb-claim.log。", "The USB handoff did not become ready. See usb-claim.log."));
            }
            catch
            {
                Signal(runDirectory, "0");
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
                process?.Dispose();
                throw;
            }
        }

        public void Attach(int qemuPid)
        {
            File.WriteAllText(Path.Combine(directory, "usb-pid.txt"), qemuPid.ToString(), new UTF8Encoding(false));
            handed = true;
        }

        /// <summary>
        /// Makes the hide rules match <paramref name="devices"/>. Starts the holder when the VM is already
        /// running and nothing is holding the rules yet. An empty list clears them, so Windows can bind again.
        /// </summary>
        public static async Task Sync(EngineSettings engine, string runDirectory, int qemuPid, IReadOnlyList<Selection> devices)
        {
            if (HolderAlive(runDirectory))
            {
                WriteRules(runDirectory, devices.Where(d => d.Vendor is >= 1 and <= 0xffff).Select(d => d.Rule()).Distinct(StringComparer.OrdinalIgnoreCase));
                var generation = DateTime.UtcNow.Ticks.ToString();
                var error = Path.Combine(runDirectory, "usb-error.txt");
                if (File.Exists(error)) File.Delete(error);
                WriteText(Path.Combine(runDirectory, "usb-rules.gen"), generation);
                await WaitAck(runDirectory, generation);
                return;
            }
            var handoff = await Open(engine, devices, runDirectory);
            if (handoff == null) return;
            handoff.Attach(qemuPid);
            handoff.Dispose();
        }

        static void WriteRules(string directory, IEnumerable<string> rules)
        {
            var path = Path.Combine(directory, "usb-rules.txt");
            var temp = path + ".tmp";
            File.WriteAllLines(temp, rules, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        static void WriteText(string path, string value)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, value, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        static async Task WaitAck(string directory, string generation)
        {
            var ack = Path.Combine(directory, "usb-rules.ack");
            var error = Path.Combine(directory, "usb-error.txt");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(ack) && File.ReadAllText(ack).Trim() == generation) return;
                if (File.Exists(error)) throw new IOException(Text.Get("Windows 没有放开 USB 设备：", "Windows did not release the USB device: ") + File.ReadAllText(error).Trim());
                await Task.Delay(100);
            }
            throw new IOException(Text.Get("USB 设备列表没有更新。详见 usb-claim.log。", "The USB device list was not updated. See usb-claim.log."));
        }

        static bool HolderAlive(string directory)
        {
            var path = Path.Combine(directory, "usb-holder.txt");
            if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path).Trim(), out var pid) || pid <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return false;
                var name = process.ProcessName;
                return name.Contains("pwsh", StringComparison.OrdinalIgnoreCase) || name.Contains("powershell", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!handed) Signal(directory, "0");
            process?.Dispose();
        }

        static void Signal(string directory, string value)
        {
            try { File.WriteAllText(Path.Combine(directory, "usb-pid.txt"), value, new UTF8Encoding(false)); }
            catch (IOException) { }
        }
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
        _ = engine;
        if (!OperatingSystem.IsWindows()) return new([], Text.Get("只能在 Windows 上列出 USB 设备。", "USB devices can only be listed on Windows."));
        try { return List(); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return new([], Text.Get("无法列出 USB 设备：", "Could not list USB devices: ") + e.Message);
        }
    }

    static string? Problem(int bus, string port, string label)
    {
        if (bus is < 1 or > 255) return Text.Get("USB 总线号应在 1–255。", "The USB bus number must be from 1 to 255.");
        if (!Regex.IsMatch(port, @"^[1-9][0-9]{0,2}(\.[1-9][0-9]{0,2}){0,6}$")) return Text.Get($"USB 端口“{port}”无效。", $"The USB port \"{port}\" is not valid.");
        if (label.Length > 80 || label.Any(c => c < 32)) return Text.Get("USB 设备名称无效。", "The USB device name is not valid.");
        return null;
    }

    sealed record Row(int Vendor, int Product, string Class, string Service, string Name, string? Path, bool Interface);

    static ScanResult List()
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new([], Text.Get("无法列出 USB 设备。", "Could not list USB devices."));
        var rows = new List<Row>();
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var hardware = Property(set, ref info, 1) ?? "";
                var match = Regex.Match(hardware, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (!match.Success) continue;
                int vendor = Convert.ToInt32(match.Groups[1].Value, 16), product = Convert.ToInt32(match.Groups[2].Value, 16);
                if (vendor == 0) continue;
                var name = Property(set, ref info, 12) ?? Property(set, ref info, 0) ?? $"USB {vendor:X4}:{product:X4}";
                rows.Add(new Row(vendor, product, Property(set, ref info, 7) ?? "", Property(set, ref info, 4) ?? "", name, Location(set, ref info), hardware.Contains("&MI_", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        var children = rows.Where(row => row.Interface).ToLookup(row => (row.Vendor, row.Product), row => row.Class);
        var found = new List<Seen>();
        foreach (var row in rows)
        {
            if (row.Interface || Blocked(row, children[(row.Vendor, row.Product)])) continue;
            if (row.Path == null || !TryPort(row.Path, out var bus, out var port)) continue;
            found.Add(new Seen(bus, port, row.Vendor, row.Product, Label(row.Name)));
        }
        found.Sort((a, b) => a.Bus != b.Bus ? a.Bus.CompareTo(b.Bus) : string.CompareOrdinal(a.Port, b.Port));
        return new(found, null);
    }

    static bool Blocked(Row row, IEnumerable<string> children)
    {
        if (row.Service.Contains("hub", StringComparison.OrdinalIgnoreCase)) return true;
        if (row.Class is "Keyboard" or "Mouse" or "Bluetooth" or "HIDClass") return true;
        var kids = children.ToList();
        if (kids.Any(c => c is "Keyboard" or "Mouse" or "Bluetooth")) return true;
        return kids.Count > 0 && kids.All(c => c == "HIDClass");
    }

    static bool TryPort(string path, out int bus, out string port)
    {
        bus = 0;
        port = "";
        var root = path.IndexOf("#USBROOT", StringComparison.OrdinalIgnoreCase);
        if (root < 0) return false;
        var numbers = Regex.Matches(path[root..], @"#USB\((\d+)\)", RegexOptions.IgnoreCase);
        var pieces = numbers.Select(m => int.Parse(m.Groups[1].Value)).Where(n => n is >= 1 and <= 255).ToArray();
        if (pieces.Length == 0) return false;
        port = string.Join('.', pieces);
        uint hash = 2166136261;
        foreach (var c in path[..root]) hash = (hash ^ c) * 16777619;
        bus = (int)(hash % 255) + 1;
        return Problem(bus, port, "USB") == null;
    }

    static string? Location(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 37 };
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out _, buffer, (uint)buffer.Length, out _, 0)) return null;
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(path => path.Contains("#USB(", StringComparison.OrdinalIgnoreCase));
    }

    static string? Property(IntPtr set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)buffer.Length, out _)) return null;
        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint needed, uint flags);
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

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }
}
