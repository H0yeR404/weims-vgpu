// SPDX-License-Identifier: GPL-2.0-or-later
// Shared folders: the upload limit, the path guard, the guarded file store, and the share server from end to
// end with the SMB library's own client standing in for macOS. No guest is involved.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.Win32;
using Weims.Broker;
using Weims.Core;
using SmbAttributes = SMBLibrary.FileAttributes;

static class Shares
{
    /// <summary>The library's client on a port of ours, as the server is.</summary>
    internal sealed class PortClient : SMB2Client
    {
        public bool ConnectTo(int port) => Connect(IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port);
    }

    internal static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("FAIL: could not make a junction: " + p.StandardError.ReadToEnd());
    }

    internal const AccessMask Reading = AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, Writing = AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE;
    internal static NTStatus Open(INTFileStore store, string path, AccessMask access, CreateDisposition disposition, out object handle, CreateOptions options = CreateOptions.FILE_NON_DIRECTORY_FILE)
        => store.CreateFile(out handle, out _, path, access, SmbAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, disposition, options | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null!);

    public static async Task Run(Action<bool, string> Check, Func<Func<bool>, int, Task<bool>> Eventually, string temp)
    {
        // ---- The upload limit, on a clock of its own ----
        {
            var now = TimeSpan.Zero; var waited = TimeSpan.Zero; int waits = 0;
            var limit = new RateLimit(1024 * 1024, () => now, pause => { waited += pause; now += pause; waits++; });
            limit.Spend(512 * 1024);
            Check(waits == 1 && Math.Abs(waited.TotalSeconds - 0.5) < 0.001, "half a second's worth of bytes waits half a second");
            now += TimeSpan.FromSeconds(10); waited = TimeSpan.Zero; waits = 0;
            limit.Spend(4096);
            Check(waits == 0, "a small piece after a pause passes at once");
            for (int i = 0; i < 1023; i++) limit.Spend(4096);
            Check(Math.Abs(waited.TotalSeconds - 3.75) < 0.03 && waits < 1024, "many small pieces add up to the same rate, less the quarter second of credit, in fewer and longer waits");
            limit.BytesPerSecond = 0; waits = 0;
            limit.Spend(64 * 1024 * 1024);
            Check(waits == 0, "a limit of 0 never waits");
            limit.BytesPerSecond = 8 * 1024 * 1024; waited = TimeSpan.Zero;
            limit.Spend(8 * 1024 * 1024);
            Check(Math.Abs(waited.TotalSeconds - 1) < 0.001, "a changed limit applies to the next piece, and nothing is owed from the time without one");
        }

        // ---- The configuration ----
        {
            VmConfig New(string name)
            {
                var state = Path.Combine(temp, name, "state"); Directory.CreateDirectory(state);
                var c = new VmConfig(Path.Combine(state, "config.json"), new JsonObject { ["mac"] = "52:54:00:12:34:56" });
                foreach (var key in new[] { "active_disk", "opencore", "firmware_code", "firmware_vars", "rom" }) c.Data[key] = @"C:\vm\" + key;
                JsonFiles.Write(c.ConfigPath, c.Data); return c;
            }
            var c = New("share-config"); int keys = c.Data.Count;
            Check(c.Shares.Count == 0 && c.UploadLimitMiB == 0 && c.Data.Count == keys, "no folder is shared by default, uploads are not limited, and reading that writes nothing");
            c.Shares = [new ShareConfig("Work", @"D:\Work", false), new ShareConfig("书 Library", @"D:\Books", true)]; c.UploadLimitMiB = 30;
            c.Validate(false); JsonFiles.Write(c.ConfigPath, c.Data);
            var loaded = VmConfig.Load(c.ConfigPath);
            Check(loaded.Shares.SequenceEqual(c.Shares) && loaded.UploadLimitMiB == 30, "shared folders and the limit round trip");
            loaded.Data["shares"]!.AsArray()[0]!.AsObject().Remove("read_only");
            Check(loaded.Shares[0].ReadOnly, "a folder written without saying so is read-only");

            Check(new[] { "Work", "Work Files", "书", "a-b_c.d (2)", new string('x', 32) }.All(n => ShareConfig.NameProblem(n) == null), "letters of any script, digits, spaces and a few marks make a share name");
            Check(new[] { "", " Work", "Work ", ".hidden", "ends.", "a/b", @"a\b", "a:b", "IPC$", "a*b", "tab\there", new string('x', 33) }.All(n => ShareConfig.NameProblem(n) != null), "other characters, edges of dots and spaces, and more than 32 characters do not");
            Check(new[] { @"D:\Work", @"D:\", @"C:\Users\someone\Documents" }.All(p => ShareConfig.PathProblem(p) == null), "a folder on a drive, or a whole drive, can be shared");
            Check(new[] { "", "Work", @"\Work", @"\\server\share", @"D:Work", @"D:\Work\..\Other", @"D:\Work\.", "D:/Work" }.All(p => ShareConfig.PathProblem(p) != null), "a path that is not a full local one cannot");
            Check(ShareConfig.Problem([new("Work", @"D:\a", false), new("work", @"D:\b", false)])!.Contains("twice") || ShareConfig.Problem([new("Work", @"D:\a", false), new("work", @"D:\b", false)])!.Contains("重复"), "two folders cannot share a name, whatever the case");
            Check(ShareConfig.Problem([.. Enumerable.Range(0, 17).Select(i => new ShareConfig("s" + i, @"D:\s" + i, false))]) != null && ShareConfig.Problem([.. Enumerable.Range(0, 16).Select(i => new ShareConfig("s" + i, @"D:\s" + i, false))]) == null, "sixteen folders at most");
            Check(ShareConfig.SuggestName(@"D:\Work Files\Projects", []) == "Projects" && ShareConfig.SuggestName(@"D:\Work Files\Projects\", ["projects"]) == "Projects 2" && ShareConfig.SuggestName(@"D:\", []) == "D"
                && ShareConfig.SuggestName(@"D:\a+b=c", []) == "a-b-c" && ShareConfig.SuggestName(@"D:\.config", []) == "config" && ShareConfig.SuggestName(@"D:\" + new string('y', 60), ["x"]) is { Length: 32 } longName && ShareConfig.NameProblem(longName) == null,
                "a folder's own name is suggested, made valid and unique");

            bool Refused(Action<VmConfig> spoil) { var bad = VmConfig.Load(c.ConfigPath); spoil(bad); try { bad.Validate(false); return false; } catch (InvalidDataException) { return true; } }
            Check(Refused(b => b.Data["shares"] = "D:\\Work") && Refused(b => b.Data["shares"] = new JsonArray("D:\\Work")) && Refused(b => b.Data["shares"] = new JsonArray(new JsonObject { ["name"] = "a/b", ["path"] = @"D:\Work" }))
                && Refused(b => b.Data["shares"] = new JsonArray(new JsonObject { ["name"] = "Work", ["path"] = @"..\Work" })), "a configuration whose shared folders cannot be read or are not valid is refused");
            Check(Refused(b => b.UploadLimitMiB = -1) && Refused(b => b.UploadLimitMiB = 1025) && !Refused(b => b.UploadLimitMiB = 0), "the upload limit is 0 to 1024 MiB/s");

            // Storing them touches nothing else and needs no broker.
            var editing = VmConfig.Load(c.ConfigPath); editing.Cpus = 16;
            await Integration.ApplyShares(editing, [new ShareConfig("Only", @"D:\Only", true)], 5);
            var stored = VmConfig.Load(c.ConfigPath);
            Check(stored.Shares is [{ Name: "Only", ReadOnly: true }] && stored.UploadLimitMiB == 5 && stored.Cpus == 8 && editing.Cpus == 16, "storing the folders and the limit leaves an unsaved edit of another field out of the file");
            bool kept = false; try { await Integration.ApplyShares(editing, [new ShareConfig("bad:name", @"D:\x", false)], 5); } catch (InvalidDataException) { kept = editing.Shares is [{ Name: "Only" }] && VmConfig.Load(c.ConfigPath).Shares is [{ Name: "Only" }]; }
            Check(kept, "a list that is not valid is neither stored nor kept in memory");

            // What Manager reads back from the broker's state.
            var status = new BrokerStatus(Environment.ProcessId);
            status.Update(s => { s.Port = 46410; s.HelperUser = "hoyer"; s.HelperMounts = true; s.SharePort = 46411; s.Shares = [new ShareState("Only", true, "mounted", null), new ShareState("Gone", false, "offline", "missing")]; });
            status.Write(Integration.StatePath(c), _ => { });
            var read = BrokerState.Read(c);
            Check(read.Online && read.HelperMounts && read.Shares.SequenceEqual([new ShareState("Only", true, "mounted", null), new ShareState("Gone", false, "offline", "missing")]), "the folders' states reach Manager through the state file");
        }

        // ---- Folders: one shared, one beside it that must stay out of reach ----
        var root = Path.Combine(temp, "shares"); var shared = Path.Combine(root, "shared"); var outside = Path.Combine(root, "outside"); var gone = Path.Combine(root, "gone");
        foreach (var d in new[] { Path.Combine(shared, "inside"), outside, gone }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(shared, "inside", "file.txt"), "inside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Junction(Path.Combine(shared, "escape"), outside);
        Junction(Path.Combine(shared, "loop"), Path.Combine(shared, "inside"));
        Junction(Path.Combine(shared, "dangling"), gone);
        Directory.Delete(gone);
        try { await Checks(Check, Eventually, root, shared, outside, gone); }
        // A junction is removed as the link it is; removing the tree around it would follow it or stumble over it.
        finally { foreach (var link in new[] { "escape", "loop", "dangling" }) try { Directory.Delete(Path.Combine(shared, link)); } catch (IOException) { } }
    }

    static async Task Checks(Action<bool, string> Check, Func<Func<bool>, int, Task<bool>> Eventually, string root, string shared, string outside, string gone)
    {

        // ---- The path guard ----
        {
            var guard = new PathGuard(shared);
            NTStatus? Ask(string path) => guard.Refusal(path, out _);
            Check(Ask(@"inside\file.txt") == null && Ask(@"\inside\file.txt") == null && Ask("") == null && Ask(@"\") == null, "a file in the folder and the folder itself may be opened");
            Check(Ask(@"inside\new.txt") == null && Ask(@"new\deeper\file.txt") == null, "so may what does not exist yet below it");
            Check(Ask(@"escape\secret.txt") == NTStatus.STATUS_ACCESS_DENIED && Ask("escape") == NTStatus.STATUS_ACCESS_DENIED, "a junction that leads out of the folder is refused, and so is what lies behind it");
            Check(Ask(@"escape\new.txt") == NTStatus.STATUS_ACCESS_DENIED && Ask(@"escape\new\deeper.txt") == NTStatus.STATUS_ACCESS_DENIED, "nothing can be created behind it either");
            Check(Ask(@"loop\file.txt") == null, "a junction that ends inside the folder is followed");
            Check(Ask("dangling") == NTStatus.STATUS_ACCESS_DENIED && Ask(@"dangling\x.txt") == NTStatus.STATUS_ACCESS_DENIED, "a link whose target is missing is refused: a file created through it would land there");
            Check(Ask(@"..\outside\secret.txt") == NTStatus.STATUS_OBJECT_NAME_INVALID && Ask(@"inside\..\..\outside") == NTStatus.STATUS_OBJECT_NAME_INVALID && Ask(@"inside\.\file.txt") == NTStatus.STATUS_OBJECT_NAME_INVALID, "dot and dot-dot are not names");
            Check(Ask(@"inside\file.txt:meta") == null && Ask(@"inside\file.txt:meta:$DATA") == null && Ask(":rootstream") == null, "a stream of a file is the file's");
            Check(Ask(@"escape:x") == NTStatus.STATUS_ACCESS_DENIED && Ask(@"in:side\file.txt") == NTStatus.STATUS_OBJECT_NAME_INVALID && Ask("inside/file.txt") == NTStatus.STATUS_OBJECT_NAME_INVALID, "a stream does not get around a link, and a colon or a slash elsewhere is no name");
            guard.Refusal(@"escape\secret.txt", out var why);
            Check(why != null && !why.Contains(outside, StringComparison.OrdinalIgnoreCase), "a refusal comes with a reason that does not tell where the link points");
            bool threw = false; try { _ = new PathGuard(gone); } catch (DirectoryNotFoundException) { threw = true; }
            Check(threw, "a folder that is not there cannot be guarded");
            var viaJunction = new PathGuard(Path.Combine(shared, "loop"));
            Check(viaJunction.Refusal("file.txt", out _) == null, "a shared folder that is itself a junction is judged by where it really is");
        }

        // ---- The guarded store over the library's Windows store ----
        {
            var logged = new List<string>();
            var limit = new RateLimit(0);
            var store = new GuardedStore(new NTDirectoryFileSystem(shared), new PathGuard(shared), false, limit, logged.Add);
            Check(Open(store, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out var h) == NTStatus.STATUS_SUCCESS && store.ReadFile(out var data, h, 0, 100) == NTStatus.STATUS_SUCCESS && Encoding.UTF8.GetString(data) == "inside", "the store reads a file in the folder");
            store.CloseFile(h);
            Check(Open(store, @"escape\secret.txt", Reading, CreateDisposition.FILE_OPEN, out _) == NTStatus.STATUS_ACCESS_DENIED, "it does not read through a junction that leads out");
            Check(Open(store, @"escape\planted.txt", Writing, CreateDisposition.FILE_CREATE, out _) == NTStatus.STATUS_ACCESS_DENIED && !File.Exists(Path.Combine(outside, "planted.txt")), "and creates nothing out there");
            Check(logged.Count == 2 && logged.All(l => l.Contains("leads out of the shared folder")), "both refusals are in the log");

            Check(Open(store, @"inside\new.txt", Writing, CreateDisposition.FILE_CREATE, out h) == NTStatus.STATUS_SUCCESS && store.WriteFile(out int written, h, 0, "new"u8.ToArray()) == NTStatus.STATUS_SUCCESS && written == 3, "it creates and writes a file in the folder");
            Check(store.SetFileInformation(h, new FileRenameInformationType2 { FileName = @"\escape\moved.txt" }) == NTStatus.STATUS_ACCESS_DENIED && !File.Exists(Path.Combine(outside, "moved.txt")), "a rename cannot move it out through the junction");
            Check(store.SetFileInformation(h, new FileRenameInformationType2 { FileName = @"\inside\renamed.txt" }) == NTStatus.STATUS_SUCCESS, "a rename inside the folder works");
            Check(store.SetFileInformation(h, new FileLinkInformationType2 { FileName = @"\inside\link.txt" }) == NTStatus.STATUS_NOT_SUPPORTED, "hard links are not offered");
            Check(store.DeviceIOControl(h, 0x000900A4 /* set reparse point */, new byte[16], out _, 0) == NTStatus.STATUS_NOT_SUPPORTED && store.DeviceIOControl(h, 0x000900A8 /* get reparse point */, [], out _, 1024) == NTStatus.STATUS_NOT_SUPPORTED, "the guest can neither make a link nor read where one points");
            store.CloseFile(h);
            Check(File.ReadAllText(Path.Combine(shared, "inside", "renamed.txt")) == "new", "the file is where the rename put it");
            Check(Open(store, @"inside\renamed.txt:com.example.note", Writing, CreateDisposition.FILE_OPEN_IF, out h) == NTStatus.STATUS_SUCCESS, "a stream of a file can be opened: that is where macOS keeps extended attributes");
            store.CloseFile(h);
            Check(Open(store, "", Reading, CreateDisposition.FILE_OPEN, out h, CreateOptions.FILE_DIRECTORY_FILE) == NTStatus.STATUS_SUCCESS, "the folder itself can be opened");
            store.CloseFile(h);
            Check(store.SetFileSystemInformation(null!) == NTStatus.STATUS_NOT_SUPPORTED, "the volume's own settings are not the guest's to change");

            var readOnly = new GuardedStore(new NTDirectoryFileSystem(shared), new PathGuard(shared), true, limit, logged.Add);
            Check(Open(readOnly, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out h) == NTStatus.STATUS_SUCCESS, "a read-only store opens a file for reading");
            Check(readOnly.WriteFile(out _, h, 0, [1]) == NTStatus.STATUS_ACCESS_DENIED && readOnly.SetFileInformation(h, new FileDispositionInformation { DeletePending = true }) == NTStatus.STATUS_ACCESS_DENIED, "it neither writes nor deletes");
            readOnly.CloseFile(h);
            Check(Open(readOnly, @"inside\made.txt", Reading, CreateDisposition.FILE_OPEN_IF, out _) == NTStatus.STATUS_ACCESS_DENIED && Open(readOnly, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out _, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE) == NTStatus.STATUS_ACCESS_DENIED
                && !File.Exists(Path.Combine(shared, "inside", "made.txt")) && File.Exists(Path.Combine(shared, "inside", "file.txt")), "it creates nothing and takes no delete-on-close");

            // Whether a whole drive can be a share: the library joins the folder and the path with a separator of its own.
            var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
            var whole = new GuardedStore(new NTDirectoryFileSystem(drive), new PathGuard(drive), true, limit, logged.Add);
            var atRoot = Open(whole, Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), Reading, CreateDisposition.FILE_OPEN, out h, CreateOptions.FILE_DIRECTORY_FILE);
            if (atRoot == NTStatus.STATUS_SUCCESS) whole.CloseFile(h);
            Console.WriteLine("INFO a folder opened in a share of a drive's root: " + atRoot);
        }

        // ---- The share server, with the library's client as the guest ----
        {
            var readOnlyFolder = Path.Combine(root, "library"); Directory.CreateDirectory(readOnlyFolder); File.WriteAllText(Path.Combine(readOnlyFolder, "book.txt"), "book");
            var logged = new List<string>(); void Log(string line) { lock (logged) logged.Add(line); }
            var status = new BrokerStatus(Environment.ProcessId);
            var limit = new RateLimit(0);
            var server = new ShareServer(status, limit, Log, () => Integration.PortBase + 20) { Grace = TimeSpan.FromMilliseconds(400) };
            var folders = new[] { new ShareConfig("Work", shared, false), new ShareConfig("Library", readOnlyFolder, true), new ShareConfig("Missing", Path.Combine(root, "nowhere"), false) };
            server.Configure(folders);
            JsonArray States() => status.Snapshot()["shares"]!.AsArray();
            string? State(string name) => States().Select(s => s!).FirstOrDefault(s => s["name"]!.GetValue<string>() == name)?["state"]!.GetValue<string>();
            Check(States().Count == 3 && States().All(s => s!["state"]!.GetValue<string>() == "offline") && status.Snapshot()["share_port"]!.GetValue<int>() == 0, "without a helper nothing is served");

            (HelperSession Session, MemoryStream Sent) Helper(params string[] capabilities) { var sent = new MemoryStream(); return (new HelperSession(sent, "tester", "0.2.0", capabilities.ToHashSet()), sent); }
            async Task<Frame> Last(MemoryStream sent)
            {
                byte[] bytes; lock (sent) bytes = sent.ToArray();
                var reader = new MemoryStream(bytes); Frame? frame = null;
                while (reader.Position < reader.Length) frame = await Frames.Read(reader, 1 << 20, default);
                return frame ?? throw new Exception("FAIL: the helper was sent nothing");
            }

            var old = Helper("clipboard-text");
            server.HelperConnected(old.Session);
            Check(old.Sent.Length == 0 && status.Snapshot()["share_port"]!.GetValue<int>() == 0 && !status.Snapshot()["helper_mounts"]!.GetValue<bool>(), "a helper from before shared folders is told nothing, and nothing is served for it");
            server.HelperDisconnected(old.Session);

            var helper = Helper("clipboard-text", ShareServer.Capability);
            server.HelperConnected(helper.Session);
            var offer = await Last(helper.Sent);
            int port = (int)(offer.Number("port") ?? 0); string user = offer.Text("user") ?? "", password = offer.Text("password") ?? "";
            var offered = offer.Header["shares"]!.AsArray().Select(s => (Name: s!["name"]!.GetValue<string>(), ReadOnly: s["read_only"]!.GetValue<bool>())).ToArray();
            Check(offer.Type == "shares" && Integration.InRange(port) && port != Integration.PortBase + 20 && user.StartsWith("weims-") && password.Length >= 40, "a helper that can mount is told the port, a name and a password");
            Check(offered.SequenceEqual([("Work", false), ("Library", true)]), "and the folders that are there, with which of them are read-only");
            Check(State("Work") == "ready" && State("Library") == "ready" && State("Missing") == "offline" && States()[2]!["error"]!.GetValue<string>().Contains("nowhere") && status.Snapshot()["share_port"]!.GetValue<int>() == port && status.Snapshot()["helper_mounts"]!.GetValue<bool>(),
                "the state names what is served and why a missing folder is not");
            Check(!status.Snapshot().ToJsonString().Contains(password) && !logged.Any(l => l.Contains(password)), "the password is neither in the published state nor in the log");

            var wrong = new PortClient();
            Check(wrong.ConnectTo(port) && wrong.Login("", user, "not-the-password") == NTStatus.STATUS_LOGON_FAILURE && wrong.Login("", "Guest", "") != NTStatus.STATUS_SUCCESS, "a wrong password and a guest logon are refused");
            wrong.Disconnect();

            var client = new PortClient();
            Check(client.ConnectTo(port) && client.Login("", user, password) == NTStatus.STATUS_SUCCESS, "the name and password log on (NTLMv2)");
            var listed = client.ListShares(out var listStatus);
            Check(listStatus == NTStatus.STATUS_SUCCESS && listed.Contains("Work") && listed.Contains("Library") && !listed.Contains("Missing"), "the served folders are listed as shares");
            var work = client.TreeConnect("Work", out var treeStatus);
            Check(treeStatus == NTStatus.STATUS_SUCCESS, "the writable share connects");
            var payload = new byte[256 * 1024]; Random.Shared.NextBytes(payload);
            Check(Open(work, @"from guest.bin", Writing, CreateDisposition.FILE_CREATE, out var h) == NTStatus.STATUS_SUCCESS && work.WriteFile(out int written, h, 0, payload) == NTStatus.STATUS_SUCCESS && written == payload.Length && work.CloseFile(h) == NTStatus.STATUS_SUCCESS
                && File.ReadAllBytes(Path.Combine(shared, "from guest.bin")).AsSpan().SequenceEqual(payload), "a file written through the share arrives intact");
            Check(Open(work, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out h) == NTStatus.STATUS_SUCCESS && work.ReadFile(out var read, h, 0, 100) == NTStatus.STATUS_SUCCESS && Encoding.UTF8.GetString(read) == "inside", "a file is read through it");
            work.CloseFile(h);
            Check(Open(work, @"escape\secret.txt", Reading, CreateDisposition.FILE_OPEN, out _) == NTStatus.STATUS_ACCESS_DENIED && Open(work, @"escape\planted.txt", Writing, CreateDisposition.FILE_CREATE, out _) == NTStatus.STATUS_ACCESS_DENIED && !File.Exists(Path.Combine(outside, "planted.txt")),
                "the junction that leads out is closed to the client too");

            var library = client.TreeConnect("Library", out treeStatus);
            Check(treeStatus == NTStatus.STATUS_SUCCESS && Open(library, "book.txt", Reading, CreateDisposition.FILE_OPEN, out h) == NTStatus.STATUS_SUCCESS && library.ReadFile(out read, h, 0, 100) == NTStatus.STATUS_SUCCESS && Encoding.UTF8.GetString(read) == "book", "the read-only share is read");
            library.CloseFile(h);
            Check(Open(library, "book.txt", Writing, CreateDisposition.FILE_OPEN, out _) == NTStatus.STATUS_ACCESS_DENIED && Open(library, "new.txt", Writing, CreateDisposition.FILE_CREATE, out _) == NTStatus.STATUS_ACCESS_DENIED
                && Open(library, "book.txt", AccessMask.DELETE | AccessMask.SYNCHRONIZE, CreateDisposition.FILE_OPEN, out _) == NTStatus.STATUS_ACCESS_DENIED && File.ReadAllText(Path.Combine(readOnlyFolder, "book.txt")) == "book" && !File.Exists(Path.Combine(readOnlyFolder, "new.txt")),
                "it takes no write, no new file and no delete");
            client.TreeConnect("Missing", out treeStatus);
            Check(treeStatus != NTStatus.STATUS_SUCCESS, "a folder that is not served is no share");

            // The limit, on the real clock: 2 MiB at 2 MiB/s cannot take less than three quarters of a second,
            // a quarter second's worth being credit.
            limit.BytesPerSecond = 2 * 1024 * 1024;
            Check(Open(work, "slow.bin", Writing, CreateDisposition.FILE_CREATE, out h) == NTStatus.STATUS_SUCCESS, "a second file opens");
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 8; i++) work.WriteFile(out _, h, i * (long)payload.Length, payload);
            work.CloseFile(h); clock.Stop();
            Console.WriteLine($"INFO 2 MiB written at a 2 MiB/s limit took {clock.ElapsedMilliseconds} ms");
            Check(clock.ElapsedMilliseconds >= 700 && clock.ElapsedMilliseconds < 4000 && new FileInfo(Path.Combine(shared, "slow.bin")).Length == 8 * payload.Length, "writes are held to the upload limit");
            limit.BytesPerSecond = 0;

            // What the helper reports back.
            server.FromGuest(helper.Session, new Frame(new JsonObject { ["type"] = "shares_state", ["mounted"] = new JsonArray("Work"), ["failed"] = new JsonArray(new JsonObject { ["name"] = "Library", ["error"] = "timed\nout" }) }, default));
            Check(State("Work") == "mounted" && State("Library") == "failed" && States()[1]!["error"]!.GetValue<string>() == "timedout", "the helper's report of its mounts is published, without control characters");
            server.FromGuest(old.Session, new Frame(new JsonObject { ["type"] = "shares_state", ["mounted"] = new JsonArray("Library") }, default));
            Check(State("Library") == "failed", "a report from a helper that is not the connected one changes nothing");

            // A helper that comes back within the grace period finds the same server.
            server.HelperDisconnected(helper.Session);
            Check(State("Work") == "ready", "when the helper leaves, its mounts are no longer claimed");
            var back = Helper(ShareServer.Capability);
            server.HelperConnected(back.Session);
            var again = await Last(back.Sent);
            await Task.Delay(700);
            Check(again.Number("port") == port && again.Text("password") == password && Open(work, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out h) == NTStatus.STATUS_SUCCESS, "a helper that returns in time gets the same port and password, and an open mount keeps working");
            work.CloseFile(h);

            // A changed list is served anew.
            server.Configure([folders[1]]);
            var changed = await Last(back.Sent);
            Check(changed.Text("password") != password && changed.Header["shares"]!.AsArray().Count == 1 && States().Count == 1, "a changed list of folders is announced again, under a new password");
            bool dropped; try { dropped = Open(work, @"inside\file.txt", Reading, CreateDisposition.FILE_OPEN, out _) != NTStatus.STATUS_SUCCESS; } catch (InvalidOperationException) { dropped = true; }
            Check(dropped, "the connection made under the old one is gone");
            server.Configure([new ShareConfig("bad/name", shared, false)]);
            Check(States().Count == 0 && status.Snapshot()["share_problem"]!.GetValue<string>().Contains("bad/name") && (await Last(back.Sent)).Header["shares"]!.AsArray().Count == 0, "a list that is not valid serves nothing and says why");
            server.Configure(folders);
            int reopened = (int)((await Last(back.Sent)).Number("port") ?? 0);

            // No helper for longer than the grace period: the port closes.
            server.HelperDisconnected(back.Session);
            bool Listens(int p) { try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, p); return true; } catch (SocketException) { return false; } }
            Check(Listens(reopened), "the folders stay served for a while after the helper left");
            Check(await Eventually(() => !Listens(reopened) && status.Snapshot()["share_port"]!.GetValue<int>() == 0, 5000), "and stop being served when it stays away");
            server.HelperConnected(back.Session);
            Check(Listens((int)((await Last(back.Sent)).Number("port") ?? 0)), "a helper that connects later is served again");
            server.Stop();
            Check(status.Snapshot()["share_port"]!.GetValue<int>() == 0 && States().All(s => s!["state"]!.GetValue<string>() == "offline"), "stopping the broker stops serving");
        }
    }
}
