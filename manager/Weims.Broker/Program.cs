// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;
using Weims.Core;

namespace Weims.Broker;

/// <summary>
/// Weims.Broker.exe: the host side of Guest Tools for one running VM. It is started after QEMU, exits
/// when QEMU exits, and neither injects anything into QEMU nor talks to QMP. If it dies, the VM and its
/// window are unaffected; clipboard sharing stops and the guest's mounts of shared folders time out until
/// someone starts it again.
/// </summary>
public static class Program
{
    public const int ExitUsage = 64, ExitNoVm = 3, ExitAlreadyRunning = 2, ExitNoPort = 4, ExitFailed = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--config") return ExitUsage;
        VmConfig config; JsonObject? runtime; Process qemu;
        try
        {
            config = VmConfig.Load(args[1]);
            runtime = VmService.Runtime(config);
            // Guest Tools need QMP on a socket: with QMP on loopback TCP the guest could reach it, and the VM must be restarted first.
            if (!Integration.RuntimeSupported(runtime) || !VmService.ProcessMatches(runtime!)) return ExitNoVm;
            qemu = Process.GetProcessById(runtime!["pid"]!.GetValue<int>());
            _ = qemu.SafeHandle;
            // The handle is now held, so the process id cannot be reused under us; make sure it is still the recorded QEMU.
            if (!VmService.ProcessMatches(runtime)) return ExitNoVm;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ExitNoVm; }

        // One broker per QEMU process. The name carries the start time, so the broker of a VM that was just
        // restarted is not blocked by the previous one while that is still on its way out.
        using var single = new Mutex(true, $@"Local\weims-broker-{qemu.Id}-{qemu.StartTime.ToFileTimeUtc()}", out bool first);
        if (!first) return ExitAlreadyRunning;

