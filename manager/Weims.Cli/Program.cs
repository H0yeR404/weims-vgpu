// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Weims.Core;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("Weims 1.2.0 — Windows x64\nKnown issue: VM freezes are possible; a fix is planned. Back up your data.\n\nWeims.Cli setup [--data-dir PATH]\nWeims.Cli create --destination PATH [--name macOS-Tahoe] [--disk-gib 200] [--recovery BaseSystem.dmg]\n  Without --recovery, download Tahoe recovery from Apple.\nWeims.Cli start --config PATH\nWeims.Cli status --config PATH\nWeims.Cli shutdown --config PATH\nWeims.Cli stop --config PATH (only for a VM started by 1.1.0 or older, after guest shutdown)\nWeims.Cli tools-enable|tools-disable --config PATH (Guest Tools: clipboard sharing with macOS; off by default)\nWeims.Cli tools-status --config PATH\nWeims.Cli tools-install --config PATH (puts the Guest Tools install disk into the running VM; installing from it pairs without input)\nWeims.Cli tools-pair --config PATH (pairs with Weims Guest Tools in macOS: both show a four-digit number to compare)\nWeims.Cli tools-reset-pairing --config PATH\nWeims.Cli tools-share --config PATH --folder PATH [--name NAME] [--access write|read] (shares a Windows folder with macOS, where Guest Tools mounts it; an existing name is replaced)\nWeims.Cli tools-unshare --config PATH --name NAME\nWeims.Cli tools-upload-limit --config PATH --mib N (most the guest may send to this PC, in MiB per second; 0, the default, for no limit)\nAll commands accept --data-dir PATH (same preferences as Manager).\nShutdown asks macOS to quit all apps and shut down; it asks about unsaved documents first. QEMU exits when the guest has powered off.");
        return 0;
    }
    string command = args[0];
    if (command is not ("setup" or "create" or "start" or "status" or "shutdown" or "stop" or "tools-enable" or "tools-disable" or "tools-status" or "tools-install" or "tools-pair" or "tools-reset-pairing" or "tools-share" or "tools-unshare" or "tools-upload-limit")) throw new ArgumentException("Unknown command. Use --help.");
    var options = new Dictionary<string,string>();
    for (int i = 1; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length || !new[] { "--data-dir", "--destination", "--name", "--disk-gib", "--recovery", "--config", "--display-mode", "--folder", "--access", "--mib" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
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
        if (command == "start" && (EnvironmentSetup.RuntimeUpdateRequired(engine, data) || await EnvironmentSetup.RuntimeIncompatible(engine, cancel.Token))) throw new InvalidOperationException(EnvironmentSetup.RuntimeUpdateRequiredText() + " Run: Weims.Cli setup");
        if (command == "start" && EnvironmentSetup.RuntimeOutdated(engine, data)) Console.Error.WriteLine("This version comes with a newer runtime than the installed one. Run: Weims.Cli setup");
        if (command == "start")
        {
            // --display-mode is accepted for command lines written for earlier versions; it no longer changes anything.
            if (options.TryGetValue("--display-mode", out var mode)) c.DisplayMode = mode;
            await VmService.RequireStopped(c);
            await BootImage.EnsureCurrent(c, engine, data, progress, cancel.Token);
            Console.WriteLine("Logs: " + await VmService.Start(c, engine, "", Path.Combine(AppContext.BaseDirectory, "launch-vm.ps1")));
        }
        else if (command == "status")
        {
            var state = await VmService.State(c); Console.WriteLine(state.Label);
            // Looking at the state is also what brings a missing broker back; there is no resident supervisor.
            if (c.IntegrationEnabled)
            {
                var broker = Integration.Supervise(c, state.Running);
                Console.WriteLine(broker.Label);
                // The same goes for an install disk the guest is done with: whoever looks, unplugs it.
                if (await InstallMedia.Collect(c, broker, cancel.Token)) Console.WriteLine("The Guest Tools install disk was removed from the VM.");
            }
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
            Console.WriteLine($"Clipboard: {(c.ClipboardMode == "off" ? "not shared" : c.ClipboardFiles ? "text and files" : "text")}");
            if (broker.Online && broker.Note is { Length: > 0 } note) Console.WriteLine(note);
            Console.WriteLine($"Upload limit: {(c.UploadLimitMiB == 0 ? "none" : c.UploadLimitMiB + " MiB/s")}");
            foreach (var share in c.Shares)
            {
                var served = broker.Shares.FirstOrDefault(s => s.Name == share.Name);
                Console.WriteLine($"Shared folder \"{share.Name}\" ({(share.ReadOnly ? "read-only" : "read-write")}): {share.Path} — {served?.State ?? "offline"}{(served?.Error is { } error ? ": " + error : "")}");
            }
            if (broker.Connected && !broker.HelperMounts && c.Shares.Count > 0) Console.WriteLine("The Guest Tools helper in macOS is from before shared folders. It updates itself from this version; otherwise install it again.");
        }
        else if (command is "tools-share" or "tools-unshare")
        {
            var shares = c.Shares.ToList();
            if (command == "tools-unshare")
            {
                if (shares.RemoveAll(s => s.Name.Equals(Required("--name"), StringComparison.OrdinalIgnoreCase)) == 0) throw new ArgumentException("No shared folder has that name.");
            }
            else
            {
                var folder = Path.GetFullPath(Required("--folder"));
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The folder does not exist: " + folder);
                if (options.GetValueOrDefault("--access", "write") is not ("write" or "read")) throw new ArgumentException("--access is write or read.");
                var name = options.GetValueOrDefault("--name") ?? ShareConfig.SuggestName(folder, shares.Select(s => s.Name));
                shares.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                shares.Add(new ShareConfig(name, folder, options.GetValueOrDefault("--access") == "read"));
            }
            await Integration.ApplyShares(c, shares, c.UploadLimitMiB, cancel.Token);
            Console.WriteLine((shares.Count == 0 ? "No folder is shared." : "Shared folders: " + string.Join(", ", shares.Select(s => $"{s.Name}{(s.ReadOnly ? " (read-only)" : "")}")) + ".") + (c.IntegrationEnabled ? "" : " Guest Tools integration is off for this VM: turn it on with tools-enable."));
        }
        else if (command == "tools-upload-limit")
        {
            if (!int.TryParse(Required("--mib"), out int mib)) throw new ArgumentException("--mib is a whole number of MiB per second; 0 for no limit.");
            await Integration.ApplyShares(c, c.Shares, mib, cancel.Token);
            Console.WriteLine(mib == 0 ? "Uploads from the guest are not limited." : $"Uploads from the guest are limited to {mib} MiB/s.");
        }
        else if (command == "tools-install")
        {
            await InstallMedia.Insert(c, cancel.Token);
            Console.WriteLine("The install disk \"Weims Guest Tools\" is now in the VM. In macOS, open it and double-click \"Install Weims Guest Tools\".");
            Console.WriteLine("Nothing has to be typed; the disk pairs this VM for the next 10 minutes. Waiting for macOS (Ctrl+C stops waiting, the disk stays in).");
            DateTime? paired = null;
            while (true)
            {
                await Task.Delay(1000, cancel.Token);
                var broker = BrokerState.Read(c);
                if (await InstallMedia.Collect(c, broker, cancel.Token)) { Console.WriteLine($"Installed and paired with: {string.Join(", ", broker.PairedUsers)}. The install disk was removed."); break; }
                if (!broker.Online) throw new IOException("The integration component stopped.");
                if (broker.InstallOpen || broker.InstallMediaEjected) continue;
                // The code is used up or has run out. If a helper is connected it was used: the helper ejects the disk
                // a moment later, and Manager, if it is open, may be the one that unplugs it.
                if (!broker.Connected) throw new IOException("The 10 minutes passed without the helper pairing. The disk is still in the VM; run tools-install again for a new one.");
                paired ??= DateTime.UtcNow;
                if (DateTime.UtcNow - paired < TimeSpan.FromSeconds(20)) continue;
                Console.WriteLine($"Installed and paired with: {string.Join(", ", broker.PairedUsers)}. If the install disk is still in the VM, it goes when Manager or the status command next looks, or when the VM shuts down.");
                break;
            }
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
