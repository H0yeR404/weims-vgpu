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
    string FileAt(string name) { var p = Path.Combine(temp, name); File.WriteAllText(p, "test"); return p; }
    var engine = new EngineSettings { Qemu = FileAt("qemu-system-x86_64.exe"), RuntimeBin = temp, Bios = temp };
    FileAt("qemu-img.exe"); FileAt("reims_vgpu_c.dll");
    var c = new VmConfig(Path.Combine(temp,"vm","state","config.json"), new JsonObject { ["future_field"] = "preserve me" });
    c.Cpus = 8; c.MemoryMiB = 8192; c.Resolution = "1920x1080"; c.Mac = "52:54:00:12:34:56";
    c.QmpPort = 4550; c.SshPort = 2330; c.Disk = FileAt("disk , 测试.qcow2"); c.OpenCore = FileAt("OpenCore.img"); c.FirmwareCode = FileAt("CODE.fd"); c.FirmwareVars = FileAt("VARS.fd"); c.Rom = FileAt("Graphics.rom");
    c.Save(); Check(VmConfig.Load(c.ConfigPath).Data["future_field"]!.GetValue<string>() == "preserve me", "unknown config fields survive save");
    var plan = LaunchPlan.Build(c, engine, temp, new string('s',64), "test-vm");
    Check(plan.Arguments.Any(a => a.Contains("disk ,, 测试.qcow2")), "QEMU keyval comma escaping");
    Check(plan.Arguments.Any(a => a.Contains("guest-memory=on")), "shared-memory graphics enabled");
    Check(!plan.Preview.Contains(new string('s',64)) && plan.Preview.Contains("<redacted>"), "preview redacts secret");
    Check(LaunchPlan.Build(c,engine,temp,"","test").Arguments.Contains("isa-applesmc,osk=" + LaunchPlan.DefaultOsk), "empty SMC uses built-in default");
    Check(LaunchPlan.ResolveOsk(null) == LaunchPlan.DefaultOsk, "unset environment uses built-in default");
    Check(LaunchPlan.ResolveOsk(new string('s',64)) == new string('s',64), "explicit SMC override retained");
    Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',63),"test"), "short SMC value rejected");
    Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',63)+" ","test"), "SMC whitespace rejected");
    Check(plan.StartInfo(engine.Qemu,temp).ArgumentList.SequenceEqual(plan.Arguments), "arguments preserve boundaries without shell evaluation");
    c.Borderless=true; Check(LaunchPlan.Build(c,engine,temp,new string('s',64),"test").Environment["REIMS_VGPU_WINDOW_BORDERLESS"]=="1", "borderless env mapping");
    c.FreePointer=true; Reject(()=>LaunchPlan.Build(c,engine,temp,new string('s',64),"test"), "free pointer gated by backend capability"); c.FreePointer=false;
    c.Resolution="3840x2160"; Reject(()=>c.Validate(), "unsafe Tahoe resolution rejected"); c.Resolution="1920x1080";
    c.QmpPort=c.SshPort; Reject(()=>c.Validate(), "duplicate ports rejected"); c.QmpPort=4550;
    c.QmpPortInput="invalid"; Reject(()=>c.Validate(), "invalid port text survives navigation and blocks launch"); c.QmpPortInput="4550";
    Reject(()=>RecoveryService.ValidateBootImage(c.Disk), "non-bootable raw data rejected");
    c.Recovery=FileAt("BaseSystem.img"); c.RecoveryAttached=true;
    var recovery=LaunchPlan.Build(c,engine,temp,new string('s',64),"test"); Check(recovery.Arguments.Any(a=>a.StartsWith("id=recovery,")&&a.Contains("readonly=on")), "recovery is attached read-only");
    c.RecoveryAttached=false; Check(!LaunchPlan.Build(c,engine,temp,new string('s',64),"test").Arguments.Any(a=>a.StartsWith("id=recovery,")), "recovery detach omits disk");
    var req=new RecoveryRequest("test",Path.Combine(temp,"newvm"),FileAt("installer.iso"),200,c.OpenCore,c.FirmwareCode,c.FirmwareVars,c.Rom,4550,2330);
    Reject(()=>RecoveryService.Validate(req), "ISO rejected");
    Reject(()=>RecoveryService.Validate(req with { Image=c.Recovery, Destination=temp }), "existing recovery destination rejected");
    Check((await VmService.State(c)).CanEdit,"no runtime record is stopped");
    using(var occupied=new TcpListener(IPAddress.Loopback,c.QmpPort)) { occupied.Start(); Reject(()=>VmService.CheckPorts(c), "port collision rejected"); }
    // Mock QMP interleaves async events before command responses and verifies request IDs.
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
    Console.WriteLine($"{passed} contract checks passed.");
}
finally { Directory.Delete(temp,true); }

sealed class PackageHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
}