        var runDirectory = runtime["run_directory"] is JsonValue v && v.TryGetValue<string>(out var d) && Directory.Exists(d) ? d : Integration.StateDirectory(config);
        var log = new Log(Path.Combine(runDirectory, "broker.log"));
        var status = new BrokerStatus(qemu.Id);
        try { return Run(config, qemu, status, log); }
        catch (Exception e)
        {
            log.Write("fatal: " + e);
            status.Update(s => { s.LastError = e.Message; s.Stopped = true; });
            status.Write(Integration.StatePath(config), log.Write);
            return e is IOException ? ExitNoPort : ExitFailed;
        }
    }

    static int Run(VmConfig config, Process qemu, BrokerStatus status, Log log)
    {
        string mode = config.ClipboardMode;
        status.Update(s => s.ClipboardMode = mode);
        var secrets = BrokerSecrets.LoadOrCreate(Integration.SecretsPath(config));
        if (secrets.Replaced) { log.Write("stored secrets could not be read; a new identity was created"); status.Update(s => s.Note = "The stored pairing could not be read on this account or machine. Pair the guest again."); }
        var pairing = new PairingSession();
        var server = new ControlServer(ControlServer.Bind(config.IntegrationPort), secrets, pairing, status, log.Write);
        log.Write($"broker {BrokerStatus.Version} for QEMU {qemu.Id}, listening on 127.0.0.1:{server.Port}, fingerprint {secrets.Fingerprint}");

        // One limit for everything this VM's guest sends here; the shared folders are served while a helper is connected.
        var limit = new RateLimit(UploadLimit(config));
        // Files that cross with the clipboard: a folder beside the VM's state, served as a share of its own.
        bool pasteFiles = config.ClipboardFiles;
        FileTransfer? transfer = null;
        try { transfer = new FileTransfer(Path.Combine(Integration.StateDirectory(config), "transfers"), limit, log.Write); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write("files will not cross with the clipboard: " + e.Message); }
        var window = new HostWindow(qemu.Id, Integration.VmKey(config), log.Write);
        var sync = new ClipboardSync(window, () => Volatile.Read(ref mode), note => status.Update(s => s.Note = note), null, transfer, () => Volatile.Read(ref pasteFiles));
        window.ClipboardChanged += sync.HostChanged;
        window.FocusChanged += sync.FocusChanged;
        var shares = new ShareServer(status, limit, log.Write, () => server.Port, transfer?.Store);
        shares.Configure(config.Shares, pasteFiles && mode != "off");
        // A newer helper shipped with this Manager goes to an older one that can update itself; see HelperUpdate.
        var update = HelperUpdate.Load(Path.Combine(AppContext.BaseDirectory, "guest-tools"));
        if (update != null) log.Write($"helper {update.Version} is available for guests that run an older one");
        server.Connected = session =>
        {
            window.Post(() => sync.HelperConnected(session));
            shares.HelperConnected(session);
            if (update?.Wanted(session) != true) return;
            log.Write($"sending helper {update.Version} to guest user {session.User}, who runs {session.Version}");
            _ = update.Send(session);
        };
        server.Disconnected = session => { window.Post(() => sync.HelperDisconnected(session)); shares.HelperDisconnected(session); };
        server.Received = (session, frame) =>
        {
            if (frame.Type == "shares_state") { shares.FromGuest(session, frame); return; }
            if (frame.Type != "update_result") { window.Post(() => sync.FromGuest(session, frame)); return; }
            if (frame.Flag("ok")) { log.Write($"guest user {session.User} installed the helper update and restarts"); return; }
            update?.Declined(session);
            var reason = new string((frame.Text("reason") ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
            log.Write($"guest user {session.User} did not install the helper update: {reason}");
        };

        using var stop = new CancellationTokenSource();
        void Stop(string why) { log.Write("stopping: " + why); try { stop.Cancel(); } catch (ObjectDisposedException) { } window.Quit(); }

        JsonObject Command(JsonObject request)
        {
            string name = request["command"] is JsonValue c && c.TryGetValue<string>(out var given) ? given : "";
            switch (name)
            {
                case "status": break;
                case "pair-begin":
                    pairing.Open(); server.PublishPairing();
                    log.Write("pairing window opened");
                    break;
                case "pair-confirm":
                case "pair-decline":
                    // The answer applies to the number Manager showed. If that request is gone, say so instead of answering for another.
                    var showing = pairing.Showing;
                    if (showing == null || (request["digits"] is JsonValue d && d.TryGetValue<string>(out var digits) && digits != showing.Digits) || !pairing.Decide(name == "pair-confirm"))
                        return new JsonObject { ["ok"] = false, ["error"] = Text.Get("这次配对请求已经不在了，请在 macOS 里重新发起。", "That pairing request is no longer there. Start it again in macOS.") };
                    server.PublishPairing();
                    break;
                case "pair-cancel":
                    pairing.Close(); server.PublishPairing();
                    break;
                case "install-ticket":
                    // For Manager to give the install disk as its serial number. The answer travels over the pipe only this user can open.
                    var (code, until) = server.Ticket.Issue();
                    status.Update(s => s.InstallMediaEjected = false); server.PublishPairing();
                    log.Write("install code issued");
                    return new JsonObject
                    {
                        ["ok"] = true, ["state"] = status.Snapshot(),
                        ["ticket"] = new JsonObject { ["serial"] = InstallTicket.Serial(server.Port, code), ["expires_utc"] = until.ToString("o") }
                    };
                case "install-cancel":
                    server.Ticket.Cancel(); server.PublishPairing();
                    break;
                case "install-media-removed":
                    status.Update(s => s.InstallMediaEjected = false);
                    break;
                case "pair-reset":
                    server.Ticket.Cancel(); pairing.Close(); secrets.Reset(); server.ReloadIdentity(); server.PublishPairing();
                    log.Write("pairing reset: all tokens revoked, new certificate");
                    break;
                case "reload":
                    try
                    {
                        var stored = VmConfig.Load(config.ConfigPath);
                        Volatile.Write(ref mode, stored.ClipboardMode); status.Update(s => s.ClipboardMode = mode);
                        limit.BytesPerSecond = UploadLimit(stored);
                        Volatile.Write(ref pasteFiles, stored.ClipboardFiles);
                        shares.Configure(stored.Shares, stored.ClipboardFiles && stored.ClipboardMode != "off");
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
                    break;
                case "stop": _ = Task.Delay(100).ContinueWith(_ => Stop("requested over the command pipe"), TaskScheduler.Default); break; // answer first
                default: return new JsonObject { ["ok"] = false, ["error"] = "Unknown command." };
            }
            return new JsonObject { ["ok"] = true, ["state"] = status.Snapshot() };
        }

        var statePath = Integration.StatePath(config);
        var tasks = new[]
        {
            server.Run(stop.Token),
            new CommandPipe(Integration.PipeName(config), Command, log.Write).Run(stop.Token),
            status.Publish(statePath, log.Write, stop.Token),
            qemu.WaitForExitAsync(stop.Token).ContinueWith(t => { if (t.IsCompletedSuccessfully) Stop("QEMU exited"); }, TaskScheduler.Default)
        };
        // Blocks on this thread until Stop.
        window.Run(() => sync.Start(window.HasForeignContent(), window.Foreground));
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        shares.Stop();
        transfer?.Clear();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
        status.Update(s => { s.Stopped = true; s.HelperUser = null; });
        status.Write(statePath, log.Write);
        log.Write("stopped");
        return 0;
    }

    /// <summary>The configured upload limit in bytes per second, 0 for none; the default when the stored value is no number in range.</summary>
    static long UploadLimit(VmConfig config)
    {
        int mib = VmConfig.DefaultUploadLimitMiB;
        try { if (config.UploadLimitMiB is >= 0 and <= VmConfig.MaxUploadLimitMiB and var given) mib = given; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { }
        return mib * 1024L * 1024L;
    }
}

/// <summary>Append-only log next to QEMU's own logs. It never receives tokens, codes, passwords or clipboard content.</summary>
sealed class Log(string path)
{
    readonly object gate = new();
    public void Write(string message)
    {
        try { lock (gate) File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
