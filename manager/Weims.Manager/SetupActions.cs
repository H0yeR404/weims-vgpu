// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using Weims.Core;

namespace Weims.Manager;

public partial class MainWindow
{
    CancellationTokenSource? setupCancellation;
    async Task DetectEnvironment()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var result = await Task.Run(() => EnvironmentSetup.Detect(vm.Engine, timeout.Token));
        SetupReport.Text = result.Details;
        vm.Message = Text.Get("环境检测完成。缺失的运行组件可自动准备；系统功能与驱动请按提示处理。", "Environment checked. Prepare missing components automatically; follow the guidance for system features and drivers.");
    }
    async void CheckEnvironment(object sender, RoutedEventArgs e) => await Work(DetectEnvironment);
    async void PrepareEnvironment(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        setupCancellation = new(); CancelSetup.IsEnabled = true;
        try
        {
            var progress = new Progress<string>(s => { SetupReport.Text = s; vm.Message = s; });
            var engine = await EnvironmentSetup.Prepare(vm.DataDirectory, progress, setupCancellation.Token);
            // Save validated new paths without altering any active VM or custom paths on failure.
            JsonFiles.Write(Path.Combine(vm.DataDirectory, "engine.json"), engine);
            vm.Engine.Qemu = engine.Qemu; vm.Engine.RuntimeBin = engine.RuntimeBin; vm.Engine.Bios = engine.Bios; vm.Engine.PowerShell = engine.PowerShell;
            UpdateCapability();
            SetupReport.Text = Text.Get("运行组件已安装并保存。点击检测环境查看系统功能与驱动状态。", "Runtime installed and saved. Check the environment to review system features and GPU readiness.");
            vm.Message = SetupReport.Text;
        }
        catch (OperationCanceledException) { SetupReport.Text = Text.Get("已取消。原运行环境设置保持不变，可以重试。", "Cancelled. Existing runtime settings are unchanged; you can retry."); }
        finally { setupCancellation.Dispose(); setupCancellation = null; CancelSetup.IsEnabled = false; }
    });
    void CancelPreparation(object sender, RoutedEventArgs e) => setupCancellation?.Cancel();
    void OpenWindowsFeatures(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OptionalFeatures.exe")) { UseShellExecute = true });
    void OpenDriverHelp(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://vulkan.lunarg.com/doc/sdk/latest/windows/getting_started.html") { UseShellExecute = true });
    void ChangeLanguage(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        string language = LanguagePicker.SelectedIndex == 0 ? "zh-CN" : "en";
        try
        {
            File.WriteAllText(Path.Combine(vm.DataDirectory, "language.txt"), language);
            vm.Message = Text.Get("语言已保存。关闭并重新打开管理器后生效；虚拟机继续运行。", "Language saved. Close and reopen the manager to apply; VMs will keep running.");
        }
        catch (Exception ex) { vm.Message = ex.Message; }
    }
}
