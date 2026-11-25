// SPDX-License-Identifier: GPL-2.0-or-later
// File copy and paste: the list of files as Windows keeps it on the clipboard, the share that copied files are
// read from and brought to, and the rules for when they cross. The SMB library's own client stands in for
// macOS. The Windows clipboard itself is never touched, and no guest is involved.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using Weims.Broker;
using Weims.Core;

/// <summary>A file store whose every operation throws, as a bug in a store would.</summary>
public class FaultyStore : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("a fault in the store");
}

static class Files
{
    sealed class OwnPortServer(SMBShareCollection shares, GSSProvider security) : SMBServer(shares, security)
    {
        public void Start(int port) => Start(IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port, false, true, true, null);
    }

    sealed class FakeExchange : IFileExchange
    {
        public List<IReadOnlyList<string>> Offers = [];
        public (IReadOnlyList<string> Items, int LeftOut)? Answer = (new[] { "1/a.txt", "1/目录" }, 0);
        public int Places; public bool NoRoom;
        public List<string> Discarded = [];
        public IReadOnlyList<string> Delivery = [@"C:\vm\transfers\in\1\from mac.txt"];
        public (IReadOnlyList<string> Items, int LeftOut)? Offer(IReadOnlyList<string> paths) { Offers.Add(paths); return Answer; }
        public (string Id, long Limit)? Receive() => NoRoom ? null : ((++Places).ToString(), 1000);
        public IReadOnlyList<string> Received(string id) => Delivery;
        public void Discard(string id) => Discarded.Add(id);
    }

    static List<string> List(INTFileStore store, string path)
    {
        if (Shares.Open(store, path, Shares.Reading, CreateDisposition.FILE_OPEN, out var handle, CreateOptions.FILE_DIRECTORY_FILE) != NTStatus.STATUS_SUCCESS) throw new Exception("FAIL: the folder \"" + path + "\" did not open");
        var status = store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
        store.CloseFile(handle);
        if (status != NTStatus.STATUS_SUCCESS) throw new Exception("FAIL: the folder \"" + path + "\" was not listed: " + status);
        return [.. entries.Select(e => ((FileDirectoryInformation)e).FileName).Where(n => n is not ("." or "..")).Order(StringComparer.Ordinal)];
    }
    static string? Read(INTFileStore store, string path)
    {
        if (Shares.Open(store, path, Shares.Reading, CreateDisposition.FILE_OPEN, out var handle) != NTStatus.STATUS_SUCCESS) return null;
        var status = store.ReadFile(out var data, handle, 0, 1000);
        store.CloseFile(handle);
        return status == NTStatus.STATUS_SUCCESS ? Encoding.UTF8.GetString(data) : null;
    }
    static NTStatus Write(INTFileStore store, string path, byte[] data)
    {
        var status = Shares.Open(store, path, Shares.Writing, CreateDisposition.FILE_OPEN_IF, out var handle);
        if (status != NTStatus.STATUS_SUCCESS) return status;
        status = store.WriteFile(out _, handle, 0, data);
        store.CloseFile(handle);
        return status;
    }
    static NTStatus Opens(INTFileStore store, string path, bool folder = false)
    {
        var status = Shares.Open(store, path, Shares.Reading, CreateDisposition.FILE_OPEN, out var handle, folder ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE);
        if (status == NTStatus.STATUS_SUCCESS) store.CloseFile(handle);
        return status;
    }

