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
        RuntimeState.Text = environmentReport == null ? uncheckedText : State(environmentReport.RuntimeReady && environmentReport.PowerShellReady);
        VirtualizationState.Text = environmentReport == null ? uncheckedText : State(environmentReport.HypervisorReady && environmentReport.SupportedHost);
        GraphicsState.Text = environmentReport == null ? uncheckedText : State(environmentReport.VulkanReady);
        bool ready = environmentReport is { SupportedHost: true, HypervisorReady: true, VulkanReady: true, RuntimeReady: true, PowerShellReady: true };
        RuntimeHeadline.Text = setupCancellation != null ? Text.Get("正在自动准备…", "Preparing your environment…")
            : runtimeChecking ? Text.Get("正在检查这台电脑…", "Checking this computer…")
            : ready ? Text.Get("可以开始使用 macOS 了", "Ready for your macOS VM")
            : Text.Get("把环境准备交给 Weims", "Let Weims handle the setup");
        RuntimeNextStep.Text = setupCancellation != null ? Text.Get("下载、校验并自动保存配置。可以取消，原设置会保留。", "Downloading, verifying and saving your configuration. Cancel safely to keep existing settings.")
            : environmentReport is { SupportedHost: false } ? Text.Get("需要 Windows 10 2004 或更新的 x64 系统。", "Windows 10 2004 or newer on x64 is required.")
            : environmentReport is { HypervisorReady: false } ? Text.Get("运行组件可自动准备；请在 BIOS/UEFI 中开启虚拟化，并启用 Windows 虚拟机监控程序平台后重启。", "Components can be prepared automatically. Enable virtualization in BIOS/UEFI and Windows Hypervisor Platform, then restart.")
            : environmentReport is { VulkanReady: false } ? Text.Get("运行组件可自动准备；显卡支持尚未就绪，请检查 Vulkan 驱动与诊断详情。", "Components can be prepared automatically. Review the Vulkan driver and diagnostics to resolve graphics support.")
            : ready ? Text.Get("无需填写路径。现在可以创建或导入虚拟机。", "No paths to fill in. Create or import a virtual machine to continue.")
            : Text.Get("点击一键准备。下载完成后，所有路径会自动保存。", "Click One-click setup. All paths will be saved automatically after download.");
        SystemActions.Visibility = environmentReport is { HypervisorReady: false } or { VulkanReady: false } ? Visibility.Visible : Visibility.Collapsed;
        WindowsFeaturesButton.Visibility = environmentReport is { HypervisorReady: false } ? Visibility.Visible : Visibility.Collapsed;
        GpuHelpButton.Visibility = environmentReport is { VulkanReady: false } ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task DetectEnvironment()
    {
        runtimeChecking = true; RefreshRuntimeSummary();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            environmentReport = await Task.Run(() => EnvironmentSetup.Detect(vm.Engine, timeout.Token));
            SetupReport.Text = environmentReport.Details;
            vm.Message = Text.Get("环境检测完成。需要的组件可一键准备，无需填写路径。", "Environment checked. Prepare missing components in one click, without filling in paths.");
        }
        finally { runtimeChecking = false; RefreshRuntimeSummary(); }
    }
    async void CheckEnvironment(object sender, RoutedEventArgs e) => await Work(DetectEnvironment);
    async Task EnsureRuntime()
    {
        setupCancellation = new(); CancelSetup.IsEnabled = true; RefreshRuntimeSummary();
        try
        {
            var progress = new Progress<string>(s => { SetupReport.Text = s; RuntimeNextStep.Text = s; vm.Message = s; });
            var engine = await EnvironmentSetup.Ensure(vm.Engine, vm.DataDirectory, progress, setupCancellation.Token);
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
        try { await EnsureRuntime(); }
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
            UpdateCapability(); RefreshUpdateText(); RefreshRuntimeSummary();
            await vm.Refresh();
            vm.Message = Text.Get("已切换为简体中文。", "Switched to English.");
        }
        catch (Exception ex) { vm.Message = ex.Message; }
    }
}
