// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weims.Core;

public sealed record RecoveryRequest(string Name, string Destination, string Image, int DiskGiB, string OpenCore, string Code, string Vars, string Rom, int QmpPort, int SshPort);

public static class RecoveryService
{
    public static void Validate(RecoveryRequest r)
    {
        if (!Regex.IsMatch(r.Name, @"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}$")) throw new InvalidDataException(Text.Get("虚拟机名称使用 1–48 个英文字母、数字、下划线或短横线。", "VM names must use 1–48 letters, numbers, underscores or hyphens."));
        if (r.DiskGiB < 64 || r.DiskGiB > 2048) throw new InvalidDataException(Text.Get("系统盘容量应为 64–2048 GiB。", "System disk size must be 64–2048 GiB."));
        if (!new[] { ".dmg", ".img" }.Contains(Path.GetExtension(r.Image).ToLowerInvariant())) throw new InvalidDataException(Text.Get("只支持恢复镜像 .dmg / .img，不支持完整 ISO。", "Only .dmg / .img recovery images are supported, not full ISO images."));
        if (!Path.IsPathFullyQualified(r.Destination)) throw new InvalidDataException(Text.Get("请选择完整的虚拟机存放路径。", "Choose an absolute path for VM storage."));
        if (Directory.Exists(r.Destination) || File.Exists(r.Destination)) throw new IOException(Text.Get("目标目录已存在。请选择一个新目录，不会覆盖已有文件。", "The destination already exists. Choose a new directory; existing files will not be overwritten."));
        foreach (var p in new[] { r.Image, r.OpenCore, r.Code, r.Vars, r.Rom }) if (!File.Exists(p)) throw new FileNotFoundException(Text.Get("缺少文件：", "Missing file: ") + p);
        if (r.QmpPort < 1024 || r.QmpPort > 65535 || r.SshPort < 1024 || r.SshPort > 65535 || r.QmpPort == r.SshPort) throw new InvalidDataException(Text.Get("端口无效或重复。", "Invalid or duplicate ports."));
    }
    public static async Task<VmConfig> Create(RecoveryRequest r, EngineSettings engine, IProgress<string> progress, CancellationToken token)
    {
        Validate(r); engine.Validate();
        var env = new Dictionary<string,string> { ["PATH"] = engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH") };
        var format = Path.GetExtension(r.Image).Equals(".dmg", StringComparison.OrdinalIgnoreCase) ? "dmg" : "raw";
        progress.Report(Text.Get("检查恢复镜像格式与所需空间…", "Checking recovery format and available space…"));
        var info = JsonNode.Parse(await ProcessRunner.Run(engine.ImageTool, new[] { "info", "--output=json", "-f", format, r.Image }, env, token))!;
        long size = info["virtual-size"]!.GetValue<long>();
        if (size < 128 * 1024 * 1024L || size > 32L * 1024 * 1024 * 1024) throw new InvalidDataException(Text.Get("恢复镜像展开大小不在 128 MiB–32 GiB 范围，请检查是否选择了 BaseSystem 恢复镜像。", "Expanded recovery size must be 128 MiB–32 GiB. Check that you selected a BaseSystem recovery image."));
        var drive = new DriveInfo(Path.GetPathRoot(r.Destination)!);
        if (drive.AvailableFreeSpace < size + 2L * 1024 * 1024 * 1024) throw new IOException(Text.Get("空间不足：需要存放展开的恢复镜像并至少留出 2 GiB。系统安装还需额外空间。", "Not enough space for the expanded recovery image plus 2 GiB. System installation requires additional space."));
        // Prepare under an unpredictable sibling name; publish with non-overwriting Directory.Move.
        // A competing creator can never make conversion overwrite files in the requested destination.
        string finalDestination = r.Destination;
        r = r with { Destination = finalDestination + ".preparing-" + Guid.NewGuid().ToString("N") };
        Directory.CreateDirectory(r.Destination);
        try
        {
            Directory.CreateDirectory(Path.Combine(r.Destination, "firmware")); Directory.CreateDirectory(Path.Combine(r.Destination, "disks"));
            var recovery = Path.Combine(r.Destination, "disks", "BaseSystem.img");
            var partial = recovery + ".partial";
            progress.Report(Text.Get("展开恢复镜像，窗口保持响应；可取消…", "Expanding recovery image. You can cancel while this runs…"));
            await ProcessRunner.Run(engine.ImageTool, new[] { "convert", "-f", format, "-O", "raw", r.Image, partial }, env, token);
            ValidateBootImage(partial);
            File.Move(partial, recovery);
            progress.Report(Text.Get("创建稀疏系统盘与独立固件副本…", "Creating a sparse system disk and independent firmware copies…"));
            var disk = Path.Combine(r.Destination, "disks", "current.qcow2");
            if (File.Exists(disk)) throw new IOException(Text.Get("目标磁盘已存在。", "The destination disk already exists."));
            await ProcessRunner.Run(engine.ImageTool, new[] { "create", "-f", "qcow2", disk, r.DiskGiB + "G" }, env, token);
            var c = new VmConfig(Path.Combine(r.Destination, "state", "config.json"), new JsonObject());
            c.Data["schema"] = 1; c.Data["lane"] = "reims-graphics-pci"; c.Data["display_name"] = r.Name; c.Data["disk_gib"] = r.DiskGiB;
            c.Cpus = 8; c.MemoryMiB = 8192; c.Resolution = "1920x1080";
            c.Mac = "52:54:00:" + string.Join(":", RandomNumberGenerator.GetBytes(3).Select(b => b.ToString("x2")));
            c.Disk = disk; c.Rom = r.Rom; c.QmpPort = r.QmpPort; c.SshPort = r.SshPort;
            c.OpenCore = Copy(r.OpenCore, "OpenCore.img"); c.FirmwareCode = Copy(r.Code, "OVMF_CODE.fd"); c.FirmwareVars = Copy(r.Vars, "OVMF_VARS.fd");
            c.Recovery = recovery; c.RecoveryAttached = true;
            c.Validate();
            token.ThrowIfCancellationRequested();
            Directory.Move(r.Destination, finalDestination);
            c = new VmConfig(Path.Combine(finalDestination, "state", "config.json"), c.Data);
            c.Disk = Path.Combine(finalDestination, "disks", "current.qcow2");
            c.OpenCore = Path.Combine(finalDestination, "firmware", "OpenCore.img");
            c.FirmwareCode = Path.Combine(finalDestination, "firmware", "OVMF_CODE.fd");
            c.FirmwareVars = Path.Combine(finalDestination, "firmware", "OVMF_VARS.fd");
            c.Recovery = Path.Combine(finalDestination, "disks", "BaseSystem.img");
            c.Save();
            return c;
        }
        catch
        {
            var incomplete = Directory.Exists(r.Destination) ? r.Destination : finalDestination;
            File.WriteAllText(Path.Combine(incomplete, "PREPARATION-INCOMPLETE.txt"), "Preparation did not complete. No source files were modified. Inspect this folder before removing it or retrying with a new destination.");
            throw;
        }
        string Copy(string source, string name)
        {
            var dest = Path.Combine(r.Destination, "firmware", name);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output); return dest;
        }
    }
    public static void ValidateBootImage(string path)
    {
        using var input = File.OpenRead(path); var header = new byte[4096]; int count = input.Read(header);
        bool mbr = count > 512 && header[510] == 0x55 && header[511] == 0xAA;
        bool appleMap = count > 512 && header[0] == 'E' && header[1] == 'R';
        bool hfs = count > 1026 && header[1024] == 'H' && (header[1025] == '+' || header[1025] == 'X');
        bool apfs = count > 36 && System.Text.Encoding.ASCII.GetString(header, 32, 4) == "NXSB";
        if (!mbr && !appleMap && !hfs && !apfs) throw new InvalidDataException(Text.Get("镜像中未识别到分区表或 macOS 文件系统头。请选择有效的 BaseSystem 恢复镜像。", "No partition table or macOS filesystem header found. Choose a valid BaseSystem recovery image."));
    }
}