    public static async Task Run(Action<bool, string> Check, Func<Func<bool>, int, Task<bool>> Eventually, string temp)
    {
        // ---- The list of files as Explorer takes it ----
        {
            var list = HostWindow.DropList([@"C:\a.txt", @"D:\目录\b"]);
            Check(BitConverter.ToUInt32(list, 0) == 20 && BitConverter.ToUInt32(list, 16) == 1 && list.AsSpan(4, 12).ToArray().All(b => b == 0)
                && Encoding.Unicode.GetString(list, 20, list.Length - 20) == "C:\\a.txt\0D:\\目录\\b\0\0", "a list of files for the Windows clipboard has its header, each path ended, and an empty one last");
        }

        // ---- A budget ----
        {
            var budget = new Budget();
            Check(!budget.Take(1), "a budget that was never set takes nothing");
            budget.Reset(10);
            Check(budget.Take(6) && !budget.Take(5) && budget.Take(4) && !budget.Take(1) && budget.Size == 10, "a budget takes what it was set to and no more, in whatever pieces");
        }

        // ---- The setting ----
        {
            var state = Path.Combine(temp, "files-config", "state"); Directory.CreateDirectory(state);
            var c = new VmConfig(Path.Combine(state, "config.json"), new JsonObject { ["mac"] = "52:54:00:12:34:56" });
            foreach (var key in new[] { "active_disk", "opencore", "firmware_code", "firmware_vars", "rom" }) c.Data[key] = @"C:\vm\" + key;
            JsonFiles.Write(c.ConfigPath, c.Data);
            Check(c.ClipboardFiles && !c.Data.ContainsKey("clipboard_files"), "files cross with the clipboard by default, and reading that writes nothing");
            c.ClipboardFiles = false; c.Validate(false); JsonFiles.Write(c.ConfigPath, c.Data);
            Check(!VmConfig.Load(c.ConfigPath).ClipboardFiles, "switching it off round trips");
            var bad = VmConfig.Load(c.ConfigPath); bad.Data["clipboard_files"] = "yes";
            bool refused = false; try { bad.Validate(false); } catch (InvalidDataException) { refused = true; }
            Check(refused, "a value that is neither true nor false is refused");
            var editing = VmConfig.Load(c.ConfigPath); editing.Cpus = 16;
            await Integration.ApplyClipboardFiles(editing, true);
            Check(VmConfig.Load(c.ConfigPath) is { ClipboardFiles: true, Cpus: 8 }, "storing it leaves an unsaved edit of another field out of the file");
            Check(ShareConfig.Problem([new ShareConfig("weims transfer", @"D:\x", false)]) != null && ShareConfig.SuggestName(@"D:\Weims Transfer", []) == "Weims Transfer 2", "no folder of the user's can have the transfer share's name, and none is suggested it");

            var status = new BrokerStatus(Environment.ProcessId);
            status.Update(s => { s.Port = 46410; s.Note = "Fetching the files copied in macOS: 3 of 9 MiB"; });
            status.Write(Integration.StatePath(c), _ => { });
            Check(BrokerState.Read(c) is { Online: true, Note: "Fetching the files copied in macOS: 3 of 9 MiB" }, "what the broker says about a transfer reaches Manager through the state file");
        }

        // ---- Folders: copies come from two of them; a third must stay out of reach ----
        var root = Path.Combine(temp, "files"); var source = Path.Combine(root, "source"); var other = Path.Combine(root, "other"); var outside = Path.Combine(root, "outside"); var place = Path.Combine(root, "transfers");
        foreach (var d in new[] { Path.Combine(source, "folder"), other, outside, Path.Combine(place, "in", "old") }) Directory.CreateDirectory(d);
        foreach (var (file, text) in new[] { (Path.Combine(source, "one.txt"), "one"), (Path.Combine(source, "two.txt"), "two"), (Path.Combine(source, "secret.txt"), "secret"), (Path.Combine(source, "folder", "inner.txt"), "inner"),
            (Path.Combine(other, "third.txt"), "third"), (Path.Combine(outside, "far.txt"), "far"), (Path.Combine(place, "in", "old", "left.txt"), "left") }) File.WriteAllText(file, text);
        Shares.Junction(Path.Combine(source, "folder", "escape"), outside);
        try { await Checks(Check, root, source, other, place); }
        finally { try { Directory.Delete(Path.Combine(source, "folder", "escape")); } catch (IOException) { } }

        Rules(Check);
    }

