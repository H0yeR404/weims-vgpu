// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Weims.Core;

namespace Weims.Manager;

public partial class MainWindow
{
    public static string VersionNumber => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public string AppVersion => "Manager " + VersionNumber;
    ManagerRelease? update;
    string? downloadedUpdate;
    CancellationTokenSource? updateCancellation;
    string updateState = "idle";
    void RefreshUpdateText()
    {
        UpdateChannel.Text = ReleaseVersion.Parse(VersionNumber).Preview == null
            ? Text.Get("更新通道：稳定版", "Update channel: stable releases")
            : Text.Get("更新通道：预览版和稳定版", "Update channel: preview and stable releases");
        UpdateStatus.Text = updateState switch
        {
            "checking" => Text.Get("正在检查 GitHub 发布…", "Checking GitHub releases…"),
            "current" => Text.Get("当前没有更新的兼容版本。", "No newer compatible release is available."),
            "available" => Text.Get("可用更新：", "Update available: ") + update?.Tag,
            "downloading" => Text.Get("正在下载并校验更新…", "Downloading and verifying update…"),
            "ready" => Text.Get("更新已下载并校验。关闭所有 QEMU 虚拟机和其他管理器窗口后，点击一键更新继续安装。", "Update downloaded and verified. Close all QEMU VMs and other Manager windows, then click Update now to install."),
            "cancelled" => Text.Get("已取消，可以重试。", "Cancelled. You can retry."),
            "failed" => Text.Get("更新未完成，请检查下方错误后重试。", "Update incomplete. Review the error below and retry."),
            _ => Text.Get("点击检查更新以连接 GitHub。", "Click Check for updates to connect to GitHub.")
        };
        if (update == null) ReleaseNotes.SetResourceReference(System.Windows.Controls.TextBox.TextProperty, "UpdateCurrentNotes");
        else ReleaseNotes.Text = string.IsNullOrWhiteSpace(update.Notes) ? Text.Get("此版本未提供更新说明。", "No release notes were provided for this version.") : update.Notes;
        InstallUpdateButton.IsEnabled = update != null && !vm.Busy;
    }
    async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        await UpdateWork(async token =>
        {
            update = null; downloadedUpdate = null; ReleaseNotes.Text = "";
            updateState = "checking"; RefreshUpdateText(); UpdateProgress.IsIndeterminate = true;
            using var service = new ManagerUpdates(); update = await service.Check(VersionNumber, token);
            updateState = update == null ? "current" : "available";
            ReleaseNotes.Text = update?.Notes ?? "";
        });
    }
    async Task UpdateWork(Func<CancellationToken, Task> action)
    {
        if (vm.Busy) return;
        await Work(async () =>
        {
            updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            CancelUpdateButton.IsEnabled = true; InstallUpdateButton.IsEnabled = false;
            try { await action(updateCancellation.Token); }
            catch (OperationCanceledException) { updateState = "cancelled"; }
            catch { updateState = "failed"; throw; }
            finally { updateCancellation.Dispose(); updateCancellation = null; CancelUpdateButton.IsEnabled = false; UpdateProgress.IsIndeterminate = false; }
        });
        RefreshUpdateText();
    }
    async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (update == null) return;
        await UpdateWork(async token =>
        {
            var release = update;
            if (downloadedUpdate == null)
            {
                updateState = "downloading"; RefreshUpdateText(); UpdateProgress.Value = 0;
                using var service = new ManagerUpdates();
                downloadedUpdate = await service.Download(release, Path.Combine(vm.DataDirectory, "updates"), new Progress<double>(p => UpdateProgress.Value = p), token);
            }
            updateState = "ready";
            // Include unregistered QEMU instances. Never stop a guest to update its manager.
            foreach (var machine in vm.Machines) if (!(await VmService.State(machine)).CanEdit) return;
            var processes = Process.GetProcessesByName("qemu-system-x86_64");
            bool any = processes.Length != 0; foreach (var process in processes) process.Dispose();
            if (any) return;
            var managers = Process.GetProcessesByName("Weims.Manager");
            bool otherManager = managers.Any(p => p.Id != Environment.ProcessId);
            foreach (var process in managers) process.Dispose();
            if (otherManager) return;
            await ManagerUpdates.Verify(downloadedUpdate, release.Sha256, token);
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", release.Version.Preview == null ? "Weims Manager" : "Weims Manager Preview");
            // Honor custom installation locations for this channel only.
            string appId = release.Version.Preview == null ? "{BB2BC22A-61BD-4C46-9013-0492F18F722D}_is1" : "{E8E74513-80F4-453E-8183-5CD70BC29070}_is1";
            using var uninstall = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + appId);
            if (appId.Length != 0 && uninstall?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)) installDir = location;
            if (installDir.Contains('"')) throw new InvalidDataException("Invalid installation directory.");
            string job = Path.Combine(vm.DataDirectory, "updates", "install-" + Guid.NewGuid().ToString("N") + ".json");
            JsonFiles.Write(job, new { pid = Environment.ProcessId, started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(), installer = downloadedUpdate, sha256 = release.Sha256, destination = installDir, data = vm.DataDirectory });
            // Copy the helper outside the installation directory before it is replaced.
            string helper = Path.ChangeExtension(job, ".ps1"); File.Copy(Path.Combine(AppContext.BaseDirectory, "update-manager.ps1"), helper);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-JobFile", job }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
            // A cancelled/failed handoff cannot leave an installer waiting for a later exit.
            try
            {
                for (int i = 0; i < 300 && !File.Exists(job + ".ready"); i++)
                {
                    if (child.HasExited) throw new IOException(Text.Get("更新助手启动失败，请查看 updates 文件夹中的日志。", "Update helper failed. See the log in the updates folder."));
                    await Task.Delay(100, token);
                }
                if (!File.Exists(job + ".ready")) throw new IOException("Update helper did not become ready. Manager remains open.");
                token.ThrowIfCancellationRequested();
                File.WriteAllText(job + ".commit", "install");
            }
            catch
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                throw;
            }
            Application.Current.Shutdown();
        });
    }
    void CancelUpdate(object sender, RoutedEventArgs e) => updateCancellation?.Cancel();
}
