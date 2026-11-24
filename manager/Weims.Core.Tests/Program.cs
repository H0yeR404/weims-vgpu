// SPDX-License-Identifier: GPL-2.0-or-later
// Dependency-free contract tests: no VM is started and no user disk is opened.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Weims.Core;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (Exception) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
var temp = Path.Combine(Path.GetTempPath(), "weims-contract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    var profiles = Path.Combine(temp, "profiles");
    var application = Path.Combine(temp, "application"); Directory.CreateDirectory(application);
    var stable = Path.Combine(profiles, "Weims", "Manager");
    var preview = Path.Combine(profiles, "Weims", "Manager-Preview");
    Check(ProfileDirectory.Resolve(null, application, profiles) == stable, "fresh stable install uses stable profile");
    Directory.CreateDirectory(stable);
    Check(ProfileDirectory.Resolve(null, application, profiles) == stable, "existing stable profile retained");
    Directory.CreateDirectory(preview);
    Check(ProfileDirectory.Resolve(null, application, profiles) == preview, "preview promotion keeps preview library and settings");
    File.WriteAllText(Path.Combine(application, "profile-directory.txt"), stable);
    Check(ProfileDirectory.Resolve(null, application, profiles) == stable, "updater profile marker takes precedence for GUI and CLI");
    Check(ProfileDirectory.Resolve(preview, application, profiles) == preview, "explicit profile overrides update marker");
    Check(Path.IsPathFullyQualified(ProfileDirectory.Resolve("relative-profile", application, profiles)), "relative explicit profile normalized");
    File.WriteAllText(Path.Combine(application, "profile-directory.txt"), Path.Combine(temp,"missing"));
    Check(ProfileDirectory.Resolve(null, application, profiles) == preview, "missing updater profile falls back to existing preview");
    File.WriteAllText(Path.Combine(application, "profile-directory.txt"), "relative-marker");
    Check(ProfileDirectory.Resolve(null, application, profiles) == preview, "relative marker is not adopted");
    string FileAt(string name) { var p = Path.Combine(temp, name); File.WriteAllText(p, "test"); return p; }
    var engine = new EngineSettings { Qemu = FileAt("qemu-system-x86_64.exe"), RuntimeBin = temp, Bios = temp };
    FileAt("qemu-img.exe"); FileAt("reims-vgpu-compile.exe");
    var c = new VmConfig(Path.Combine(temp,"vm","state","config.json"), new JsonObject { ["future_field"] = "preserve me" });
    c.Cpus = 8; c.MemoryMiB = 8192; c.Resolution = "1920x1080"; c.Mac = "52:54:00:12:34:56";
    c.QmpPort = 4550; c.SshPort = 2330; c.Disk = FileAt("disk , 测试.qcow2"); c.OpenCore = FileAt("OpenCore.img"); c.FirmwareCode = FileAt("CODE.fd"); c.FirmwareVars = FileAt("VARS.fd"); c.Rom = FileAt("Graphics.rom");
    c.Save(); Check(VmConfig.Load(c.ConfigPath).Data["future_field"]!.GetValue<string>() == "preserve me", "unknown config fields survive save");
    var freePorts = GuidedSetup.FreePorts();
    Check(freePorts.Qmp != freePorts.Ssh && freePorts.Qmp >= 1024 && freePorts.Ssh >= 1024, "guided setup selects distinct nonprivileged ports");
    Reject(() => GuidedSetup.Create("../escape", Path.Combine(temp,"guided"),null,200,engine,temp,new Progress<string>(),default).GetAwaiter().GetResult(), "guided setup rejects path traversal before downloading");
    Reject(() => GuidedSetup.Create("valid", temp,null,200,engine,temp,new Progress<string>(),default).GetAwaiter().GetResult(), "guided setup refuses existing destination before downloading");
    Reject(() => GuidedSetup.Create("valid",Path.Combine(temp,"guided"),FileAt("bad.iso"),200,engine,temp,new Progress<string>(),default).GetAwaiter().GetResult(), "guided setup refuses ISO before downloading");
    var plan = LaunchPlan.Build(c, engine, temp, new string('s',64), "test-vm");
    Check(plan.Arguments.Any(a => a.Contains("disk ,, 测试.qcow2")), "QEMU keyval comma escaping");
    // Guest Tools must not reach QEMU at all: same arguments and environment whether integration is on or off.
    Check(!c.IntegrationEnabled && c.ClipboardMode == "text" && Integration.InRange(c.IntegrationPort), "Guest Tools integration is off by default with a port inside its range");
    c.IntegrationEnabled = true; c.ClipboardMode = "off"; c.IntegrationPort = Integration.PortBase + 63;
    var integrated = LaunchPlan.Build(c, engine, temp, new string('s',64), "test-vm");
    Check(integrated.Arguments.SequenceEqual(plan.Arguments) && integrated.Environment.OrderBy(p => p.Key).SequenceEqual(plan.Environment.OrderBy(p => p.Key)), "Guest Tools integration does not change the QEMU command line or environment");
    c.IntegrationPort = Integration.PortBase + 64; Reject(() => c.Validate(false), "integration port outside its range rejected");
    c.IntegrationPort = Integration.PortBase; c.ClipboardMode = "everything"; Reject(() => c.Validate(false), "unknown clipboard mode rejected");
    foreach (var key in new[] { "integration_enabled", "clipboard_mode", "integration_port" }) c.Data.Remove(key);
    Check(plan.Arguments.Contains("q35,memory-backend=ram,max-ram-below-4g=1G") && plan.Arguments.Contains("memory-backend-ram,id=ram,size=8192M,share=on"), "guest RAM is the shared backend the library maps");
    Check(plan.Arguments[plan.Arguments.IndexOf("-accel") + 1] == "whpx" && !plan.Arguments.Any(a => a.Contains("guest-memory") || a.Contains("x-speed") || a.Contains("usb-audio")), "no option of the earlier QEMU remains");
    Check(plan.Arguments[plan.Arguments.IndexOf("-cpu") + 1] == VmConfig.DefaultCpu, "upstream CPU model by default");
    c.Data["cpu"] = "Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce";
    Check(c.Cpu == VmConfig.DefaultCpu, "CPU line written for the earlier QEMU is read as the default");
    c.Data["cpu"] = "Skylake-Client,check"; Check(c.Cpu == "Skylake-Client,check", "CPU line of the user's own retained"); c.Data.Remove("cpu");
    var withSound = LaunchPlan.Build(c, engine, temp, new string('s',64), "test-vm", true).Arguments; var silent = LaunchPlan.Build(c, engine, temp, new string('s',64), "test-vm", false).Arguments;
    Check(withSound.Contains("dsound,id=snd0") && silent.Contains("none,id=snd0") && !silent.Any(a => a.StartsWith("dsound")), "sound goes to DirectSound only where Windows has an output device");
    Check(withSound.Contains("virtio-sound-pci,id=audioin,audiodev=sndin,addr=0x8,streams=1,input-streams=1") && withSound.Contains("none,id=sndin"), "the host microphone is not given to the guest");
    // The guest reaches host loopback TCP through user-mode networking, so QMP must never listen on TCP.
    Check(!plan.Arguments.Contains("-no-shutdown"), "QEMU exits when the guest powers off instead of staying behind");
    int qmpAt = plan.Arguments.IndexOf("-qmp");
    Check(qmpAt >= 0 && plan.Arguments[qmpAt + 1] == "unix:" + LaunchPlan.Q(plan.QmpSocket) + ",server=on,wait=off", "QMP uses a Unix domain socket");
    Check(!plan.Arguments.Any(a => a.StartsWith("tcp:")) && plan.Arguments.Count(a => a.Contains("tcp:")) == 1 && plan.Arguments.Single(a => a.Contains("tcp:")).Contains("hostfwd=tcp:127.0.0.1:2330-:22"), "SSH forwarding is the only TCP endpoint QEMU opens");
    Check(Path.IsPathFullyQualified(plan.QmpSocket) && Path.GetDirectoryName(plan.QmpSocket) == QmpEndpoint.Folder() && Encoding.UTF8.GetByteCount(plan.QmpSocket) <= QmpEndpoint.MaxPathBytes, "QMP socket lives in the short per-user folder");
    Check(LaunchPlan.Build(c,engine,temp,new string('s',64),"test-vm").QmpSocket == plan.QmpSocket && LaunchPlan.Build(c,engine,temp,new string('s',64),"other-vm").QmpSocket != plan.QmpSocket, "QMP socket path follows the per-launch name");
    Reject(() => QmpEndpoint.PathFor("test-vm", Path.Combine(temp, new string('d', 100))), "over-long QMP socket path rejected before launch");
    string socketFolder = Path.Combine(temp, "q"), leftover = Path.Combine(socketFolder, "old.sock"), outside = Path.Combine(temp, "outside.sock"), other = Path.Combine(socketFolder, "keep.txt");
    Directory.CreateDirectory(socketFolder); File.WriteAllText(leftover, ""); File.WriteAllText(outside, ""); File.WriteAllText(other, "");
    QmpEndpoint.Prepare(Path.Combine(socketFolder, "new.sock"), outside); QmpEndpoint.Prepare(Path.Combine(socketFolder, "new.sock"), other);
    Check(File.Exists(outside) && File.Exists(other), "socket cleanup never deletes outside its folder or other file types");
    QmpEndpoint.Prepare(Path.Combine(socketFolder, "new.sock"), leftover);
    Check(!File.Exists(leftover), "leftover socket of the previous launch removed");
    Check(!plan.Preview.Contains(new string('s',64)) && plan.Preview.Contains("<redacted>"), "preview redacts secret");
    Check(LaunchPlan.Build(c,engine,temp,"","test").Arguments.Contains("isa-applesmc,osk=" + LaunchPlan.DefaultOsk), "empty SMC uses built-in default");
    Check(LaunchPlan.ResolveOsk(null) == LaunchPlan.DefaultOsk, "unset environment uses built-in default");
    Check(LaunchPlan.ResolveOsk(new string('s',64)) == new string('s',64), "explicit SMC override retained");
    Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',63),"test"), "short SMC value rejected");
    Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',63)+" ","test"), "SMC whitespace rejected");
    Check(plan.StartInfo(engine.Qemu,temp).ArgumentList.SequenceEqual(plan.Arguments), "arguments preserve boundaries without shell evaluation");
    c.Borderless=true; Check(LaunchPlan.Build(c,engine,temp,new string('s',64),"test").Environment["REIMS_VGPU_WINDOW_BORDERLESS"]=="1", "borderless env mapping");
    c.FreePointer=true; Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',64),"test"), "free pointer gated by backend capability"); c.FreePointer=false;
    foreach (var size in new[] { "2560x1440", "3440x1440", "3840x2160", "4096x2160" }) { c.Resolution=size; c.Validate(); Check(true, "larger startup mode accepted: " + size); }
    foreach (var size in new[] { "0x0", "799x600", "800x599", "4097x2160", "3840x2161", "2560x1440,guest-memory=off" }) { c.Resolution=size; Reject(()=>c.Validate(), "invalid mode rejected: " + size); }
    c.Resolution="1920x1080";
    Check(!plan.Environment.ContainsKey("WEIMS_DISPLAY_MODE"), "no display policy is passed: the window's size is the guest's mode");
    c.DisplayMode="fixed"; LaunchPlan.Build(c,engine,temp,"","test"); Check(true, "a display mode stored by an earlier version does not block a start");
    c.DisplayMode="auto"; c.Save(); Check(VmConfig.Load(c.ConfigPath).DisplayMode=="auto", "display mode key persists for earlier versions");
    var record = Path.Combine(temp,"weims-runtime.json");
    JsonFiles.Write(record, new { format=1, capabilities=new[] { "free-pointer" }, qemu_bytes=new FileInfo(engine.Qemu).Length });
    c.FreePointer=true; Check(engine.SupportsFreePointer && LaunchPlan.Build(c,engine,temp,"","test").Environment["REIMS_VGPU_WINDOW_FREE_POINTER"]=="1", "runtime record enables free pointer");
    JsonFiles.Write(record, new { format=1, capabilities=Array.Empty<string>(), qemu_bytes=new FileInfo(engine.Qemu).Length }); Check(!engine.SupportsFreePointer, "record without the capability does not enable it");
    JsonFiles.Write(record, new { format=1, capabilities=new[] { "free-pointer" }, qemu_bytes=new FileInfo(engine.Qemu).Length + 1 }); Check(!engine.SupportsFreePointer, "record beside another QEMU counts for nothing");
    File.Delete(record); c.FreePointer=false;
    // Boot files: a made-up Mac in a config.plist as reims-util writes it, inside a raw image with other bytes around it.
    string Plist(string serial, string picker = "<true/>") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>Misc</key><dict><key>Boot</key><dict><key>PickerMode</key><string>Builtin</string><key>ShowPicker</key>{picker}</dict></dict>
        <key>NVRAM</key><dict><key>Add</key><dict><key>7C436110-AB2A-4BBB-A880-FE41995C9F82</key><dict><key>csr-active-config</key><data>/w8AAA==</data></dict></dict></dict>
        <key>PlatformInfo</key><dict><key>Generic</key><dict>
        <key>MLB</key><string>TESTMLB0000000001</string><key>ROM</key><data>ESIzRFVm</data>
        <key>SystemProductName</key><string>iMac20,1</string><key>SystemSerialNumber</key><string>{serial}</string>
        <key>SystemUUID</key><string>00000000-1111-2222-3333-444444444444</string></dict></dict>
        </dict></plist>
        """;
    string Image(string name, params string[] plists) { var p = Path.Combine(temp, name); using var f = File.Create(p); f.Write(new byte[70000]); foreach (var text in plists) { f.Write(Encoding.UTF8.GetBytes(text)); f.Write(new byte[4096]); } return p; }
    var mac = BootImage.Read(Image("boot-a.img", Plist("TESTSERIAL01")));
    Check(mac == new BootSettings("iMac20,1", "TESTSERIAL01", "TESTMLB0000000001", "00000000-1111-2222-3333-444444444444", "112233445566", true, true, true), "Mac identity and boot settings read out of a raw boot image");
    Check(!BootImage.Read(Image("boot-b.img", Plist("TESTSERIAL01", "<false/>"))).ShowPicker, "hidden boot picker is kept hidden");
    Reject(() => BootImage.Read(Image("boot-c.img", Plist("TESTSERIAL01"), Plist("TESTSERIAL02"))), "image holding two configurations refused");
    Check(BootImage.Read(Image("boot-f.img", Plist("TESTSERIAL01") + Plist("TESTSERIAL02")[300..], Plist("TESTSERIAL03")[300..])) == mac, "leftover tail of an earlier configuration is passed over, as in images of 1.2's helper");
    Check(BootImage.Read(Image("boot-g.img", Plist("TESTSERIAL01"), Plist("TESTSERIAL01"))) == mac, "two copies of one configuration present one Mac");
    Reject(() => BootImage.Read(Image("boot-d.img")), "image without a configuration refused");
    Reject(() => BootImage.Read(Image("boot-e.img", Plist("TESTSERIAL01")[..600])), "image with a cut-off configuration refused");
    var renewArgs = BootImage.HelperArguments(engine, "out.img", mac);
    Check(renewArgs.Contains("--no-ovmf") && renewArgs[renewArgs.IndexOf("--serial") + 1] == "TESTSERIAL01" && renewArgs[renewArgs.IndexOf("--rom") + 1] == "112233445566" && renewArgs.Count(a => a == "--add-kext") == 3 && renewArgs.Contains("--disable-sip"), "renewal keeps the Mac, its settings and the firmware, with this runtime's drivers");
    Check(!BootImage.HelperArguments(engine, "out.img").Contains("--serial"), "a new VM gets a Mac of its own");
    Check(BootImage.SetId(engine) == null && !BootImage.Outdated(c, engine), "runtime without guest drivers never calls a VM's boot files outdated");
    Directory.CreateDirectory(Path.Combine(temp, "boot")); File.WriteAllText(Path.Combine(temp, "boot", "set.toml"), "format = 1"); File.WriteAllText(Path.Combine(temp, "boot", "ReimsGraphics.rom"), "rom"); FileAt("reims-util.exe");
    string setId = BootImage.SetId(engine)!;
    Check(c.BootSet == "" && BootImage.Outdated(c, engine), "VM of an earlier version has outdated boot files");
    c.BootSet = setId; Check(!BootImage.Outdated(c, engine), "boot files made for this runtime are current");
    File.WriteAllText(Path.Combine(temp, "boot", "ReimsGraphics.rom"), "rom2"); Check(BootImage.SetId(engine) != setId && BootImage.Outdated(c, engine), "another runtime's drivers or ROM make them outdated");
    c.BootSet = VmConfig.OwnBootFiles; Check(!BootImage.Outdated(c, engine), "boot files of the user's own are never replaced");
    c.Data.Remove("boot_set"); Directory.Delete(Path.Combine(temp, "boot"), true); File.Delete(Path.Combine(temp, "reims-util.exe"));
    c.DisplayMode="invalid"; Reject(()=>c.Validate(), "invalid display policy rejected"); c.DisplayMode="legacy";
    c.QmpPort=c.SshPort; c.Validate(); Check(true, "legacy QMP port value no longer blocks validation"); c.QmpPort=4550;
    c.SshPort=80; Reject(()=>c.Validate(), "privileged SSH port rejected"); c.SshPort=2330;
    c.SshPortInput="invalid"; Reject(()=>c.Validate(), "invalid port text survives navigation and blocks launch"); c.SshPortInput="2330";
    Reject(()=>RecoveryService.ValidateBootImage(c.Disk), "non-bootable raw data rejected");
    c.Recovery=FileAt("BaseSystem.img"); c.RecoveryAttached=true;
    var recovery=LaunchPlan.Build(c,engine,temp,new string('s',64),"test"); Check(recovery.Arguments.Any(a=>a.StartsWith("id=recovery,")&&a.Contains("snapshot=on")), "recovery source protected by temporary write layer");
    c.RecoveryAttached=false; Check(!LaunchPlan.Build(c,engine,temp,new string('s',64),"test").Arguments.Any(a=>a.StartsWith("id=recovery,")), "recovery detach omits disk");
    var req=new RecoveryRequest("test",Path.Combine(temp,"newvm"),FileAt("installer.iso"),200,c.OpenCore,c.FirmwareCode,c.FirmwareVars,c.Rom,4550,2330);
    Reject(()=>RecoveryService.Validate(req), "ISO rejected");
    Reject(()=>RecoveryService.Validate(req with { Image=c.Recovery, Destination=temp }), "existing recovery destination rejected");
    Check((await VmService.State(c)).CanEdit,"no runtime record is stopped");
    using(var occupied=new TcpListener(IPAddress.Loopback,c.SshPort)) { occupied.Start(); Reject(()=>VmService.CheckPorts(c), "port collision rejected"); }
    using(var occupied=new TcpListener(IPAddress.Loopback,c.QmpPort)) { occupied.Start(); VmService.CheckPorts(c); Check(true, "legacy QMP port is not probed"); }
    // Mock QMP interleaves async events before command responses and verifies request IDs.
    // This TCP variant covers a VM that an older version started; new launches use the socket variant below.
    using var server = new TcpListener(IPAddress.Loopback,0); server.Start();
    int port=((IPEndPoint)server.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var serve=Task.Run(async()=>{
        using var peer=await server.AcceptTcpClientAsync(timeout.Token); using var r=new StreamReader(peer.GetStream()); using var w=new StreamWriter(peer.GetStream(),new UTF8Encoding(false)){AutoFlush=true};
        await w.WriteLineAsync("{\"QMP\":{\"version\":{}}}");
        for(int i=0;i<3;i++) { var request=JsonNode.Parse((await r.ReadLineAsync(timeout.Token))!)!; await w.WriteLineAsync("{\"event\":\"STOP\"}"); var result=new JsonObject { ["return"]=request["execute"]!.GetValue<string>()=="query-name"?new JsonObject{["name"]="expected"}:new JsonObject(), ["id"]=request["id"]!.DeepClone()}; await w.WriteLineAsync(result.ToJsonString()); }
    });
    using(var q=await QmpClient.Connect(port,timeout.Token)) { await q.VerifyName("expected",timeout.Token); Check(true,"QMP event filtering and response IDs"); bool rejected=false; try { await q.VerifyName("wrong",timeout.Token); } catch(IOException) { rejected=true; } Check(rejected,"QMP rejects wrong VM identity"); }
    await serve;
    using var stalled=new TcpListener(IPAddress.Loopback,0); stalled.Start(); int stalledPort=((IPEndPoint)stalled.LocalEndpoint).Port;
    using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(200)); bool timedOut=false;
    try { using var q=await QmpClient.Connect(stalledPort,deadline.Token); } catch(OperationCanceledException) { timedOut=true; }
    Check(timedOut,"QMP greeting timeout bounded");
    // The same contract over a Unix domain socket. Socket paths are short by necessity, so not under the long test folder.
    string socketPath = Path.Combine(Path.GetTempPath(), "wq-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
    string stalledPath = Path.Combine(Path.GetTempPath(), "wq-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
    try
    {
        using var unixTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var unixServer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        unixServer.Bind(new UnixDomainSocketEndPoint(socketPath)); unixServer.Listen(1);
        Task ServeUnix(Func<string, JsonObject> reply, int requests) => Task.Run(async () =>
        {
            using var peer = await unixServer.AcceptAsync(unixTimeout.Token); using var ns = new NetworkStream(peer); using var r = new StreamReader(ns); using var w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };
            await w.WriteLineAsync("{\"QMP\":{\"version\":{}}}");
            for (int i = 0; i < requests; i++) { var request = JsonNode.Parse((await r.ReadLineAsync(unixTimeout.Token))!)!; await w.WriteLineAsync("{\"event\":\"STOP\"}"); await w.WriteLineAsync(new JsonObject { ["return"] = reply(request["execute"]!.GetValue<string>()), ["id"] = request["id"]!.DeepClone() }.ToJsonString()); }
        });
        JsonObject Reply(string command) => command == "query-name" ? new JsonObject { ["name"] = "expected" } : command == "query-status" ? new JsonObject { ["status"] = "running" } : new JsonObject();
        var serveUnix = ServeUnix(Reply, 3);
        using (var q = await QmpClient.Connect(socketPath, unixTimeout.Token)) { await q.VerifyName("expected", unixTimeout.Token); Check(true, "QMP over a Unix domain socket"); bool rejected = false; try { await q.VerifyName("wrong", unixTimeout.Token); } catch (IOException) { rejected = true; } Check(rejected, "socket QMP rejects wrong VM identity"); }
        await serveUnix;
        // A runtime record naming a socket is managed over that socket; this test process stands in for QEMU.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        JsonFiles.Write(c.RuntimePath, new { pid = Environment.ProcessId, executable = Environment.ProcessPath, started_utc = self.StartTime.ToUniversalTime().ToString("o"), qmp_socket = socketPath, qmp_name = "expected" });
        var serveState = ServeUnix(Reply, 3);
        var socketState = await VmService.State(c);
        Check(socketState.Running && !socketState.CanEdit, "runtime record with a socket is managed over that socket");
        await serveState;
        File.Delete(c.RuntimePath);
        using (var stalledUnix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stalledUnix.Bind(new UnixDomainSocketEndPoint(stalledPath)); stalledUnix.Listen(1);
            using var unixDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)); bool unixTimedOut = false;
            try { using var q = await QmpClient.Connect(stalledPath, unixDeadline.Token); } catch (OperationCanceledException) { unixTimedOut = true; }
            Check(unixTimedOut, "socket QMP greeting timeout bounded");
        }
        // Nobody listens on that path any more. The real leftover-file case after a hard kill is in the integration tests.
        bool refused = false; var refusedTimer = System.Diagnostics.Stopwatch.StartNew();
        try { using var q = await QmpClient.Connect(stalledPath, unixTimeout.Token); } catch (SocketException) { refused = true; }
        Check(refused && refusedTimer.ElapsedMilliseconds < 2000, "socket path with nobody listening fails fast");
    }
    finally { File.Delete(socketPath); File.Delete(stalledPath); }
    // Downloaded archives must be verified before creating any extracted files.
    string zip = Path.Combine(temp, "runtime.zip");
    using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("bin/test.txt").Open())) writer.Write("verified");
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip)));
    string extract = Path.Combine(temp, "extracted");
    Reject(() => EnvironmentSetup.ExtractVerified(zip, extract, new string('0', 64), default), "wrong package checksum rejected");
    Check(!Directory.Exists(extract), "checksum failure creates no extracted directory");
    EnvironmentSetup.ExtractVerified(zip, extract, hash, default);
    Check(File.ReadAllText(Path.Combine(extract, "bin/test.txt")) == "verified", "verified archive extracts successfully");
    Reject(() => EnvironmentSetup.ExtractVerified(zip, extract, hash, default), "existing extraction directory is never overwritten");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Reject(() => EnvironmentSetup.ExtractVerified(zip, Path.Combine(temp, "cancelled"), hash, cancelled.Token), "cancelled extraction stops");
    Check(!Directory.Exists(Path.Combine(temp, "cancelled")), "cancel before extraction leaves destination untouched");
    foreach (string unsafeName in new[] { "../escape.txt", "..\\escape.txt", "C:/escape.txt", "safe.txt:stream" })
    {
        string badZip = Path.Combine(temp, Guid.NewGuid() + ".zip");
        using (var archive = System.IO.Compression.ZipFile.Open(badZip, System.IO.Compression.ZipArchiveMode.Create)) archive.CreateEntry(unsafeName);
        string badHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(badZip)));
        Reject(() => EnvironmentSetup.ExtractVerified(badZip, Path.Combine(temp, Guid.NewGuid().ToString()), badHash, default), "unsafe ZIP path rejected: " + unsafeName);
    }
    Text.Chinese = false; Check(c.Summary.Contains("cores"), "English model summary");
    Text.Chinese = true; Check(c.Summary.Contains("核"), "Chinese model summary");
    var payload = File.ReadAllBytes(zip);
    using var transport = new HttpClient(new PackageHandler(payload));
    var package = new SetupPackage("fixture-1.0", "https://github.com/test/fixture.zip", hash, payload.Length);
    string installRoot = Path.Combine(temp,"installs"); Directory.CreateDirectory(installRoot);
    var progress = new Progress<string>();
    string installed = await EnvironmentSetup.InstallPackage(package, installRoot, progress, default, transport);
    Check(File.ReadAllText(Path.Combine(installed,"bin/test.txt")) == "verified", "download pipeline validates and publishes package");
    string repaired = await EnvironmentSetup.InstallPackage(package, installRoot, progress, default, transport);
    Check(repaired != installed && Directory.Exists(installed), "retry preserves previous installation");
    bool oversized = false;
    try { await EnvironmentSetup.InstallPackage(package with { MaxBytes = 1 }, installRoot, progress, default, transport); } catch(IOException) { oversized = true; }
    Check(oversized && Directory.GetDirectories(installRoot).Length == 2, "oversized download rejected and staging cleaned");
    bool corrupt = false;
    try { await EnvironmentSetup.InstallPackage(package with { Sha256 = new string('0',64) }, installRoot, progress, default, transport); } catch(InvalidDataException) { corrupt = true; }
    Check(corrupt && Directory.GetDirectories(installRoot).Length == 2, "corrupt download does not publish or overwrite");
    string profile = Path.Combine(temp, "profile"), managed = Path.Combine(profile, "runtimes"), installId = new string('a', 32);
    EngineSettings RuntimeAt(string folder) => new() { Qemu = Path.Combine(folder, "qemu-system-x86_64.exe"), RuntimeBin = folder };
    Check(EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(managed, "weims-runtime-1.1.0-pre.1-" + installId)), profile, "weims-runtime-1.1.2"), "managed runtime of another version than the catalog's is outdated");
    Check(!EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(managed, "weims-runtime-1.1.2-" + installId)), profile, "weims-runtime-1.1.2"), "managed runtime of the catalog's version is current");
    Check(EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(managed.ToUpperInvariant(), "weims-runtime-1.0-" + installId)), profile + Path.DirectorySeparatorChar, "weims-runtime-1.1.2"), "letter case and a trailing separator do not hide a managed runtime");
    Check(!EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(temp, "custom", "weims-runtime-1.0-" + installId)), profile, "weims-runtime-1.1.2"), "runtime outside the managed folder is never outdated");
    Check(!EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(managed, "my-own-build")), profile, "weims-runtime-1.1.2"), "folder without an install id is not a managed runtime");
    Check(!EnvironmentSetup.RuntimeOutdated(RuntimeAt(Path.Combine(managed, "weims-runtime-1.0-" + installId, "bin")), profile, "weims-runtime-1.1.2"), "runtime nested deeper in the managed folder is not a managed runtime");
    Check(!EnvironmentSetup.RuntimeOutdated(new EngineSettings(), profile, "weims-runtime-1.1.2"), "empty runtime settings are not outdated");
    Console.WriteLine($"{passed} contract checks passed.");
}
finally { Directory.Delete(temp,true); }

sealed class PackageHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
}
