// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Weims.Core;

namespace Weims.Manager;
public sealed class MainViewModel : Observable
{
    public string DataDirectory { get; }
    public string? Repo { get; }
    public ObservableCollection<VmConfig> Machines { get; } = new();
    public EngineSettings Engine { get; }
    VmConfig? selected; bool busy, canEdit, isRunning; int page; string status = Text.Get("请选择虚拟机", "Select a virtual machine"), message = Text.Get("欢迎使用 Weims。导入已有虚拟机，或通过恢复镜像开始。", "Welcome to Weims. Import a VM or start with a recovery image."), details = "";
    public VmConfig? Selected { get => selected; set { if (Set(ref selected, value)) { Changed(nameof(HasVm)); Changed(nameof(CanConfigure)); Changed(nameof(CanIntegrate)); ClipboardChanged(); SharesChanged(); PairingOpen = false; PairingDigits = ""; PairingPrompt = ""; } } }
    public bool HasVm => Selected != null;
    public bool Busy { get => busy; set { Set(ref busy, value); Changed(nameof(Available)); Changed(nameof(CanConfigure)); Changed(nameof(CanPower)); Changed(nameof(CanIntegrate)); Changed(nameof(CanPair)); Changed(nameof(CanStartPairing)); Changed(nameof(CanAnswerPairing)); } }
    public bool Available => !Busy;
    public bool CanEdit { get => canEdit; set { Set(ref canEdit, value); Changed(nameof(CanConfigure)); } }
    public bool CanConfigure => HasVm && CanEdit && !Busy;
    public bool CanPower => HasVm && isRunning && !Busy;
    public int Page { get => page; set => Set(ref page, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Message { get => message; set => Set(ref message, value); }
    public string Details { get => details; set => Set(ref details, value); }
    // Guest Tools. Unlike the hardware settings these do not need a stopped VM: they never reach QEMU.
    string integrationStatus = "", integrationDetail = "", pairingDigits = "", pairingPrompt = ""; bool brokerOnline, pairingOpen;
    public bool CanIntegrate => HasVm && !Busy;
    public bool CanPair => HasVm && brokerOnline && !Busy;
    public bool CanStartPairing => CanPair && !PairingOpen;
    // The install image ships next to Manager; a build without it simply does not offer the button.
    static readonly bool installImage = InstallMedia.Available;
    public bool CanInstallTools => CanPair && installImage;
    /// <summary>What to do in macOS while the install disk is in the VM; empty otherwise.</summary>
    public string InstallPrompt { get => installPrompt; private set => Set(ref installPrompt, value); }
    string installPrompt = "";
    public bool InstallOpen => InstallPrompt != "";
    public bool ClipboardShared => Selected?.ClipboardMode == "text";
    /// <summary>Files cross with the clipboard. The setting only counts while the clipboard is shared at all.</summary>
    public bool FilesShared => Selected?.ClipboardFiles == true;
    public bool BrokerConnected { get; private set; }
    public string IntegrationStatus { get => integrationStatus; set => Set(ref integrationStatus, value); }
    public string IntegrationDetail { get => integrationDetail; set => Set(ref integrationDetail, value); }
    // Pairing is done by comparing a number: nothing is typed on either side.
    /// <summary>A pairing window is open on the broker.</summary>
    public bool PairingOpen { get => pairingOpen; private set { if (Set(ref pairingOpen, value)) Changed(nameof(CanStartPairing)); } }
    /// <summary>The four digits the helper in macOS is showing; empty until a helper has asked to pair.</summary>
    public string PairingDigits { get => pairingDigits; private set { if (Set(ref pairingDigits, value)) Changed(nameof(CanAnswerPairing)); } }
    public string PairingPrompt { get => pairingPrompt; private set => Set(ref pairingPrompt, value); }
    public bool CanAnswerPairing => PairingDigits != "" && !Busy;
    public void ClipboardChanged() { Changed(nameof(ClipboardShared)); Changed(nameof(FilesShared)); }
    // Shared folders. The rows are the selected VM's stored folders; what each one says about itself comes from the broker.
    public ObservableCollection<ShareRow> Shares { get; } = new();
    string uploadLimitText = "";
    public string UploadLimitText { get => uploadLimitText; set => Set(ref uploadLimitText, value); }
    /// <summary>Shows the selected VM's folders and upload limit as they are stored now.</summary>
    public void SharesChanged()
    {
        var stored = Selected?.Shares ?? [];
        if (!Shares.Select(r => r.Share).SequenceEqual(stored)) { Shares.Clear(); foreach (var share in stored) Shares.Add(new ShareRow(share)); }
        UploadLimitText = Selected?.UploadLimitMiB.ToString() ?? "";
    }
    static string ShareText(VmConfig c, bool running, BrokerState broker, ShareConfig share)
    {
        if (!c.IntegrationEnabled) return Text.Get("集成已关闭，没有共享", "Not shared: integration is off");
        if (!running || !broker.Online) return Text.Get("虚拟机运行时共享", "Shared while the VM runs");
        return broker.Shares.FirstOrDefault(s => s.Name == share.Name) switch
        {
            { State: "mounted" } => Text.Get("已在 macOS 里挂载", "Mounted in macOS"),
            { State: "failed" } failed => Text.Get("macOS 挂载失败：", "macOS could not mount it: ") + failed.Error,
            { State: "ready" } => Text.Get("正在等待 macOS 挂载", "Waiting for macOS to mount it"),
            { Error: { } error } => error,
            _ => broker.Connected && !broker.HelperMounts ? Text.Get("macOS 里的 Guest Tools 是旧版本，不支持共享文件夹。它会自动更新；没有更新时请重新安装。", "Guest Tools in macOS is from before shared folders. It updates itself; if it does not, install it again.")
                : Text.Get("等待 macOS 里的 Guest Tools 连接", "Waiting for Guest Tools in macOS to connect")
        };
    }
    public MainViewModel(string data, string? repo)
    {
        DataDirectory = data; Repo = repo; Directory.CreateDirectory(data);
        var engineFile = Path.Combine(data, "engine.json");
        Engine = File.Exists(engineFile) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(engineFile)) ?? new() : new();
        if (repo != null && string.IsNullOrEmpty(Engine.Qemu))
        {
            // In a source checkout with no saved settings, the runtime scripts/build.ps1 assembled.
            var built = Path.Combine(repo, "build", "weims-runtime");
            Engine.Qemu = Path.Combine(built, "qemu-system-x86_64.exe"); Engine.RuntimeBin = built; Engine.Bios = Path.Combine(built, "pc-bios");
        }
        var library = Path.Combine(data, "library.json");
        if (File.Exists(library)) foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(library)) ?? [])
        { try { Add(path, false); } catch { Message = Text.Get("部分已登记配置无法读取，请重新导入。", "Some registered configurations could not be read. Please import them again."); } }
        Selected = Machines.FirstOrDefault();
        if (Machines.Count == 0 && !File.Exists(Engine.Qemu)) Page = 4;
    }
    public void Add(string path, bool save = true)
    {
        var c = VmConfig.Load(path);
        if (c.Data["lane"] != null && c.Data["lane"]!.GetValue<string>() != "reims-graphics-pci") throw new InvalidDataException(Text.Get("这是旧图形通道配置。请先迁移 OpenCore 和磁盘，再导入新通道配置。", "This configuration uses the old graphics path. Migrate OpenCore and the disk before importing it."));
        c.Validate();
        var existing = Machines.FirstOrDefault(x => x.ConfigPath.Equals(c.ConfigPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Selected = existing;
        else { Machines.Add(c); Selected = c; }
        if (save) SaveLibrary();
    }
    public void SaveLibrary() => JsonFiles.Write(Path.Combine(DataDirectory, "library.json"), Machines.Select(x => x.ConfigPath).ToArray());
    public void SaveEngine() { Engine.Validate(); JsonFiles.Write(Path.Combine(DataDirectory, "engine.json"), Engine); }
    public async Task Refresh()
    {
        if (Selected == null) { Status = Text.Get("未选择虚拟机", "No VM selected"); CanEdit = false; isRunning = false; Changed(nameof(CanPower)); brokerOnline = false; BrokerConnected = false; Changed(nameof(CanPair)); Changed(nameof(CanStartPairing)); Changed(nameof(CanInstallTools)); IntegrationStatus = ""; IntegrationDetail = ""; InstallPrompt = ""; PairingOpen = false; PairingDigits = ""; PairingPrompt = ""; return; }
        var current = Selected;
        var state = await VmService.State(current);
        if (Selected != current) return;
        Status = state.Label; CanEdit = state.CanEdit; isRunning = state.Running; Changed(nameof(CanPower));
        // Looking at the state is also what brings a missing broker back: there is no resident supervisor.
        // Off the UI thread, because it may have to start a process.
        var broker = await Task.Run(() => Integration.Supervise(current, state.Running));
        if (Selected != current) return;
        brokerOnline = broker.Online; BrokerConnected = broker.Connected; Changed(nameof(CanPair)); Changed(nameof(CanStartPairing)); Changed(nameof(CanInstallTools));
        // The helper has paired and unmounted the install volume: unplug the disk. The broker never talks to QMP.
        if (await InstallMedia.Collect(current, broker))
            Message = Text.Get("Guest Tools 已安装并配对，安装盘已从虚拟机移除。", "Guest Tools are installed and paired. The install disk was removed from the VM.");
        if (Selected != current) return;
        InstallPrompt = broker.Online && broker.InstallOpen && !broker.InstallMediaEjected
            ? Text.Get("安装盘“Weims Guest Tools”已插入虚拟机。请在 macOS 里打开它，双击“Install Weims Guest Tools”。不需要输入任何东西，10 分钟内有效。",
                "The install disk \"Weims Guest Tools\" is in the VM. In macOS, open it and double-click \"Install Weims Guest Tools\". Nothing has to be typed. It is good for 10 minutes.")
            : "";
        IntegrationStatus = !current.IntegrationEnabled ? Text.Get("集成：关闭", "Integration: off")
            : !state.Running ? Text.Get("集成：已开启，随虚拟机启动", "Integration: on, starts with the VM") : broker.Label;
        IntegrationDetail = !current.IntegrationEnabled || !broker.Online ? ""
            : broker.PairedUsers.Count == 0 ? Text.Get("尚未与 macOS 里的 Guest Tools 配对。", "Not paired with Guest Tools in macOS yet.")
            : Text.Get("已配对的 macOS 用户：", "Paired macOS users: ") + string.Join(Text.Get("、", ", "), broker.PairedUsers);
        // Something that did not cross the clipboard, or how far a transfer of copied files is.
        if (IntegrationDetail != "" && broker.Note is { Length: > 0 } note) IntegrationDetail += "\n" + note;
        foreach (var row in Shares) row.State = ShareText(current, state.Running, broker, row.Share);
        PairingOpen = broker.Online && broker.PairingOpen;
        PairingDigits = PairingOpen ? broker.PairingDigits ?? "" : "";
        PairingPrompt = !PairingOpen ? ""
            : PairingDigits == "" ? Text.Get("正在等待 macOS。请在 macOS 菜单栏点 Weims Guest Tools 的图标，选“与宿主配对”。", "Waiting for macOS. In the macOS menu bar, click the Weims Guest Tools icon and choose Pair with Host.")
            : Text.Get($"macOS 用户 {broker.PairingUser} 请求配对。macOS 里显示的也是这个数字吗？", $"macOS user {broker.PairingUser} asks to pair. Does macOS show this same number?");
    }
}

/// <summary>One shared folder in Manager's list.</summary>
public sealed class ShareRow(ShareConfig share) : Observable
{
    string state = "";
    public ShareConfig Share { get; } = share;
    public string Name => Share.Name;
    public string Path => Share.Path;
    public bool ReadOnly => Share.ReadOnly;
    public string State { get => state; set => Set(ref state, value); }
}
