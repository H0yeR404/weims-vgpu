// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Weims.Core;

namespace Weims.Manager;
public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    bool refreshing;
    public int[] CpuChoices { get; } = [2, 4, 6, 8, 12, 16, 24, 32, 48, 64];
    public int[] MemoryChoices { get; } = [4096, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 262144];
    public string[] ResolutionChoices { get; } = ["1280x800", "1440x900", "1600x900", "1920x1080", "1920x1200", "2048x1152", "2560x1440", "2560x1600", "3440x1440", "3840x2160"];
    public MainWindow(MainViewModel model, bool detectOnLoad = true)
    {
        vm = model;
        var app = (App)Application.Current; app.LoadTheme(vm.DataDirectory);
        InitializeComponent(); DataContext = vm; LanguagePicker.SelectedIndex = Text.Chinese ? 0 : 1;
        ThemePicker.SelectedValue = app.Theme;
        Loaded += async (_, _) => { UpdateCapability(); await vm.Refresh(); timer.Start(); if (detectOnLoad) await Work(DetectEnvironment); };
        timer.Tick += async (_, _) => { if (refreshing || vm.Busy) return; refreshing = true; try { await vm.Refresh(); } finally { refreshing = false; Pace(); } };
        Closed += (_, _) => { timer.Stop(); setupCancellation?.Cancel(); updateCancellation?.Cancel(); };
        RefreshUpdateText(); RefreshRuntimeSummary();
        vm.Engine.PropertyChanged += (_, _) => { environmentReport = null; RefreshRuntimeSummary(); };
    }
    async Task Work(Func<Task> work)
    {
        if (vm.Busy) return; vm.Busy = true;
        try { await work(); }
        catch (Exception e) { vm.Message = Text.Get("操作未完成：", "Operation incomplete: ") + e.Message; MessageBox.Show(this, e.Message, Text.Get("Weims · 请检查", "Weims · Please check"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { vm.Busy = false; await vm.Refresh(); Pace(); }
    }
    // While a pairing is under way the number should appear promptly, so the state is looked at every second instead of every five.
    void Pace() => timer.Interval = TimeSpan.FromSeconds(vm.PairingOpen || vm.InstallOpen ? 1 : 5);
    VmConfig Current() => vm.Selected ?? throw new InvalidOperationException(Text.Get("请先导入或创建虚拟机。", "Import or create a virtual machine first."));
    bool HasErrors(DependencyObject obj)
    {
        if (Validation.GetHasError(obj)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) if (HasErrors(VisualTreeHelper.GetChild(obj, i))) return true;
        return false;
    }
    void EnsureValidFields() { if (HasErrors(this)) throw new InvalidDataException(Text.Get("请修正红框中的输入；端口必须是整数。", "Correct the highlighted fields; ports must be integers.")); }
    void UpdateCapability()
    {
        FreePointer.IsEnabled = vm.Engine.SupportsFreePointer;
        PointerHint.Text = FreePointer.IsEnabled ? Text.Get("开启后鼠标可直接移出窗口，Windows 快捷键照常可用；关闭后点击窗口捕获键盘和鼠标，按 Ctrl+Alt+Win 释放。游戏需要关闭此项才能使用相对鼠标。", "When on, the pointer moves freely in and out of the window and Windows keeps its shortcuts. When off, a click captures keyboard and pointer and Ctrl+Alt+Win releases them. Games need it off to get relative mouse motion.") : Text.Get("当前运行组件是上游原版，没有自由鼠标。点击窗口捕获键盘和鼠标，按 Ctrl+Alt+Win 释放。用“一键准备运行环境”可安装带自由鼠标的运行组件。", "This runtime is upstream's own and has no free pointer. A click captures keyboard and pointer; Ctrl+Alt+Win releases them. One-click setup installs the runtime that has it.");
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    async void MachineChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !vm.Busy) await vm.Refresh(); }
    async void RefreshState(object sender, RoutedEventArgs e) => await Work(async () => { UpdateCapability(); await vm.Refresh(); vm.Message = vm.Status; });
    async void ImportVm(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Text.Get("选择新通道虚拟机的 state/config.json", "Select the VM state/config.json for the new graphics path"), Filter = Text.Get("虚拟机配置|config.json|JSON 配置|*.json", "VM configuration|config.json|JSON configuration|*.json") };
        if (dialog.ShowDialog(this) == true) await Work(() => { vm.Add(dialog.FileName); vm.Message = Text.Get("已导入配置。原磁盘继续使用，未复制或修改。", "Configuration imported. The original disk is used without copying or modifying it."); return Task.CompletedTask; });
    }
    async void SaveVm(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        EnsureValidFields(); var c = Current(); await VmService.RequireStopped(c);
        if (c.FreePointer && !vm.Engine.SupportsFreePointer) throw new InvalidOperationException(Text.Get("当前运行组件不支持自由鼠标。", "This runtime does not support free pointer."));
        c.Save(); vm.Message = Text.Get("配置已保存，下次启动生效。", "Settings saved. Changes apply at the next start.");
    });
    async void StartVm(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        EnsureValidFields(); vm.Message = Text.Get("正在检查并启动虚拟机…", "Checking and starting the VM…");
        await VmService.RequireStopped(Current());
        Current().Validate();
        try { await EnsureRuntime(); }
        catch (OperationCanceledException) { vm.Message = Text.Get("已取消环境准备，虚拟机未启动。", "Setup cancelled. The VM was not started."); return; }
        await BootImage.EnsureCurrent(Current(), vm.Engine, vm.DataDirectory, new Progress<string>(s => vm.Message = s), CancellationToken.None);
        string key = LaunchPlan.ResolveOsk(Environment.GetEnvironmentVariable("WEIMS_OSK"));
        var run = await VmService.Start(Current(), vm.Engine, key, Path.Combine(AppContext.BaseDirectory, "launch-vm.ps1"));
        vm.Message = Text.Get("已启动独立显示窗口。日志：", "Separate display started. Logs: ") + run;
    });
    async void ShutdownVm(object sender, RoutedEventArgs e) => await Work(async () => { await VmService.Shutdown(Current()); vm.Message = Text.Get("已请求 macOS 关机。有未保存的文档时 macOS 会先询问；关机完成后虚拟机窗口自动关闭。", "Asked macOS to shut down. It asks about unsaved documents first; the VM window closes when it has powered off."); });
    async void ForceStop(object sender, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        if (MessageBox.Show(this, Text.Get("立即停止当前虚拟机的 QEMU 进程？尚未保存的数据可能丢失。", "Stop this VM's QEMU process immediately? Unsaved data may be lost."), Text.Get("确认强制停止", "Confirm force stop"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Work(async () => { await VmService.Power(Current(), "quit"); vm.Message = Text.Get("已请求 QEMU 退出。", "QEMU exit requested."); });
    }
    void ChooseEngine(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        if (tag == "qemu") { var d = new OpenFileDialog { Filter = "QEMU|qemu-system-x86_64.exe" }; if (d.ShowDialog(this) == true) vm.Engine.Qemu = d.FileName; }
        else { var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) { if (tag == "runtime") vm.Engine.RuntimeBin = d.FolderName; else vm.Engine.Bios = d.FolderName; } }
        UpdateCapability();
    }
    async void SaveEngine(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        vm.Engine.Validate(); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = new Dictionary<string,string> { ["PATH"] = vm.Engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH") };
        var version = await ProcessRunner.Run(vm.Engine.Qemu, new[] { "--version" }, env, limit.Token);
        var device = await ProcessRunner.Run(vm.Engine.Qemu, new[] { "-device", "reims-graphics-pci,help" }, env, limit.Token);
        if (device.Contains("guest-memory")) throw new InvalidOperationException(Text.Get("此 QEMU 属于 1.2 及更早版本，与当前版本不兼容。", "This QEMU belongs to version 1.2 or earlier and does not work with this version."));
        var accel = await ProcessRunner.Run(vm.Engine.Qemu, new[] { "-accel", "help" }, env, limit.Token);
        if (!accel.Contains("whpx")) throw new InvalidOperationException(Text.Get("此 QEMU 未编译 WHPX。", "This QEMU was built without WHPX."));
        await ProcessRunner.Run(vm.Engine.PowerShell, new[] { "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major" }, null, limit.Token);
        vm.SaveEngine(); UpdateCapability(); vm.Message = Text.Get("运行组件检查通过并已保存。WHPX 启用状态和 Vulkan 设备将在实际启动时确认。", "Runtime components checked and saved. WHPX and Vulkan availability will be confirmed at startup."); vm.Details = version + "\n" + device;
    });
    async void Preview(object sender, RoutedEventArgs e) => await Work(() =>
    {
        EnsureValidFields(); var c = Current();
        var plan = LaunchPlan.Build(c, vm.Engine, Path.Combine(c.DirectoryPath, "logs", "preview"), new string('x', 64), "weims-preview");
        vm.Details = plan.Preview + "\n\n" + string.Join("\n", plan.Environment.Where(v => v.Key != "PATH").Select(v => v.Key + "=" + v.Value)); vm.Message = Text.Get("参数预览完成；未启动虚拟机，未写入配置。", "Arguments previewed. No VM started or configuration written."); return Task.CompletedTask;
    });
    string LogDirectory()
    {
        var c = Current(); var rt = VmService.Runtime(c);
        return rt?["run_directory"]?.GetValue<string>() ?? Path.Combine(c.DirectoryPath, "logs");
    }
    async void ReadLogs(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        var dir = LogDirectory();
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(Text.Get("尚无运行日志。", "No runtime logs yet."));
        vm.Details = await Task.Run(() => string.Join("\n", new[] { "stderr.log", "stdout.log", "serial.log" }.Where(n => File.Exists(Path.Combine(dir,n))).Select(n => n + "\n" + ReadTail(Path.Combine(dir,n)))));
        vm.Message = Text.Get("已读取最近日志尾部；启动密钥不出现在参数预览中。", "Recent log tails loaded. Startup keys are hidden in argument previews.");
    });
    static string ReadTail(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Seek(Math.Max(0, f.Length - 32000), SeekOrigin.Begin); using var r = new StreamReader(f); return r.ReadToEnd();
    }
    async void OpenLogs(object sender, RoutedEventArgs e) => await Work(() => { var dir = LogDirectory(); if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(Text.Get("尚无运行日志。", "No runtime logs yet.")); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false }); return Task.CompletedTask; });
    // ---- Guest Tools ----
    async void ToggleIntegration(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        var c = Current(); bool on = ((CheckBox)sender).IsChecked == true;
        c.IntegrationEnabled = on;
        try { c.SaveOnly("integration_enabled"); } catch { c.IntegrationEnabled = !on; throw; }
        // Turning it off ends the broker of a running VM. Turning it on needs nothing here: the refresh that follows starts one.
        if (!on) { try { await Integration.Command(c, "stop"); } catch (IOException) { } }
        vm.Message = on ? Text.Get("已开启 Guest Tools 集成。", "Guest Tools integration is on.") : Text.Get("已关闭 Guest Tools 集成。", "Guest Tools integration is off.");
    });
    async void ToggleClipboard(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        var c = Current(); string before = c.ClipboardMode;
        c.ClipboardMode = ((CheckBox)sender).IsChecked == true ? "text" : "off";
        try { c.SaveOnly("clipboard_mode"); } catch { c.ClipboardMode = before; throw; } finally { vm.ClipboardChanged(); }
        // A running broker reads the new mode at once; without one there is nothing to tell.
        try { await Integration.Command(c, "reload"); } catch (IOException) { }
        vm.Message = c.ClipboardMode == "text" ? Text.Get("剪贴板文本共享已开启。", "Clipboard text sharing is on.") : Text.Get("剪贴板共享已关闭。", "Clipboard sharing is off.");
    });
    // Shared folders and the upload limit are stored and taken up at once, like the clipboard setting.
    Task StoreShares(IReadOnlyList<ShareConfig> shares, int uploadLimitMiB, string done) => Work(async () =>
    {
        try { await Integration.ApplyShares(Current(), shares, uploadLimitMiB); } finally { vm.SharesChanged(); }
        vm.Message = done;
    });
    async void AddShare(object sender, RoutedEventArgs e)
    {
        if (vm.Busy || vm.Selected is not { } c) return;
        var dialog = new OpenFolderDialog { Title = Text.Get("选择要与 macOS 共享的文件夹", "Choose a folder to share with macOS") };
        if (dialog.ShowDialog(this) != true) return;
        var shares = c.Shares.ToList();
        if (shares.Any(s => s.Path.Equals(dialog.FolderName, StringComparison.OrdinalIgnoreCase))) { vm.Message = Text.Get("这个文件夹已经共享了。", "That folder is shared already."); return; }
        shares.Add(new ShareConfig(ShareConfig.SuggestName(dialog.FolderName, shares.Select(s => s.Name)), dialog.FolderName, false));
        await StoreShares(shares, c.UploadLimitMiB, Text.Get("已共享文件夹。稍后它会出现在 macOS 个人文件夹下的 Weims Shared Folders 里。", "Folder shared. In a moment it appears in Weims Shared Folders in the macOS home folder."));
    }
    async void RemoveShare(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ShareRow row || vm.Selected is not { } c) return;
        await StoreShares([.. c.Shares.Where(s => s != row.Share)], c.UploadLimitMiB, Text.Get("已停止共享这个文件夹。", "That folder is no longer shared."));
    }
    async void ToggleShareReadOnly(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ShareRow row || vm.Selected is not { } c) return;
        bool readOnly = ((CheckBox)sender).IsChecked == true;
        await StoreShares([.. c.Shares.Select(s => s == row.Share ? s with { ReadOnly = readOnly } : s)], c.UploadLimitMiB,
            readOnly ? Text.Get("macOS 现在只能读这个文件夹。", "macOS can now only read that folder.") : Text.Get("macOS 现在可以读写这个文件夹。", "macOS can now read and write that folder."));
    }
    async void ApplyUploadLimit(object sender, RoutedEventArgs e)
    {
        if (vm.Selected is not { } c) return;
        if (!int.TryParse(vm.UploadLimitText.Trim(), out int mib)) { vm.Message = Text.Get("上传限速应为整数，单位是 MiB/s；0 表示不限。", "The upload limit is a whole number of MiB/s; 0 means no limit."); return; }
        await StoreShares(c.Shares, mib, mib == 0 ? Text.Get("已取消上传限速。", "Uploads from macOS are no longer limited.") : Text.Get($"上传限速已设为 {mib} MiB/s。", $"Uploads from macOS are limited to {mib} MiB/s."));
    }
    // Plugs the install disk, carrying this VM's pairing ticket, into the running VM. See InstallMedia.
    async void InstallGuestTools(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        await InstallMedia.Insert(Current());
        vm.Message = Text.Get("安装盘已插入虚拟机。请在 macOS 里打开“Weims Guest Tools”并双击安装器。", "The install disk is in the VM. In macOS, open \"Weims Guest Tools\" and double-click the installer.");
    });
    // Pairing compares a number shown on both sides; nothing is typed. See PairingSession in the broker.
    async void PairGuestTools(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        await Integration.Command(Current(), "pair-begin");
        vm.Message = Text.Get("已打开配对，10 分钟内有效。请到 macOS 里发起配对。", "Pairing is open for 10 minutes. Start it in macOS.");
    });
    Task AnswerPairing(bool same) => Work(async () =>
    {
        // The digits go along so that the answer can only apply to the number that was on screen.
        await Integration.Command(Current(), new System.Text.Json.Nodes.JsonObject { ["command"] = same ? "pair-confirm" : "pair-decline", ["digits"] = vm.PairingDigits });
        vm.Message = same ? Text.Get("配对完成。", "Paired.") : Text.Get("已拒绝这次配对请求，没有配对任何设备。", "That request was declined. Nothing was paired.");
    });
    async void ConfirmPairing(object sender, RoutedEventArgs e) => await AnswerPairing(true);
    async void DeclinePairing(object sender, RoutedEventArgs e) => await AnswerPairing(false);
    async void CancelPairing(object sender, RoutedEventArgs e) => await Work(async () => { await Integration.Command(Current(), "pair-cancel"); vm.Message = Text.Get("已取消配对。", "Pairing cancelled."); });
    async void ResetPairing(object sender, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        if (MessageBox.Show(this, Text.Get("重置后，这台虚拟机里所有已配对的 Guest Tools 都要重新配对。继续？", "After a reset every paired Guest Tools helper in this VM has to pair again. Continue?"), Text.Get("确认重置配对", "Confirm pairing reset"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Work(async () => { await Integration.Command(Current(), "pair-reset"); vm.Message = Text.Get("配对已重置。", "Pairing reset."); });
    }
    void NewVm(object sender, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        var wizard = new RecoveryWindow(vm.Engine, vm.Repo, vm.DataDirectory) { Owner = this };
        if (wizard.ShowDialog() == true && wizard.Created != null) { vm.Add(wizard.Created.ConfigPath); vm.Page = 0; vm.Message = Text.Get("新虚拟机已准备好。请检查配置后启动恢复安装。", "The new VM is ready. Check its settings, then start recovery installation."); }
    }
}
