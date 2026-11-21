// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;

namespace Weims.Core;

/// <summary>
/// The Guest Tools install disk: a small read-only USB stick that Manager or the CLI plugs into a
/// running VM, as VMware Tools does. One prebuilt image ships with Manager and is the same for
/// every VM. What is specific to a VM, its pairing ticket, travels as the stick's serial number,
/// which the installer in the guest reads without needing any permission; so installing from the
/// stick pairs the helper without anything being typed.
///
/// The broker never talks to QMP; plugging and unplugging is done here, by whoever polls the VM.
/// </summary>
public static class InstallMedia
{
    public const string ImageOverride = "WEIMS_GUEST_TOOLS_IMAGE";
    public const string Node = "weims-gt", Device = "weims-gt-dev";

    public static string ImagePath()
    {
        var custom = Environment.GetEnvironmentVariable(ImageOverride);
        return string.IsNullOrWhiteSpace(custom) ? Path.Combine(AppContext.BaseDirectory, "guest-tools", "weims-guest-tools.img") : Path.GetFullPath(custom);
    }
    public static bool Available => File.Exists(ImagePath());
    /// <summary>The VM's own copy, so that a running VM never keeps the shipped file open while Manager is updated.</summary>
    public static string PreparedPath(VmConfig c) => Path.Combine(Integration.StateDirectory(c), "guest-tools-media.img");

    /// <summary>
    /// Plugs a raw image into the VM as a read-only, removable USB stick with the given serial number.
    /// macOS mounts it within seconds. QEMU allows this kind of stick a serial number of at most 20 characters.
    /// </summary>
    public static async Task Plug(VmConfig c, string image, string serial, CancellationToken token = default)
    {
        if (serial.Length is 0 or > 20 || serial.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException(Text.Get("安装盘的序列号无效。", "The install disk's serial number is not valid."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var qmp = await VmService.OpenQmp(c, timeout.Token);
        await qmp.Execute("blockdev-add", new JsonObject
        {
            ["driver"] = "raw", ["node-name"] = Node, ["read-only"] = true,
            ["file"] = new JsonObject { ["driver"] = "file", ["filename"] = Path.GetFullPath(image).Replace('\\', '/'), ["read-only"] = true }
        }, timeout.Token);
        try { await qmp.Execute("device_add", new JsonObject { ["driver"] = "usb-storage", ["id"] = Device, ["bus"] = "xhci.0", ["drive"] = Node, ["removable"] = true, ["serial"] = serial }, timeout.Token); }
        catch (IOException)
        {
            // Do not leave the image open in QEMU when the stick could not be created.
            try { await qmp.Execute("blockdev-del", new JsonObject { ["node-name"] = Node }, timeout.Token); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Unplugs the stick and lets go of the image file. Safe to call when nothing is plugged in.
    /// The device goes away a moment after QEMU is asked, so releasing the image is retried briefly.
    /// </summary>
    public static async Task Unplug(VmConfig c, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var qmp = await VmService.OpenQmp(c, timeout.Token);
        try { await qmp.Execute("device_del", new JsonObject { ["id"] = Device }, timeout.Token); }
        catch (IOException) { /* not plugged in */ }
        for (int i = 0; i < 20; i++)
        {
            try { await qmp.Execute("blockdev-del", new JsonObject { ["node-name"] = Node }, timeout.Token); return; }
            catch (IOException e) when (e.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)) { await Task.Delay(200, timeout.Token); }
            catch (IOException) { return; } // no such node: nothing was plugged in
        }
        throw new IOException(Text.Get("安装盘仍被虚拟机占用。", "The install image is still in use by the VM."));
    }

    /// <summary>
    /// "Install Guest Tools": gets a pairing ticket from this VM's broker and plugs a copy of the image in
    /// with the ticket as its serial number. A stick that is already plugged in is replaced.
    /// </summary>
    public static async Task Insert(VmConfig c, CancellationToken token = default)
    {
        var image = ImagePath();
        if (!File.Exists(image)) throw new FileNotFoundException(Text.Get("缺少 Guest Tools 安装映像：", "Missing Guest Tools install image: ") + image);
        var answer = await Integration.Command(c, "install-ticket", token);
        try
        {
            var serial = answer["ticket"]?["serial"] is JsonValue v && v.TryGetValue<string>(out var given) ? given
                : throw new IOException(Text.Get("集成组件应答无效。", "Invalid answer from the integration component."));
            await Unplug(c, token);
            File.Copy(image, PreparedPath(c), overwrite: true);
            await Plug(c, PreparedPath(c), serial, token);
        }
        catch
        {
            try { await Integration.Command(c, "install-cancel", CancellationToken.None); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Called by whoever polls the VM. Once the helper has paired and unmounted the volume in the guest,
    /// the stick is unplugged and the VM's copy of the image is deleted. Returns true when it did so.
    /// </summary>
    public static async Task<bool> Collect(VmConfig c, BrokerState state, CancellationToken token = default)
    {
        if (!state.Online || !state.InstallMediaEjected) return false;
        try
        {
            await Unplug(c, token);
            await Integration.Command(c, "install-media-removed", token);
            try { File.Delete(PreparedPath(c)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Net.Sockets.SocketException) { return false; } // tried again at the next poll
    }
}
