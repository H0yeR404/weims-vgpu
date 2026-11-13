// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Weims.Core;

namespace Weims.Manager;
public partial class RecoveryWindow : Window
{
    readonly EngineSettings engine; readonly string data; CancellationTokenSource? cancellation;
    public VmConfig? Created { get; private set; }
    public RecoveryWindow(EngineSettings engine, string? repo, string? dataDirectory = null)
    {
        this.engine = engine; data = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weims", "Manager-Preview"); InitializeComponent();
        ParentPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Weims VMs");
        if (repo != null) RomPath.Text = Path.Combine(repo, "sources", "reims", "firmware", "prebuilt", "ReimsGraphics.rom");
        Closing += (_, e) => { if (cancellation != null) { e.Cancel = true; cancellation.Cancel(); Status.Text = Text.Get("正在取消，等待镜像工具退出…", "Cancelling; waiting for the image tool to exit…"); } };
    }
    void Browse(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        if (tag == "folder") { var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) ParentPath.Text = d.FolderName; return; }
        var dialog = new OpenFileDialog { Filter = tag == "image" ? Text.Get("恢复镜像|*.dmg;*.img", "Recovery image|*.dmg;*.img") : tag == "opencore" ? Text.Get("OpenCore 原始镜像|*.img", "OpenCore raw image|*.img") : tag == "rom" ? "ROM|*.rom" : Text.Get("OVMF 固件|*.fd", "OVMF firmware|*.fd") };
        if (dialog.ShowDialog(this) != true) return;
        (tag switch { "image" => ImagePath, "opencore" => OpenCorePath, "code" => CodePath, "vars" => VarsPath, _ => RomPath }).Text = dialog.FileName;
        if (tag == "image") DownloadRecovery.IsChecked = false;
    }
    void Cancel(object sender, RoutedEventArgs e) { if (cancellation != null) { cancellation.Cancel(); Status.Text = Text.Get("正在取消，源文件不会修改…", "Cancelling; source files will remain unchanged…"); } else Close(); }
    async void Create(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        try
        {
            if (!int.TryParse(DiskSize.Text, out var size)) throw new InvalidDataException(Text.Get("容量必须是整数。", "Capacity must be an integer."));
            string name = VmName.Text.Trim(), destination = Path.Combine(ParentPath.Text.Trim(), name);
            if (DownloadRecovery.IsChecked != true && string.IsNullOrWhiteSpace(ImagePath.Text)) throw new InvalidDataException(Text.Get("请选择恢复镜像或勾选自动下载。", "Choose a recovery image or enable automatic download."));
            cancellation = new(); Form.IsEnabled = false; CreateButton.IsEnabled = false; Progress.IsIndeterminate = true;
            var progress = new Progress<string>(message => Status.Text = message);
            if (ManualBoot.IsChecked != true)
                Created = await GuidedSetup.Create(name, destination, DownloadRecovery.IsChecked == true ? null : ImagePath.Text, size, engine, data, progress, cancellation.Token);
            else
            {
                if (DownloadRecovery.IsChecked == true) throw new InvalidDataException(Text.Get("手动模式请选择本地恢复镜像。", "Select a local recovery image for manual mode."));
                if (!int.TryParse(QmpPort.Text, out var qmp) || !int.TryParse(SshPort.Text, out var ssh)) throw new InvalidDataException("Invalid ports.");
                var request = new RecoveryRequest(name, destination, ImagePath.Text, size, OpenCorePath.Text, CodePath.Text, VarsPath.Text, RomPath.Text, qmp, ssh);
                Created = await RecoveryService.Create(request, engine, progress, cancellation.Token);
            }
            cancellation.Dispose(); cancellation = null; DialogResult = true;
        }
        catch (OperationCanceledException) { Status.Text = Text.Get("已取消。源文件未修改；目标目录中的未完成文件已保留，请检查后处理。", "Cancelled. Source files are unchanged. Incomplete destination files are retained for inspection."); }
        catch (Exception ex) { Status.Text = Text.Get("未完成：", "Incomplete: ") + ex.Message; MessageBox.Show(this, ex.Message, Text.Get("请检查安装配置", "Check installation settings"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { cancellation?.Dispose(); cancellation = null; Form.IsEnabled = true; CreateButton.IsEnabled = true; Progress.IsIndeterminate = false; }
    }
}