    static async Task Checks(Action<bool, string> Check, string root, string source, string other, string place)
    {
        var logged = new List<string>(); void Log(string line) { lock (logged) logged.Add(line); }
        var limit = new RateLimit(0);
        var transfer = new FileTransfer(place, limit, Log);
        var store = transfer.Store;
        string In(params string[] parts) => Path.Combine([place, "in", .. parts]);

        // ---- Windows to macOS: copied files are read where they are ----
        Check(!Directory.Exists(In("old")) && Directory.Exists(In()) && List(store, "").SequenceEqual(["in"]), "what an earlier run left in the transfer folder is removed, and the share's top shows the inbox alone");
        var offer = transfer.Offer([Path.Combine(source, "one.txt"), Path.Combine(source, "folder"), Path.Combine(other, "third.txt"), @"\\server\share\x.txt", Path.GetPathRoot(source)!]);
        Check(offer is { LeftOut: 2 } && offer.Value.Items.SequenceEqual(["1/one.txt", "1/folder", "2/third.txt"]), "copied files are offered under a number for each folder they came from; a network path and a whole drive are left out");
        Check(List(store, "").SequenceEqual(["1", "2", "in"]) && List(store, "1").SequenceEqual(["folder", "one.txt"]) && List(store, "2").SequenceEqual(["third.txt"]), "a number lists the copied files and not what lies beside them");
        Check(Read(store, @"1\one.txt") == "one" && Read(store, @"\1\folder\inner.txt") == "inner" && Read(store, @"2\third.txt") == "third" && List(store, @"1\folder").SequenceEqual(["escape", "inner.txt"]), "a copied file is read, and a copied folder with what is in it");
        Check(Opens(store, @"1\secret.txt") == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND && Opens(store, @"1\two.txt") == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND && Opens(store, @"2\one.txt") == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            && Opens(store, @"1\secret.txt:stream") == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, "a file beside the copied ones is not there for the guest, by its name or by a stream of it");
        Check(Opens(store, @"1\folder\escape\far.txt") == NTStatus.STATUS_ACCESS_DENIED && logged.Any(l => l.Contains("leads out of the shared folder")), "a link in a copied folder that leads out of where the copy came from is refused");
        Check(Opens(store, @"7\one.txt") == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND && Opens(store, "7", true) == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND && Opens(store, "1:stream") == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, "a number that was never given is not there");
        Check(Write(store, @"1\new.txt", [1]) == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND && Write(store, @"1\folder\new.txt", [1]) == NTStatus.STATUS_ACCESS_DENIED && Write(store, @"1\one.txt", [1]) == NTStatus.STATUS_ACCESS_DENIED
            && Write(store, "top.txt", [1]) == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND && !File.Exists(Path.Combine(source, "new.txt")) && !File.Exists(Path.Combine(source, "folder", "new.txt"))
            && File.ReadAllText(Path.Combine(source, "one.txt")) == "one" && !File.Exists(Path.Combine(place, "top.txt")), "nothing can be made or changed among the copied files, in a copied folder or at the top");
        Check(Shares.Open(store, @"1\one.txt", Shares.Reading, CreateDisposition.FILE_OPEN, out var held) == NTStatus.STATUS_SUCCESS && store.WriteFile(out _, held, 0, [1]) == NTStatus.STATUS_ACCESS_DENIED
            && store.SetFileInformation(held, new FileRenameInformationType2 { FileName = @"\in\taken.txt" }) == NTStatus.STATUS_NOT_SUPPORTED && store.SetFileInformation(held, new FileDispositionInformation { DeletePending = true }) == NTStatus.STATUS_ACCESS_DENIED,
            "an open copied file takes no write, no new name and no delete");
        Check(store.NotifyChange(out _, held, NotifyChangeFilter.FileName, false, 4096, (_, _, _) => { }, null!) == NTStatus.STATUS_NOT_SUPPORTED, "the share offers no watching: a watch on a number would tell of the files beside the copied ones");
        // macOS sends control requests that name no file when it connects; the server hands them on with no handle. This once ended the broker.
        Check(store.DeviceIOControl(null!, 0x00140204 /* validate negotiate info */, [], out _, 64) == NTStatus.STATUS_NOT_SUPPORTED && store.DeviceIOControl(null!, 0x001401FC /* query network interfaces */, [], out _, 64) == NTStatus.STATUS_NOT_SUPPORTED
            && store.ReadFile(out _, null!, 0, 10) == NTStatus.STATUS_INVALID_HANDLE && store.CloseFile("not a handle") == NTStatus.STATUS_INVALID_HANDLE && store.QueryDirectory(out _, null!, "*", FileInformationClass.FileDirectoryInformation) == NTStatus.STATUS_INVALID_HANDLE,
            "a request without a handle of this store is answered, not thrown at");

        var two = new[] { Path.Combine(source, "two.txt") };
        transfer.Offer(two); transfer.Offer(two);
        Check(Read(store, @"1\one.txt") == "one", "the copy before the last two is still readable");
        Check(transfer.Offer(two) is { Items: ["5/two.txt"] } && Opens(store, @"1\one.txt") == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND && Opens(store, @"2\third.txt") == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND && !List(store, "").Contains("1")
            && store.ReadFile(out var still, held, 0, 10) == NTStatus.STATUS_SUCCESS && Encoding.UTF8.GetString(still) == "one", "the fourth copy lets go of the first: nothing new opens there, and what is open is read to its end");
        store.CloseFile(held);
        Check(transfer.Offer([@"\\server\share\x.txt"]) == null && transfer.Offer([Path.Combine(root, "nowhere", "x.txt")]) == null && Read(store, @"5\two.txt") == "two", "a copy with nothing that can be offered is no offer and lets go of nothing");

        // ---- macOS to Windows: the guest fills a place in the inbox ----
        Check(Write(store, @"in\early.bin", new byte[16]) == NTStatus.STATUS_DISK_FULL && !File.Exists(In("early.bin")), "the inbox takes nothing while nothing is expected, not even an empty file");
        var room = transfer.Receive();
        Check(room is { Limit: FileTransfer.InboundLimit } && Directory.Exists(In(room.Value.Id)), "making room gives a place for this one time and says how much may come");
        var id = room!.Value.Id;
        Check(Shares.Open(store, $@"in\{id}\Folder", Shares.Writing, CreateDisposition.FILE_CREATE, out var made, CreateOptions.FILE_DIRECTORY_FILE) == NTStatus.STATUS_SUCCESS && store.CloseFile(made) == NTStatus.STATUS_SUCCESS
            && Write(store, $@"in\{id}\Folder\from mac.txt", "from mac"u8.ToArray()) == NTStatus.STATUS_SUCCESS && Write(store, $@"in\{id}\名前.txt", [1, 2, 3]) == NTStatus.STATUS_SUCCESS, "the guest writes files and folders into its place");
        Check(Shares.Open(store, $@"in\{id}\名前.txt", Shares.Writing, CreateDisposition.FILE_OPEN, out var sized) == NTStatus.STATUS_SUCCESS && store.SetFileInformation(sized, new FileEndOfFileInformation { EndOfFile = FileTransfer.InboundLimit * 2 }) == NTStatus.STATUS_DISK_FULL
            && store.SetFileInformation(sized, new FileEndOfFileInformation { EndOfFile = 3 }) == NTStatus.STATUS_SUCCESS, "a file cannot be given a size beyond what may come");
        store.CloseFile(sized);
        var delivered = transfer.Received(id);
        Check(delivered.Order().SequenceEqual(new[] { In(id, "Folder"), In(id, "名前.txt") }.Order()) && File.ReadAllText(In(id, "Folder", "from mac.txt")) == "from mac", "what arrived is named by full paths, the top level only");
        Check(Write(store, $@"in\{id}\late.bin", new byte[16]) == NTStatus.STATUS_DISK_FULL && !File.Exists(In(id, "late.bin")), "once it was taken, the place takes nothing more");

        var second = transfer.Receive()!.Value.Id;
        Check(Directory.Exists(In(id)), "the last delivery stays while the next one is under way");
        Write(store, $@"in\{second}\half.bin", new byte[100]);
        transfer.Discard(second);
        Check(!Directory.Exists(In(second)) && Directory.Exists(In(id)), "a transfer that was given up leaves nothing behind, and the last delivery stays");
        var third = transfer.Receive()!.Value.Id; Write(store, $@"in\{third}\new.txt", [9]);
        Check(transfer.Received(third).Count == 1 && !Directory.Exists(In(id)), "a new delivery replaces the last one");
        var fourth = transfer.Receive()!.Value.Id;
        Check(transfer.Received(fourth).Count == 0 && !Directory.Exists(In(fourth)) && Directory.Exists(In(third)), "a place the guest left empty is no delivery");

        // The size, on a store of its own: the real limit is too large to fill here.
        {
            var small = new Budget(); var folder = Path.Combine(root, "small"); Directory.CreateDirectory(folder);
            var guarded = new GuardedStore(new SMBLibrary.Win32.NTDirectoryFileSystem(folder), new PathGuard(folder), false, limit, Log, small);
            small.Reset(10);
            Check(Write(guarded, "a.bin", new byte[8]) == NTStatus.STATUS_SUCCESS && Write(guarded, "b.bin", new byte[8]) == NTStatus.STATUS_DISK_FULL && Write(guarded, "c.bin", new byte[2]) == NTStatus.STATUS_SUCCESS
                && new FileInfo(Path.Combine(folder, "b.bin")).Length == 0, "writes beyond the size are refused, and what still fits is taken");
        }

        // ---- A fault while a request is handled ends that connection, not the broker ----
        {
            var collection = new SMBShareCollection { new FileSystemShare("Faulty", DispatchProxy.Create<INTFileStore, FaultyStore>()) };
            var server = new OwnPortServer(collection, new GSSProvider(new IndependentNTLMAuthenticationProvider(name => name == "tester" ? "secret" : null)));
            int port = 0;
            for (int candidate = Integration.PortBase + 50; port == 0 && candidate < Integration.PortBase + 60; candidate++) { try { server.Start(candidate); port = candidate; } catch (SocketException) { server.Stop(); } }
            var client = new Shares.PortClient();
            bool connected = port != 0 && client.ConnectTo(port) && client.Login("", "tester", "secret") == NTStatus.STATUS_SUCCESS;
            var tree = connected ? client.TreeConnect("Faulty", out _) : null;
            NTStatus? opened = null;
            try { if (tree != null) opened = Shares.Open(tree, "anything.txt", Shares.Reading, CreateDisposition.FILE_OPEN, out _); } catch (Exception e) when (e is IOException or InvalidOperationException or SocketException or ObjectDisposedException) { }
            var another = new Shares.PortClient();
            Check(tree != null && opened != NTStatus.STATUS_SUCCESS && another.ConnectTo(port) && another.Login("", "tester", "secret") == NTStatus.STATUS_SUCCESS, "an exception while a request is handled closes that connection, and the server takes the next one");
            another.Disconnect(); server.Stop();
        }

        // ---- Served beside the shared folders, to a helper that knows what it is ----
        {
            var status = new BrokerStatus(Environment.ProcessId);
            var server = new ShareServer(status, limit, Log, () => Integration.PortBase + 40, store) { Grace = TimeSpan.FromMilliseconds(400) };
            (HelperSession Session, MemoryStream Sent) Helper(params string[] capabilities) { var sent = new MemoryStream(); return (new HelperSession(sent, "tester", "0.3.0", capabilities.ToHashSet()), sent); }
            async Task<Frame> Last(MemoryStream sent)
            {
                byte[] bytes; lock (sent) bytes = sent.ToArray();
                var reader = new MemoryStream(bytes); Frame? frame = null;
                while (reader.Position < reader.Length) frame = await Frames.Read(reader, 1 << 20, default);
                return frame ?? throw new Exception("FAIL: the helper was sent nothing");
            }
            server.Configure([], true);
            var old = Helper("clipboard-text", ShareServer.Capability);
            server.HelperConnected(old.Session);
            var told = await Last(old.Sent);
            Check(told.Type == "shares" && told.Header["shares"]!.AsArray().Count == 0 && told.Text("password") == null, "a helper from before file copy and paste is told of no share, and is given no password");
            server.HelperDisconnected(old.Session);

            var helper = Helper("clipboard-text", ShareServer.Capability, FileTransfer.Capability);
            server.HelperConnected(helper.Session);
            var offered = await Last(helper.Sent);
            int port = (int)(offered.Number("port") ?? 0); string user = offered.Text("user") ?? "", password = offered.Text("password") ?? "";
            Check(offered.Header["shares"]!.AsArray() is [JsonObject entry] && entry["name"]!.GetValue<string>() == ShareConfig.TransferName && entry["hidden"]!.GetValue<bool>() && !entry["read_only"]!.GetValue<bool>() && Integration.InRange(port),
                "a helper that can is offered the transfer share, to be kept out of sight, though no folder is shared");
            Check(status.Snapshot()["shares"]!.AsArray().Count == 0 && status.Snapshot()["share_port"]!.GetValue<int>() == port, "it is served, and is not among the folders Manager shows");

            var client = new Shares.PortClient();
            Check(client.ConnectTo(port) && client.Login("", user, password) == NTStatus.STATUS_SUCCESS, "the name and password log on");
            var share = client.TreeConnect(ShareConfig.TransferName, out var treeStatus);
            var copied = transfer.Offer([Path.Combine(source, "one.txt"), Path.Combine(source, "folder")])!.Value.Items.Select(i => i.Replace('/', '\\')).ToArray();
            Check(treeStatus == NTStatus.STATUS_SUCCESS && Read(share, copied[0]) == "one" && Read(share, copied[1] + @"\inner.txt") == "inner", "macOS's stand-in reads a copied file and one in a copied folder through the share");
            Check(Shares.Open(share, copied[0], Shares.Writing, CreateDisposition.FILE_OPEN, out _) == NTStatus.STATUS_ACCESS_DENIED && Shares.Open(share, copied[1] + @"\planted.txt", Shares.Writing, CreateDisposition.FILE_CREATE, out _) == NTStatus.STATUS_ACCESS_DENIED
                && !File.Exists(Path.Combine(source, "folder", "planted.txt")), "write access to the copied files is refused before Windows is asked");
            var into = transfer.Receive()!.Value.Id; var payload = new byte[300 * 1024]; Random.Shared.NextBytes(payload);
            Check(Write(share, $@"in\{into}\via smb.bin", payload) == NTStatus.STATUS_SUCCESS && File.ReadAllBytes(In(into, "via smb.bin")).AsSpan().SequenceEqual(payload), "a file written into the inbox through the share arrives intact");
            Check(Shares.Open(share, "in", Shares.Writing, CreateDisposition.FILE_OPEN, out _, CreateOptions.FILE_DIRECTORY_FILE) == NTStatus.STATUS_ACCESS_DENIED, "the inbox itself is not the guest's to change");
            transfer.Discard(into);

            server.Configure([], false);
            Check((await Last(helper.Sent)).Header["shares"]!.AsArray().Count == 0 && status.Snapshot()["share_port"]!.GetValue<int>() == 0, "with the setting off, the share is withdrawn and nothing is served");
            server.Configure([new ShareConfig("Work", source, true)], true);
            var both = (await Last(helper.Sent)).Header["shares"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToArray();
            Check(both.SequenceEqual(["Work", ShareConfig.TransferName]), "with a shared folder and the setting on, both are offered");
            server.Stop();
        }

        // A file the guest marked read-only does not keep the folder from being emptied.
        File.SetAttributes(In(third, "new.txt"), System.IO.FileAttributes.ReadOnly);
        transfer.Clear();
        Check(!Directory.Exists(place), "when the broker stops, nothing of the guest's stays behind");
    }

    /// <summary>When files cross, against stand-ins for the clipboard, the helper and the transfer folder.</summary>
    static void Rules(Action<bool, string> Check)
    {
        var time = DateTime.UtcNow; string mode = "text"; string? note = null; bool on = true;
        var host = new FakeHost(); var link = new FakeLink(); var exchange = new FakeExchange();
        link.Can.Add(FileTransfer.Capability);
        var sync = new ClipboardSync(host, () => mode, n => note = n, () => time, exchange, () => on);
        string Types() => string.Join(",", link.Sent.Select(s => s.Header["type"]!.GetValue<string>()));
        Frame Guest(string type, JsonObject fields) { fields["type"] = type; return new Frame(fields, default); }
        var copied = new ClipContent("", false) { Files = [@"D:\Work\a.txt", @"D:\Work\目录"] };
        long Blur() { sync.FocusChanged(false); return link.Sent.Last(s => s.Header["type"]!.GetValue<string>() == "clipboard_request").Header["id"]!.GetValue<long>(); }

        // Windows to macOS.
        host.Content = copied;
        sync.Start(hostHasForeignContent: true, windowFocused: false);
        sync.HelperConnected(link);
        Check(Types() == "focus" && exchange.Offers.Count == 0, "files copied in Windows are not offered while the VM's window is in the background");
        sync.FocusChanged(true);
        var sent = link.Sent.Last();
        Check(Types() == "focus,focus,clipboard" && sent.Header["kind"]!.GetValue<string>() == "files" && sent.Header["share"]!.GetValue<string>() == ShareConfig.TransferName && Encoding.UTF8.GetString(sent.Body) == "1/a.txt\n1/目录"
            && exchange.Offers is [var offered] && offered.SequenceEqual(copied.Files) && note == null, "coming to the foreground, the guest is told where it can read them: the share, and one place to a line");
        exchange.Answer = (new[] { "2/a.txt" }, 3); sync.HostChanged(false);
        Check(link.Sent.Count == 4 && note != null && note.Contains('3'), "when some of them cannot be offered, the rest are and the status says how many are not");
        exchange.Answer = null; sync.HostChanged(false);
        Check(link.Sent.Count == 4 && note != null, "when none can, nothing is sent and the status says so");
        exchange.Answer = (new[] { "3/a.txt" }, 0);

        on = false; int offers = exchange.Offers.Count; sync.HostChanged(false);
        Check(link.Sent.Count == 4 && exchange.Offers.Count == offers, "with the setting off, copied files are not offered");
        on = true; link.Can.Remove(FileTransfer.Capability); sync.HostChanged(false);
        Check(link.Sent.Count == 4 && exchange.Offers.Count == offers, "nor to a helper from before file copy and paste");
        link.Can.Add(FileTransfer.Capability);
        host.Content = new ClipContent("plain text", false); sync.HostChanged(false);
        Check(link.Sent.Last().Header["kind"]!.GetValue<string>() == "text", "text still crosses as before");

        // macOS to Windows.
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 2 }));
        Check(exchange.Places == 0, "files copied in macOS are not fetched while the VM's window is in front");
        long request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 2, ["reply_to"] = request }));
        sent = link.Sent.Last();
        Check(sent.Header["type"]!.GetValue<string>() == "files_fetch" && sent.Header["id"]!.GetValue<string>() == "1" && sent.Header["limit"]!.GetValue<long>() == 1000 && sent.Header["share"]!.GetValue<string>() == ShareConfig.TransferName && note != null,
            "leaving the window with files on the guest's pasteboard asks the helper to bring them, to a place and up to a size");
        sync.FromGuest(link, Guest("files_progress", new JsonObject { ["id"] = "1", ["done"] = 3L << 20, ["total"] = 9L << 20 }));
        Check(note != null && note.Contains('3') && note.Contains('9'), "the status says how far it is");
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "7", ["ok"] = true }));
        Check(host.Writes.Count == 0, "an end of some other transfer is not this one's");
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "1", ["ok"] = true }));
        Check(host.Writes is [{ Files: [@"C:\vm\transfers\in\1\from mac.txt"] }] && note == null, "when they are all here they are put on the Windows clipboard");
        sync.HostChanged(true);
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "1", ["ok"] = true }));
        Check(host.Writes.Count == 1, "and only once");
        int count = link.Sent.Count; sync.FocusChanged(true);
        Check(link.Sent.Count == count + 1, "files that came from this VM are not offered back when its window returns");

        request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        host.Content = new ClipContent("copied in Windows meanwhile", false); sync.HostChanged(false);
        Check(link.Sent.Last().Header["type"]!.GetValue<string>() == "files_cancel" && link.Sent.Last().Header["id"]!.GetValue<string>() == "2" && exchange.Discarded.SequenceEqual(["2"]) && note == null, "a copy made in Windows while they are fetched calls the fetch off");
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "2", ["ok"] = true }));
        Check(host.Writes.Count == 1, "and what still arrives is not put over it");

        sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "3", ["ok"] = false, ["reason"] = "too-large" }));
        Check(host.Writes.Count == 1 && exchange.Discarded.Last() == "3" && note != null && note.Contains("256"), "files over the size are not fetched, and the status says what the size is");
        sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        sync.FromGuest(link, Guest("files_done", new JsonObject { ["id"] = "4", ["ok"] = false, ["reason"] = "the disk is\nfull" }));
        Check(note != null && note.Contains("the disk isfull"), "another failure is told with the helper's reason, without control characters");

        sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, new Frame(new JsonObject { ["type"] = "clipboard", ["kind"] = "text", ["reply_to"] = request }, "now text"u8.ToArray()));
        Check(exchange.Discarded.Last() == "5" && host.Writes.Last().Text == "now text", "text on the guest's pasteboard replaces a fetch that is under way");

        sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        sync.HelperDisconnected(link);
        Check(exchange.Discarded.Last() == "6" && note == null, "a helper that leaves in the middle leaves nothing behind");

        sync.HelperConnected(link); sync.FocusChanged(true); request = Blur(); exchange.NoRoom = true;
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        Check(link.Sent.Last().Header["type"]!.GetValue<string>() != "files_fetch" && note != null, "without a place to put them, nothing is asked for and the status says so");
        exchange.NoRoom = false; on = false; sync.FocusChanged(true); request = Blur();
        sync.FromGuest(link, Guest("clipboard", new JsonObject { ["kind"] = "files", ["count"] = 1, ["reply_to"] = request }));
        Check(link.Sent.Last().Header["type"]!.GetValue<string>() != "files_fetch" && exchange.Places == 6, "with the setting off, files copied in macOS are not fetched");
    }
}
