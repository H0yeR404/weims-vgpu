// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Weims.Core;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("Weims 1.1.0-pre.1 — Windows x64\nPRE-RELEASE: VM freezes are possible; a fix is planned. Back up your data.\n\nWeims.Cli setup [--data-dir PATH]\nWeims.Cli create --destination PATH [--name macOS-Tahoe] [--disk-gib 200] [--recovery BaseSystem.dmg]\n  Without --recovery, download Tahoe recovery from Apple.\nWeims.Cli start --config PATH [--display-mode auto|fixed|legacy]\nWeims.Cli status --config PATH\nWeims.Cli shutdown --config PATH\nWeims.Cli stop --config PATH (only after guest shutdown)\nAll commands accept --data-dir PATH (same preferences as Manager).\nShutdown requests normal guest shutdown; use the macOS menu if it does not respond.");
        return 0;
    }
    string command = args[0];
    if (command is not ("setup" or "create" or "start" or "status" or "shutdown" or "stop")) throw new ArgumentException("Unknown command. Use --help.");
    var options = new Dictionary<string,string>();
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length || !new[] { "--data-dir", "--destination", "--name", "--disk-gib", "--recovery", "--config", "--display-mode" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Invalid or duplicate option. Use --help.");
    }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required: " + key);
    string data = Path.GetFullPath(options.GetValueOrDefault("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weims", "Manager-Preview"));
    Directory.CreateDirectory(data);
    string engineFile = Path.Combine(data, "engine.json");
    var engine = File.Exists(engineFile) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(engineFile))! : new EngineSettings();
    using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    var progress = new ConsoleProgress();
    Console.Error.WriteLine("PRE-RELEASE: VM freezes are possible. A fix is planned; back up important data.");
    if (command == "setup")
    {
        var prepared = await EnvironmentSetup.Prepare(data, progress, cancel.Token);
        if (!GuidedSetup.Available(prepared)) throw new IOException("Incomplete prerelease runtime.");
        JsonFiles.Write(engineFile, prepared); Console.WriteLine("Runtime ready.");
    }
    else if (command == "create")
    {
        var c = await GuidedSetup.Create(options.GetValueOrDefault("--name", "macOS-Tahoe"), Path.GetFullPath(Required("--destination")), options.GetValueOrDefault("--recovery"), int.Parse(options.GetValueOrDefault("--disk-gib", "200")), engine, data, progress, cancel.Token);
        var library = Path.Combine(data, "library.json"); var paths = File.Exists(library) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(library))! : [];
        if (!paths.Contains(c.ConfigPath)) paths.Add(c.ConfigPath); JsonFiles.Write(library, paths);
        Console.WriteLine("Ready: " + c.ConfigPath);
    }
    else
    {
        var c = VmConfig.Load(Required("--config"));
        if (command == "start")
        {
            if (options.TryGetValue("--display-mode", out var mode)) c.DisplayMode = mode;
            Console.WriteLine("Logs: " + await VmService.Start(c, engine, "", Path.Combine(AppContext.BaseDirectory, "launch-vm.ps1")));
        }
        else if (command == "status") Console.WriteLine((await VmService.State(c)).Label);
        else if (command == "stop") { await VmService.StopAfterShutdown(c); Console.WriteLine("Stopped."); }
        else { await VmService.Power(c, "system_powerdown"); Console.WriteLine("Shutdown requested. If needed, shut down from the macOS menu."); }
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Source files are unchanged; incomplete preparation files may remain."); return 2; }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

sealed class ConsoleProgress : IProgress<string> { public void Report(string message) => Console.WriteLine(message); }
