// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Weims.Core;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("Weims 1.1.1 — Windows x64\nKnown issue: VM freezes are possible; a fix is planned. Back up your data.\n\nWeims.Cli setup [--data-dir PATH]\nWeims.Cli create --destination PATH [--name macOS-Tahoe] [--disk-gib 200] [--recovery BaseSystem.dmg]\n  Without --recovery, download Tahoe recovery from Apple.\nWeims.Cli start --config PATH [--display-mode auto|fixed|legacy]\nWeims.Cli status --config PATH\nWeims.Cli shutdown --config PATH\nWeims.Cli stop --config PATH (only for a VM started by 1.1.0 or older, after guest shutdown)\nWeims.Cli tools-enable|tools-disable --config PATH (Guest Tools: clipboard sharing with macOS; off by default)\nWeims.Cli tools-status --config PATH\nWeims.Cli tools-pair --config PATH (pairs with Weims Guest Tools in macOS: both show a four-digit number to compare)\nWeims.Cli tools-reset-pairing --config PATH\nAll commands accept --data-dir PATH (same preferences as Manager).\nShutdown asks macOS to quit all apps and shut down; it asks about unsaved documents first. QEMU exits when the guest has powered off.");
        return 0;
    }
    string command = args[0];
    if (command is not ("setup" or "create" or "start" or "status" or "shutdown" or "stop" or "tools-enable" or "tools-disable" or "tools-status" or "tools-pair" or "tools-reset-pairing")) throw new ArgumentException("Unknown command. Use --help.");
    var options = new Dictionary<string,string>();
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length || !new[] { "--data-dir", "--destination", "--name", "--disk-gib", "--recovery", "--config", "--display-mode" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Invalid or duplicate option. Use --help.");
    }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required: " + key);
    string data = ProfileDirectory.Resolve(options.GetValueOrDefault("--data-dir"), AppContext.BaseDirectory);
    Directory.CreateDirectory(data);
    string engineFile = Path.Combine(data, "engine.json");
    var engine = File.Exists(engineFile) ? JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(engineFile))! : new EngineSettings();
    using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    var progress = new ConsoleProgress();
    Console.Error.WriteLine("Known issue: VM freezes are possible. A fix is planned; back up important data.");
    if (command == "setup")
    {
        var prepared = await EnvironmentSetup.Prepare(data, progress, cancel.Token);
        if (!GuidedSetup.Available(prepared)) throw new IOException("Incomplete runtime.");
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
        else if (command == "status")
        {
            var state = await VmService.State(c); Console.WriteLine(state.Label);
            // Looking at the state is also what brings a missing broker back; there is no resident supervisor.
            if (c.IntegrationEnabled) Console.WriteLine(Integration.Supervise(c, state.Running).Label);
        }
        else if (command is "tools-enable" or "tools-disable")
        {
            c.IntegrationEnabled = command == "tools-enable"; c.Save();
            bool running = (await VmService.State(c)).Running;
            if (c.IntegrationEnabled) { Integration.Supervise(c, running); Console.WriteLine("Guest Tools integration is on." + (running ? " Starting it for the running VM." : " It starts with the VM.")); }
            else { if (running) try { await Integration.Command(c, "stop"); } catch (IOException) { } Console.WriteLine("Guest Tools integration is off."); }
        }
        else if (command == "tools-status")
        {
            var broker = Integration.Supervise(c, (await VmService.State(c)).Running);
            Console.WriteLine(c.IntegrationEnabled ? broker.Label : "Integration: off");
            if (broker.Online) Console.WriteLine($"Port {broker.Port}; certificate {broker.Fingerprint?[..8]}; paired with: {(broker.PairedUsers.Count == 0 ? "nobody" : string.Join(", ", broker.PairedUsers))}");
        }
        else if (command == "tools-pair")
        {
            await Integration.Command(c, "pair-begin", cancel.Token);
            Console.WriteLine("Waiting for macOS. In the guest, choose Weims Guest Tools (menu bar) > Pair with Host. Ctrl+C cancels.");
            // Nothing is typed: both sides show a four-digit number and the answer is given here.
            while (true)
            {
                await Task.Delay(500, cancel.Token);
                var broker = BrokerState.Read(c);
                if (!broker.PairingOpen) throw new IOException("The pairing window closed before a helper asked to pair.");
                if (broker.PairingDigits is not { } digits) continue;
                Console.Write($"macOS user {broker.PairingUser} asks to pair and shows a number. Manager's number is {digits}. Same number in macOS? [y/N] ");
                bool same = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
                await Integration.Command(c, new System.Text.Json.Nodes.JsonObject { ["command"] = same ? "pair-confirm" : "pair-decline", ["digits"] = digits }, cancel.Token);
                Console.WriteLine(same ? "Paired." : "Declined. Nothing was paired.");
                break;
            }
        }
        else if (command == "tools-reset-pairing") { await Integration.Command(c, "pair-reset", cancel.Token); Console.WriteLine("Pairing reset. Every guest helper has to pair again."); }
        else if (command == "stop") { await VmService.StopAfterShutdown(c); Console.WriteLine("Stopped."); }
        else { await VmService.Shutdown(c); Console.WriteLine("Asked macOS to shut down. It asks about unsaved documents first; QEMU exits when the guest has powered off."); }
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Source files are unchanged; incomplete preparation files may remain."); return 2; }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }

sealed class ConsoleProgress : IProgress<string> { public void Report(string message) => Console.WriteLine(message); }
