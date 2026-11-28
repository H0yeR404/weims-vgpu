// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Weims.Core;

/// <summary>The Mac an OpenCore image presents (config.plist, PlatformInfo &gt; Generic), with how the image boots.</summary>
public sealed record BootSettings(string Model, string Serial, string Mlb, string Uuid, string Rom, bool ShowPicker, bool TextPicker, bool SipDisabled);

/// <summary>
/// A VM's OpenCore image holds the guest's graphics drivers, and its folder holds the device's option ROM.
/// Both belong to one build of the runtime: the drivers and the host library speak a protocol that changes
/// between builds. When a VM's files were made for another runtime, they are made again for this one,
/// presenting the same Mac, so macOS sees the computer it was installed on.
/// </summary>
public static class BootImage
{
    const string Previous = ".previous";

    /// <summary>What names the runtime's guest files: the driver set's list and the option ROM. Null where the runtime has none.</summary>
    public static string? SetId(EngineSettings engine)
    {
        try
        {
            string boot = GuidedSetup.BootFiles(engine);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(Path.Combine(boot, "set.toml"))); hash.AppendData(File.ReadAllBytes(Path.Combine(boot, "ReimsGraphics.rom")));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether this VM's boot files were made for another runtime than <paramref name="engine"/>, or by a version that kept no record.</summary>
    public static bool Outdated(VmConfig c, EngineSettings engine) =>
        c.BootSet != VmConfig.OwnBootFiles && GuidedSetup.Available(engine) && SetId(engine) is { } id && c.BootSet != id;

    /// <summary>What a start does first: nothing for a VM whose boot files are this runtime's.</summary>
    public static Task EnsureCurrent(VmConfig c, EngineSettings engine, string data, IProgress<string> progress, CancellationToken token) =>
        Outdated(c, engine) ? Renew(c, engine, data, progress, token) : Task.CompletedTask;

    /// <summary>
    /// The arguments that make reims-util write an image with this runtime's drivers: for a new VM, a Mac of
    /// its own of <paramref name="model"/> (reims-util's default where none is named); otherwise the Mac
    /// <paramref name="keep"/>, where an empty serial asks for a new serial and logic board serial of its model.
    /// </summary>
    public static List<string> HelperArguments(EngineSettings engine, string output, BootSettings? keep = null, string? model = null)
    {
        var args = new List<string> { "--json", "opencore", "-o", output };
        if (keep == null)
        {
            args.AddRange(["--show-picker", "--text-picker", "--disable-sip"]);
            if (model != null && model != MacModel.Default) args.AddRange(["--model", model]);
        }
        else
        {
            args.AddRange(["--no-ovmf", "--model", keep.Model]);
            if (keep.Serial.Length > 0) args.AddRange(["--serial", keep.Serial, "--mlb", keep.Mlb]);
            args.AddRange(["--uuid", keep.Uuid, "--rom", keep.Rom]);
            if (keep.ShowPicker) args.Add("--show-picker");
            if (keep.TextPicker) args.Add("--text-picker");
            if (keep.SipDisabled) args.Add("--disable-sip");
        }
        foreach (string kext in new[] { "Graft.kext", "ReimsGraphics.kext", "RgfxGate.kext" })
        { args.Add("--add-kext"); args.Add(Path.Combine(GuidedSetup.BootFiles(engine), kext) + ",20.0.0"); }
        return args;
    }

    /// <summary>
    /// Reads config.plist out of a raw OpenCore image without reading its file system: the file is XML text
    /// and sits in one piece on an image that was written once, as these are. What is looked for is a whole
    /// document around the PlatformInfo key. The image of 1.2's helper also holds the tail of a longer
    /// config.plist written before the final one, with no beginning of its own: such a piece is no document
    /// and is passed over. An image with no whole document, or with two that present different Macs, is
    /// refused, and nothing is changed.
    /// </summary>
    public static BootSettings Read(string image)
    {
        if (new FileInfo(image).Length > 1L << 30) throw new InvalidDataException(Text.Get("引导镜像超过 1 GiB，无法读取其配置。", "The boot image is larger than 1 GiB; its configuration cannot be read."));
        byte[] bytes = File.ReadAllBytes(image);
        BootSettings? found = null;
        for (int from = 0; ;)
        {
            int at = bytes.AsSpan(from).IndexOf("<key>PlatformInfo</key>"u8);
            if (at < 0) break;
            at += from; from = at + 1;
            int start = bytes.AsSpan(0, at).LastIndexOf("<?xml"u8), end = bytes.AsSpan(at).IndexOf("</plist>"u8);
            // A document is text: bytes of another file between its ends mean these ends are not one document's.
            if (start < 0 || end < 0 || end > 4 << 20 || bytes.AsSpan(start, at + end - start).IndexOf((byte)0) >= 0) continue;
            if (Parse(Encoding.UTF8.GetString(bytes, start, at + end + 8 - start)) is not { } settings) continue;
            if (found != null && found != settings) throw Unreadable();
            found = settings;
        }
        return found ?? throw Unreadable();
    }

    static BootSettings? Parse(string plist)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var root = XDocument.Load(reader).Root?.Element("dict");
            var generic = In(In(root, "PlatformInfo"), "Generic");
            string? Value(string key) => In(generic, key) is { Name.LocalName: "string" } e && e.Value.Trim().Length > 0 ? e.Value.Trim() : null;
            if (Value("SystemProductName") is not { } model || Value("SystemSerialNumber") is not { } serial || Value("MLB") is not { } mlb || Value("SystemUUID") is not { } uuid) return null;
            if (In(generic, "ROM") is not { Name.LocalName: "data" } r || Convert.FromBase64String(r.Value.Trim()) is not { Length: 6 } rom) return null;
            var boot = In(In(root, "Misc"), "Boot");
            var sip = In(In(In(In(root, "NVRAM"), "Add"), "7C436110-AB2A-4BBB-A880-FE41995C9F82"), "csr-active-config");
            return new(model, serial, mlb, uuid, Convert.ToHexString(rom),
                In(boot, "ShowPicker")?.Name.LocalName != "false", In(boot, "PickerMode")?.Value.Trim() != "External",
                sip is { Name.LocalName: "data" } && Convert.FromBase64String(sip.Value.Trim()).Any(b => b != 0));
        }
        catch (Exception e) when (e is XmlException or FormatException) { return null; }
    }

    // A plist dictionary is a run of <key>name</key> elements, each followed by its value.
    static XElement? In(XElement? dict, string key) =>
        dict?.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    static InvalidDataException Unreadable() => new(Text.Get(
        "无法从此虚拟机的引导镜像读出它的 Mac 身份，未做任何更改。请用与当前运行组件匹配的驱动重新制作引导镜像。",
        "This VM's Mac identity could not be read from its boot image; nothing was changed. Make the boot image again with the drivers of this runtime."));

    /// <summary>
    /// Makes the VM's OpenCore image again with this runtime's drivers and the Mac the old image presents, and
    /// puts this runtime's option ROM beside it. Files replaced there stay beside the new ones as *.previous.
    /// The VM must be stopped. reims-util may fetch OpenCore from the network where its cache has none.
    /// </summary>
    public static async Task Renew(VmConfig c, EngineSettings engine, string data, IProgress<string> progress, CancellationToken token)
    {
        progress.Report(Text.Get("正在为当前运行组件更新此虚拟机的引导文件，Mac 身份保持不变…", "Updating this VM's boot files for the current runtime; its Mac identity stays the same…"));
        await Replace(c, engine, Read(c.OpenCore), data, token);
    }

    /// <summary>
    /// Puts in place of the VM's OpenCore image one that presents the Mac <paramref name="want"/>, with this
    /// runtime's drivers and its option ROM beside it, and returns what the new image presents. An empty
    /// serial in <paramref name="want"/> asks for a new serial and logic board serial of its model
    /// (MacModel.Switch). Nothing is replaced unless the new image presents what was asked.
    /// </summary>
    public static async Task<BootSettings> Replace(VmConfig c, EngineSettings engine, BootSettings want, string data, CancellationToken token)
    {
        string id = SetId(engine) ?? throw new FileNotFoundException(Text.Get("运行组件不含客户机驱动。", "The runtime has no guest drivers."));
        string work = Path.Combine(Path.GetDirectoryName(c.OpenCore)!, ".renew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string made = Path.Combine(work, "OpenCore.img");
            // The helper's own lines name the identity; they are not passed on.
            await GuidedSetup.RunHelper(engine, HelperArguments(engine, made, want), data, new Quiet(), token);
            var now = Read(made);
            var asked = want.Serial.Length > 0 ? want : want with { Serial = now.Serial, Mlb = now.Mlb };
            if (now with { ShowPicker = want.ShowPicker, TextPicker = want.TextPicker, SipDisabled = want.SipDisabled } != asked)
                throw new InvalidDataException(Text.Get("新引导镜像呈现的 Mac 与要求的不一致，未做任何更改。", "The new boot image does not present the Mac it was asked to; nothing was changed."));
            // The ROM goes beside the image, in the VM's own folder: the old one may be a file other VMs share.
            string rom = Path.Combine(Path.GetDirectoryName(c.OpenCore)!, "ReimsGraphics.rom");
            File.Copy(c.OpenCore, c.OpenCore + Previous, true);
            if (File.Exists(rom)) File.Copy(rom, rom + Previous, true);
            File.Move(made, c.OpenCore, true); File.Copy(Path.Combine(GuidedSetup.BootFiles(engine), "ReimsGraphics.rom"), rom, true);
            c.Rom = rom; c.BootSet = id; c.SaveOnly("rom", "boot_set");
            return now;
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    sealed class Quiet : IProgress<string> { public void Report(string value) { } }
}
