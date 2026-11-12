// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Weims.Core;

namespace Weims.Manager;
public partial class RecoveryWindow : Window
{
    readonly EngineSettings engine; CancellationTokenSource? cancellation;
    public VmConfig? Created { get; private set; }
    public RecoveryWindow(EngineSettings engine, string? repo)
    {
        this.engine = engine; InitializeComponent();
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
    }
    void Cancel(object sender, RoutedEventArgs e) { if (cancellation != null) { cancellation.Cancel(); Status.Text = Text.Get("正在取消，源文件不会修改…", "Cancelling; source files will remain unchanged…"); } else Close(); }
    async void Create(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        try
        {
            if (!int.TryParse(DiskSize.Text, out var size) || !int.TryParse(QmpPort.Text, out var qmp) || !int.TryParse(SshPort.Text, out var ssh)) throw new InvalidDataException(Text.Get("容量与端口必须是整数。", "Capacity and ports must be integers."));
            var request = new RecoveryRequest(VmName.Text.Trim(), Path.Combine(ParentPath.Text.Trim(), VmName.Text.Trim()), ImagePath.Text, size, OpenCorePath.Text, CodePath.Text, VarsPath.Text, RomPath.Text, qmp, ssh);
            RecoveryService.Validate(request); engine.Validate();
            cancellation = new(); Form.IsEnabled = false; CreateButton.IsEnabled = false; Progress.IsIndeterminate = true;
            Created = await RecoveryService.Create(request, engine, new Progress<string>(message => Status.Text = message), cancellation.Token);
            cancellation.Dispose(); cancellation = null; DialogResult = true;
        }
        catch (OperationCanceledException) { Status.Text = Text.Get("已取消。源文件未修改；目标目录中的未完成文件已保留，请检查后处理。", "Cancelled. Source files are unchanged. Incomplete destination files are retained for inspection."); }
        catch (Exception ex) { Status.Text = Text.Get("未完成：", "Incomplete: ") + ex.Message; MessageBox.Show(this, ex.Message, Text.Get("请检查安装配置", "Check installation settings"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { cancellation?.Dispose(); cancellation = null; Form.IsEnabled = true; CreateButton.IsEnabled = true; Progress.IsIndeterminate = false; }
    }
}
