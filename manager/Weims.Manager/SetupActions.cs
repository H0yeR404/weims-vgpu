// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Weims.Core;

namespace Weims.Manager;

public partial class MainWindow
{
    CancellationTokenSource? setupCancellation;
    EnvironmentReport? environmentReport;
    bool runtimeChecking;
    void RefreshRuntimeSummary()
    {
        string uncheckedText = Text.Get("待检查", "Not checked");
        string State(bool ready) => ready ? Text.Get("已就绪", "Ready") : Text.Get("需要处理", "Needs attention");
        // A required update shows whether the old runtime still passes its check or not (the one before 1.2.0 does not).
        bool required = environmentReport is { RuntimeUpdateRequired: true };
        bool outdated = required || environmentReport is { RuntimeReady: true, RuntimeOutdated: true };
        RuntimeState.Text = environmentReport == null ? uncheckedText : outdated ? Text.Get("有新版本", "Update available") : State(environmentReport.RuntimeReady && environmentReport.PowerShellReady);
        VirtualizationState.Text = environmentReport == null ? uncheckedText : State(environmentReport.HypervisorReady && environmentReport.SupportedHost);
        GraphicsState.Text = environmentReport == null ? uncheckedText : State(environmentReport.VulkanReady);
        bool ready = environmentReport is { SupportedHost: true, HypervisorReady: true, VulkanReady: true, RuntimeReady: true, PowerShellReady: true };
        RuntimeHeadline.Text = setupCancellation != null ? Text.Get("正在自动准备…", "Preparing your environment…")
            : runtimeChecking ? Text.Get("正在检查这台电脑…", "Checking this computer…")
            : required ? Text.Get("需要更新运行组件", "The runtime must be updated")
            : ready && outdated ? Text.Get("运行组件有新版本", "A newer runtime is available")
            : ready ? Text.Get("可以开始使用 macOS 了", "Ready for your macOS VM")
            : Text.Get("把环境准备交给 Weims", "Let Weims handle the setup");
        RuntimeNextStep.Text = setupCancellation != null ? Text.Get("下载、校验并自动保存配置。可以取消，原设置会保留。", "Downloading, verifying and saving your configuration. Cancel safely to keep existing settings.")
            : environmentReport is { SupportedHost: false } ? Text.Get("需要 Windows 10 2004 或更新的 x64 系统。", "Windows 10 2004 or newer on x64 is required.")
            : environmentReport is { HypervisorReady: false } ? Text.Get("运行组件可自动准备；请在 BIOS/UEFI 中开启虚拟化，并启用 Windows 虚拟机监控程序平台后重启。", "Components can be prepared automatically. Enable virtualization in BIOS/UEFI and Windows Hypervisor Platform, then restart.")
            : environmentReport is { VulkanReady: false } ? Text.Get("运行组件可自动准备；显卡支持尚未就绪，请检查 Vulkan 驱动与诊断详情。", "Components can be prepared automatically. Review the Vulkan driver and diagnostics to resolve graphics support.")
            : required ? EnvironmentSetup.RuntimeUpdateRequiredText() + Text.Get("点击“一键准备运行环境”即可。", " Click One-click setup.")
            : ready && outdated ? RuntimeUpdateHint()
            : ready ? Text.Get("无需填写路径。现在可以创建或导入虚拟机。", "No paths to fill in. Create or import a virtual machine to continue.")
            : Text.Get("点击一键准备。下载完成后，所有路径会自动保存。", "Click One-click setup. All paths will be saved automatically after download.");
        // An outdated runtime is announced on every page, not only on the runtime page.
        RuntimeBanner.Visibility = outdated && setupCancellation == null ? Visibility.Visible : Visibility.Collapsed;
        RuntimeBannerTitle.Text = required ? Text.Get("需要更新运行组件", "The runtime must be updated") : Text.Get("运行组件有新版本", "A newer runtime is available");
        RuntimeBannerText.Text = required
            ? Text.Get("这个版本的管理器必须使用新的运行组件，更新之前虚拟机无法启动。虚拟机和磁盘不受影响。", "This version of Manager needs its newer runtime. VMs cannot start until it is updated. VMs and disks are not affected.")
            : Text.Get("虚拟机和磁盘不受影响；正在运行的虚拟机在下次启动时改用新组件。", "VMs and disks are not affected; a running VM uses the new runtime the next time it starts.");
        SystemActions.Visibility = environmentReport is { HypervisorReady: false } or { VulkanReady: false } ? Visibility.Visible : Visibility.Collapsed;
        WindowsFeaturesButton.Visibility = environmentReport is { HypervisorReady: false } ? Visibility.Visible : Visibility.Collapsed;
        GpuHelpButton.Visibility = environmentReport is { VulkanReady: false } ? Visibility.Visible : Visibility.Collapsed;
    }
    static string RuntimeUpdateHint() => Text.Get("点击“一键准备运行环境”即可下载并切换到新的运行组件。虚拟机和磁盘不受影响；正在运行的虚拟机在下次启动时改用新组件。", "Click One-click setup to download the newer runtime and switch to it. VMs and disks are not affected; a running VM uses the new runtime the next time it starts.");
    async Task DetectEnvironment()
    {
        runtimeChecking = true; RefreshRuntimeSummary();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            ShowEnvironment(await Task.Run(() => EnvironmentSetup.Detect(vm.Engine, timeout.Token, vm.DataDirectory)));
        }
        finally { runtimeChecking = false; RefreshRuntimeSummary(); }
    }
    public void ShowEnvironment(EnvironmentReport report)
    {
        environmentReport = report;
        SetupReport.Text = report.Details;
        vm.Message = report is { RuntimeUpdateRequired: true } ? EnvironmentSetup.RuntimeUpdateRequiredText()
            : report is { RuntimeReady: true, RuntimeOutdated: true }
            ? Text.Get("运行组件有新版本。请到“运行环境”页点击“一键准备运行环境”。", "A newer runtime is available. Open Runtime and click One-click setup.")
            : Text.Get("环境检测完成。需要的组件可一键准备，无需填写路径。", "Environment checked. Prepare missing components in one click, without filling in paths.");
        RefreshRuntimeSummary();
    }
    async void CheckEnvironment(object sender, RoutedEventArgs e) => await Work(DetectEnvironment);
    async Task EnsureRuntime(bool replaceOutdated = false)
    {
        setupCancellation = new(); CancelSetup.IsEnabled = true; RefreshRuntimeSummary();
        try
        {
            var progress = new Progress<string>(s => { SetupReport.Text = s; RuntimeNextStep.Text = s; vm.Message = s; });
            var engine = await EnvironmentSetup.Ensure(vm.Engine, vm.DataDirectory, progress, setupCancellation.Token, replaceOutdated);
            setupCancellation.Token.ThrowIfCancellationRequested();
            JsonFiles.Write(Path.Combine(vm.DataDirectory, "engine.json"), engine);
            vm.Engine.Qemu = engine.Qemu; vm.Engine.RuntimeBin = engine.RuntimeBin; vm.Engine.Bios = engine.Bios; vm.Engine.PowerShell = engine.PowerShell;
            UpdateCapability();
            vm.Message = Text.Get("运行环境已就绪，所有路径已自动保存。", "Runtime ready. All paths have been saved automatically.");
        }
        finally { setupCancellation.Dispose(); setupCancellation = null; CancelSetup.IsEnabled = false; RefreshRuntimeSummary(); }
    }
    async void PrepareEnvironment(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        try { await EnsureRuntime(replaceOutdated: true); }
        catch (OperationCanceledException)
        {
            SetupReport.Text = RuntimeNextStep.Text = vm.Message = Text.Get("已取消。原运行环境设置保持不变，可以重试。", "Cancelled. Existing runtime settings are unchanged; you can retry.");
            return;
        }
        try { await DetectEnvironment(); }
        catch (OperationCanceledException)
        {
            SetupReport.Text = RuntimeNextStep.Text = vm.Message = Text.Get("运行组件已保存，但环境检测超时。请点击检查环境重试。", "Runtime saved, but environment detection timed out. Click Check environment to retry.");
        }
    });
    void CancelPreparation(object sender, RoutedEventArgs e) => setupCancellation?.Cancel();
    void OpenWindowsFeatures(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OptionalFeatures.exe")) { UseShellExecute = true });
    void OpenDriverHelp(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://vulkan.lunarg.com/doc/sdk/latest/windows/getting_started.html") { UseShellExecute = true });
    async void ChangeLanguage(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        string language = LanguagePicker.SelectedIndex == 0 ? "zh-CN" : "en";
        try
        {
            File.WriteAllText(Path.Combine(vm.DataDirectory, "language.txt"), language);
            ((App)Application.Current).ApplyLanguage(language);
            foreach (var machine in vm.Machines) machine.RefreshLanguage();
            foreach (var item in new[] { (VmNameText, "Name", "S21"), (VmSummaryText, "Summary", "S22"), (VmRecoveryText, "Recovery", "S64") })
                item.Item1.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Selected." + item.Item2) { TargetNullValue = FindResource(item.Item3) });
            UpdateCapability(); RefreshUpdateText(); RefreshRuntimeSummary(); await LoadUsb();
            await vm.Refresh();
            vm.Message = Text.Get("已切换为简体中文。", "Switched to English.");
        }
        catch (Exception ex) { vm.Message = ex.Message; }
    }
}
