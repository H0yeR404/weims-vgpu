// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;

namespace Weims.Core;

/// <summary>
/// Which Mac a VM's OpenCore image presents, among the Intel Macs with a Radeon that run macOS 26.
/// A serial number names its model in its last four characters, so another model comes with another
/// serial number and logic board serial; the system UUID and the ROM stay. To Apple's services the VM is
/// then another computer. Each model's serial numbers are kept beside the boot image, in a file of
/// their own and not in the VM's configuration, so going back to a model brings back the Mac it was.
/// </summary>
public static class MacModel
{
    public sealed record Choice(string Id, string Chinese, string English)
    {
        public string Name => Text.Get(Chinese, English);
        public override string ToString() => Name;
    }

    /// <summary>reims-util's own default, which every runtime's reims-util knows.</summary>
    public const string Default = "iMac20,1";
    public static readonly IReadOnlyList<Choice> Choices =
    [
        new(Default, "iMac（27 英寸，2020）", "iMac (27-inch, 2020)"),
        new("MacBookPro16,1", "MacBook Pro（16 英寸，2019）", "MacBook Pro (16-inch, 2019)"),
    ];

    public static Choice? Find(string? id) => Choices.FirstOrDefault(c => c.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Refuses a model this list does not hold, and one the runtime's reims-util has no table for.</summary>
    public static Choice Require(EngineSettings engine, string? id)
    {
        var choice = Find(id) ?? throw new InvalidDataException(Text.Get("可选的 Mac 机型：", "The Mac models to choose from: ") + string.Join(", ", Choices.Select(c => c.Id)));
        if (choice.Id != Default && !engine.SupportsMacModels)
            throw new InvalidOperationException(Text.Get($"当前运行组件只能生成 iMac 的引导镜像。请先用“一键准备运行环境”更新运行组件，再选择 {choice.Name}。", $"This runtime makes boot images for the iMac alone. Update the runtime with one-click setup, then choose {choice.Name}."));
        return choice;
    }

    public static string KeptFile(string image) => Path.Combine(Path.GetDirectoryName(image)!, "mac-models.json");

    /// <summary>The serial number and logic board serial each model had in this VM; empty where none was kept or the file cannot be read.</summary>
    public static Dictionary<string, (string Serial, string Mlb)> Kept(string image)
    {
        var kept = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(KeptFile(image))) is JsonObject all)
                foreach (var (model, value) in all)
                    if (value is JsonObject o && o["serial"] is JsonValue s && s.TryGetValue<string>(out var serial) && o["mlb"] is JsonValue m && m.TryGetValue<string>(out var mlb) && serial.Length > 0 && mlb.Length > 0)
                        kept[model] = (serial, mlb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return kept;
    }

    static void Keep(string image, Dictionary<string, (string Serial, string Mlb)> kept)
    {
        var all = new JsonObject();
        foreach (var (model, id) in kept) all[model] = new JsonObject { ["serial"] = id.Serial, ["mlb"] = id.Mlb };
        JsonFiles.Write(KeptFile(image), all);
    }

    /// <summary>The image the model switch asks for: the Mac <paramref name="now"/> as another model, with that model's kept serials or none.</summary>
    public static BootSettings As(BootSettings now, Choice model, IReadOnlyDictionary<string, (string Serial, string Mlb)> kept) =>
        kept.TryGetValue(model.Id, out var id) ? now with { Model = model.Id, Serial = id.Serial, Mlb = id.Mlb } : now with { Model = model.Id, Serial = "", Mlb = "" };

    /// <summary>
    /// Makes the VM's OpenCore image again as <paramref name="model"/>, with this runtime's drivers. The VM must
    /// be stopped. Returns false where the image already presents that model. The image replaced stays beside
    /// the new one as *.previous, and the serials of the model left are written down before anything is replaced.
    /// </summary>
    public static async Task<bool> Switch(VmConfig c, EngineSettings engine, string model, string data, IProgress<string> progress, CancellationToken token)
    {
        var choice = Require(engine, model);
        if (c.BootSet == VmConfig.OwnBootFiles)
            throw new InvalidOperationException(Text.Get("此虚拟机用的是你自己提供的引导文件，Manager 不替换它们，机型请在你的 OpenCore 配置里改。", "This VM runs on boot files of your own, which Manager does not replace. Change the model in your OpenCore configuration."));
        var now = BootImage.Read(c.OpenCore);
        if (now.Model.Equals(choice.Id, StringComparison.OrdinalIgnoreCase)) return false;
        progress.Report(Text.Get($"正在把此虚拟机的引导镜像改为 {choice.Name}…", $"Changing this VM's Mac model to {choice.Name}…"));
        var kept = Kept(c.OpenCore);
        kept[now.Model] = (now.Serial, now.Mlb); Keep(c.OpenCore, kept);
        var made = await BootImage.Replace(c, engine, As(now, choice, kept), data, token);
        kept[made.Model] = (made.Serial, made.Mlb); Keep(c.OpenCore, kept);
        return true;
    }
}
