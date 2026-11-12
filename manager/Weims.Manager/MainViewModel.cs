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
    public VmConfig? Selected { get => selected; set { if (Set(ref selected, value)) { Changed(nameof(HasVm)); Changed(nameof(CanConfigure)); } } }
    public bool HasVm => Selected != null;
    public bool Busy { get => busy; set { Set(ref busy, value); Changed(nameof(Available)); Changed(nameof(CanConfigure)); Changed(nameof(CanPower)); } }
    public bool Available => !Busy;
    public bool CanEdit { get => canEdit; set { Set(ref canEdit, value); Changed(nameof(CanConfigure)); } }
    public bool CanConfigure => HasVm && CanEdit && !Busy;
    public bool CanPower => HasVm && isRunning && !Busy;
    public int Page { get => page; set => Set(ref page, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Message { get => message; set => Set(ref message, value); }
    public string Details { get => details; set => Set(ref details, value); }
    public MainViewModel(string data, string? repo)
    {
        DataDirectory = data; Repo = repo; Directory.CreateDirectory(data);
        var engineFile = Path.Combine(data, "engine.json");
        Engine = File.Exists(engineFile) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(engineFile)) ?? new() : new();
        if (repo != null && string.IsNullOrEmpty(Engine.Qemu))
        {
            var inputRuntime = Path.Combine(repo, "build", "manager-runtime", "qemu-system-x86_64.exe");
            Engine.Qemu = File.Exists(inputRuntime) ? inputRuntime : Path.Combine(repo, "build", "port-qemu", "qemu-system-x86_64.exe");
            Engine.Bios = Path.Combine(repo, "sources", "qemu", "pc-bios");
            var msys = Environment.GetEnvironmentVariable("WEIMS_MSYS2");
            var adjacent = Path.GetFullPath(Path.Combine(repo, "..", "..", "tools", "msys64", "ucrt64", "bin"));
            Engine.RuntimeBin = msys != null ? Path.Combine(msys, "ucrt64", "bin") : Directory.Exists(adjacent) ? adjacent : @"C:\msys64\ucrt64\bin";
        }
        var library = Path.Combine(data, "library.json");
        if (File.Exists(library)) foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(library)) ?? [])
        { try { Add(path, false); } catch { Message = Text.Get("部分已登记配置无法读取，请重新导入。", "Some registered configurations could not be read. Please import them again."); } }
        Selected = Machines.FirstOrDefault();
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
        if (Selected == null) { Status = Text.Get("未选择虚拟机", "No VM selected"); CanEdit = false; isRunning = false; Changed(nameof(CanPower)); return; }
        var current = Selected;
        var state = await VmService.State(current);
        if (Selected != current) return;
        Status = state.Label; CanEdit = state.CanEdit; isRunning = state.Running; Changed(nameof(CanPower));
    }
}
