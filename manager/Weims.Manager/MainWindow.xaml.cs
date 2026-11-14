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
        vm = model; InitializeComponent(); DataContext = vm; LanguagePicker.SelectedIndex = Text.Chinese ? 0 : 1;
        Loaded += async (_, _) => { UpdateCapability(); await vm.Refresh(); timer.Start(); if (detectOnLoad) await Work(DetectEnvironment); };
        timer.Tick += async (_, _) => { if (refreshing || vm.Busy) return; refreshing = true; try { await vm.Refresh(); } finally { refreshing = false; } };
        Closed += (_, _) => { timer.Stop(); setupCancellation?.Cancel(); updateCancellation?.Cancel(); };
        RefreshUpdateText(); RefreshRuntimeSummary();
        vm.Engine.PropertyChanged += (_, _) => { environmentReport = null; RefreshRuntimeSummary(); };
    }
    async Task Work(Func<Task> work)
    {
        if (vm.Busy) return; vm.Busy = true;
        try { await work(); }
        catch (Exception e) { vm.Message = Text.Get("操作未完成：", "Operation incomplete: ") + e.Message; MessageBox.Show(this, e.Message, Text.Get("Weims · 请检查", "Weims · Please check"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { vm.Busy = false; await vm.Refresh(); }
    }
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
        DisplayHint.Text = vm.Engine.SupportsDisplayPolicy
            ? Text.Get("显示扩展已验证。自动模式在拖动停稳约 200 ms 后调整 macOS 分辨率；固定模式只缩放画面。保存后下次启动生效。", "Display extension verified. Auto fit adjusts macOS resolution about 200 ms after resizing settles. Fixed mode scales the picture. Saved changes apply at the next start.")
            : Text.Get("当前运行包不支持新的显示策略，请使用兼容模式。自动适应与固定模式需要显示扩展运行包。", "Use Compatibility with this runtime. Auto fit and fixed resolution require the display extension runtime.");
        FreePointer.IsEnabled = vm.Engine.SupportsFreePointer;
        PointerHint.Text = FreePointer.IsEnabled ? Text.Get("输入扩展已验证。开启后鼠标可直接移出窗口；关闭后点击捕获，使用快捷键释放。", "Input extension verified. Enable to move the pointer freely; otherwise click to capture and use the shortcut to release.") : Text.Get("当前后端没有经校验的输入扩展，暂不可开启。请按 manager 文档构建输入扩展版。", "No verified input extension found. Build the extended backend using the manager guide, or use automatic setup.");
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
        vm.Engine.ValidateDisplay(c);
        if (c.FreePointer && !vm.Engine.SupportsFreePointer) throw new InvalidOperationException(Text.Get("自由鼠标需要输入扩展版后端。", "Free pointer requires the extended input backend."));
        c.Save(); vm.Message = Text.Get("配置已保存，下次启动生效。", "Settings saved. Changes apply at the next start.");
    });
    async void StartVm(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        EnsureValidFields(); vm.Message = Text.Get("正在检查并启动虚拟机…", "Checking and starting the VM…");
        await VmService.RequireStopped(Current());
        Current().Validate();
        try { await EnsureRuntime(); }
        catch (OperationCanceledException) { vm.Message = Text.Get("已取消环境准备，虚拟机未启动。", "Setup cancelled. The VM was not started."); return; }
        string key = LaunchPlan.ResolveOsk(Environment.GetEnvironmentVariable("WEIMS_OSK"));
        var run = await VmService.Start(Current(), vm.Engine, key, Path.Combine(AppContext.BaseDirectory, "launch-vm.ps1"));
        vm.Message = Text.Get("已启动独立显示窗口。日志：", "Separate display started. Logs: ") + run;
    });
    async void ShutdownVm(object sender, RoutedEventArgs e) => await Work(async () => { await VmService.Power(Current(), "system_powerdown"); vm.Message = Text.Get("已发送正常关机请求。若 macOS 未响应，请从苹果菜单关机。", "Shutdown requested. If macOS does not respond, shut down from its Apple menu."); });
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
        if (!device.Contains("guest-memory")) throw new InvalidOperationException(Text.Get("此 QEMU 不支持共享内存新通道。", "This QEMU does not support the shared-memory graphics path."));
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
    void NewVm(object sender, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        var wizard = new RecoveryWindow(vm.Engine, vm.Repo, vm.DataDirectory) { Owner = this };
        if (wizard.ShowDialog() == true && wizard.Created != null) { vm.Add(wizard.Created.ConfigPath); vm.Page = 0; vm.Message = Text.Get("新虚拟机已准备好。请检查配置后启动恢复安装。", "The new VM is ready. Check its settings, then start recovery installation."); }
    }
}
