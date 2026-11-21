// SPDX-License-Identifier: GPL-2.0-or-later
// Real qemu-img and QMP integration; no guest OS and no existing VM disk are used.
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Weims.Core;

if(args.Length!=2) throw new ArgumentException("Arguments: repository-root msys2-ucrt64-bin");
string repo=Path.GetFullPath(args[0]);
var engine=new EngineSettings { Qemu=Path.Combine(repo,"build","manager-runtime","qemu-system-x86_64.exe"), RuntimeBin=Path.GetFullPath(args[1]), Bios=Path.Combine(repo,"sources","qemu","pc-bios") };
engine.Validate();
var root=Path.Combine(repo,"logs","manager-integration-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int checks=0; void Check(bool ok,string name) { if(!ok)throw new Exception("FAIL "+name); Console.WriteLine("PASS "+name); checks++; }
int Port() { using var l=new TcpListener(IPAddress.Loopback,0); l.Start();return ((IPEndPoint)l.LocalEndpoint).Port; }
var env=new Dictionary<string,string>{["PATH"]=engine.RuntimeBin+";"+Environment.GetEnvironmentVariable("PATH"),["WEIMS_OSK"]=new string('x',64)};
string image=Path.Combine(root,"fixture.img");
using(var f=File.Create(image)) { f.SetLength(128L*1024*1024); f.Position=510; f.Write(new byte[]{0x55,0xaa}); }
string Fixture(string n) { var p=Path.Combine(root,n); File.WriteAllBytes(p,new byte[4096]); return p; }
var request=new RecoveryRequest("fixture-vm",Path.Combine(root,"prepared"),image,64,Fixture("OpenCore.img"),Fixture("CODE.fd"),Fixture("VARS.fd"),Fixture("Graphics.rom"),Port(),Port());
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
var c=await RecoveryService.Create(request,engine,new Progress<string>(Console.WriteLine),timeout.Token);
var info=JsonNode.Parse(await ProcessRunner.Run(engine.ImageTool,new[]{"info","--output=json",c.Disk},env,timeout.Token))!;
Check(info["virtual-size"]!.GetValue<long>()==64L*1024*1024*1024,"recovery preparation creates requested qcow2 virtual capacity");
Check(c.RecoveryAttached&&File.Exists(c.Recovery)&&c.DirectoryPath==request.Destination,"staged recovery published with final paths");
Check(c.Rom == Path.Combine(c.DirectoryPath,"firmware","ReimsGraphics.rom") && File.ReadAllBytes(c.Rom).SequenceEqual(File.ReadAllBytes(request.Rom)), "ROM copied into independent VM directory");
Check(new FileInfo(image).Length==128L*1024*1024&&File.Exists(request.Vars),"source image and firmware retained");
Check(engine.SupportsFreePointer,"input capability marker matches built DLL");
var launcher=Path.Combine(repo,"manager","launch-vm.ps1");
using(var locked=new FileStream(Path.Combine(Path.GetDirectoryName(c.ConfigPath)!,"manager-launch.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
{
    bool rejected=false;try{await VmService.Start(c,engine,new string('x',64),launcher);}catch(IOException){rejected=true;}
    Check(rejected,"concurrent manager launch rejected before QEMU starts");
}
// Never leave the test QEMU behind, and never let cleanup hide the check that failed: if QMP cannot stop it, end the process.
async Task StopTestQemu()
{
    var rt=VmService.Runtime(c); if(rt==null||!VmService.ProcessMatches(rt)) return;
    try { await VmService.Power(c,"quit"); }
    catch { try { using var p=System.Diagnostics.Process.GetProcessById(rt["pid"]!.GetValue<int>()); p.Kill(); p.WaitForExit(5000); } catch { } }
}
// No boot firmware, disk or accelerator is needed for this stopped QMP-only QEMU process.
var run=Path.Combine(root,"qmp"); Directory.CreateDirectory(run); var name="weims-test-"+Guid.NewGuid().ToString("N");
var plan=Path.Combine(run,"command.json");
// QMP is a Unix domain socket, exactly as LaunchPlan passes it for a real VM.
string socket=QmpEndpoint.PathFor(name); QmpEndpoint.Prepare(socket,null);
// q35 rather than "none": it has a keyboard, which the shutdown key check below needs. It starts paused and has no disk.
string[] command={"-machine","q35,accel=tcg","-L",engine.Bios,"-nodefaults","-device","qemu-xhci,id=xhci","-display","none","-S","-name",name,"-qmp",$"unix:{LaunchPlan.Q(socket)},server=on,wait=off"};
JsonFiles.Write(plan,new{executable=engine.Qemu,arguments=command,runtime=c.RuntimePath,run_directory=run,qmp_socket=socket,qmp_name=name});
try
{
    await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-File",launcher,"-Plan",plan},env,timeout.Token,captureOutput:false);
    var record=VmService.Runtime(c)!;
    Check(record["qmp_socket"]!.GetValue<string>()==socket&&record["qmp_port"]==null,"launcher records the socket and no TCP port");
    int qemuPid=record["pid"]!.GetValue<int>();
    string listeners=(await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-Command",$"@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq {qemuPid}).Count"},env,timeout.Token)).Trim();
    Check(listeners=="0","QEMU with socket QMP opens no TCP listener");
    var state=await VmService.State(c); Check(!state.CanEdit&&state.Running,"real QMP identifies paused test process");
    {
        // The Guest Tools install disk: a raw image plugged in as a read-only USB stick, exactly as a real VM gets it.
        string stick=Path.Combine(root,"gt-stick.img"); File.WriteAllBytes(stick,new byte[1<<20]);
        await InstallMedia.Unplug(c); Check(true,"unplugging the install disk when none is plugged in is not an error");
        await InstallMedia.Plug(c,stick,"WRabcdefghijklmnopqr");
        using(var look=await QmpClient.Connect(socket,timeout.Token))
        {
            var blocks=(await look.Execute("query-block",timeout.Token))!.AsArray();
            var ours=blocks.FirstOrDefault(b=>b!["inserted"]?["node-name"]?.GetValue<string>()==InstallMedia.Node);
            Check(ours!=null&&ours["inserted"]!["ro"]!.GetValue<bool>()&&ours["removable"]!.GetValue<bool>(),"QEMU accepts the install disk as a read-only, removable USB stick");
        }
        bool locked=false; try { using var held=new FileStream(stick,FileMode.Open,FileAccess.ReadWrite,FileShare.None); } catch(IOException) { locked=true; }
        bool twice=false; try { await InstallMedia.Plug(c,stick,"WRabcdefghijklmnopqr"); } catch(IOException) { twice=true; }
        bool longSerial=false; try { await InstallMedia.Plug(c,stick,new string('a',21)); } catch(ArgumentException) { longSerial=true; }
        Check(longSerial,"a serial number longer than QEMU allows is refused before anything is plugged in");
        Check(twice,"plugging a second install disk is refused while one is in");
        await InstallMedia.Unplug(c);
        using(var look=await QmpClient.Connect(socket,timeout.Token))
            Check(!(await look.Execute("query-block",timeout.Token))!.AsArray().Any(b=>b!["inserted"]?["node-name"]?.GetValue<string>()==InstallMedia.Node),"unplugging removes the stick");
        File.Delete(stick); Check(!File.Exists(stick),$"after unplugging QEMU has let go of the image file (it held it while plugged: {locked})");
        File.WriteAllBytes(stick,new byte[1<<20]); await InstallMedia.Plug(c,stick,"WS0123456789-_ABCDEF"); await InstallMedia.Unplug(c);
        Check(true,"the install disk can be plugged in again after it was removed");
    }
    bool stopRejected=false; try { await VmService.StopAfterShutdown(c); } catch(IOException) { stopRejected=true; }
    Check(stopRejected,"safe CLI stop refuses paused guest");
    // QEMU takes key events only while the VM runs. A request to a guest that cannot act on it must be reported.
    bool chordRefused=false; try { await VmService.Shutdown(c); } catch(IOException) { chordRefused=true; }
    Check(chordRefused,"shutdown request to a paused VM is reported, not dropped");
    // No guest here to act on the keys; this proves QEMU accepts the key events of the shutdown chord.
    using(var resume=await QmpClient.Connect(socket,timeout.Token)) await resume.Execute("cont",timeout.Token);
    await VmService.Shutdown(c);
    var afterChord=await VmService.State(c); Check(!afterChord.CanEdit&&afterChord.Running,"shutdown key chord accepted by real QMP while running");
    await VmService.Power(c,"quit");
    for(int i=0;i<20;i++){if((await VmService.State(c)).CanEdit)break;await Task.Delay(100);}
    Check((await VmService.State(c)).CanEdit,"real QMP quit releases process");
}
finally { await StopTestQemu(); }
Check(!File.Exists(socket),"QEMU removes its socket on a clean quit");
// A VM that an older version started listens on loopback TCP and has no socket in its record. It must stay manageable until restarted.
var legacyName="weims-test-"+Guid.NewGuid().ToString("N");
string[] legacyCommand={"-machine","none,accel=tcg","-nodefaults","-display","none","-S","-name",legacyName,"-qmp",$"tcp:127.0.0.1:{c.QmpPort},server=on,wait=off"};
JsonFiles.Write(plan,new{executable=engine.Qemu,arguments=legacyCommand,runtime=c.RuntimePath,run_directory=run,qmp_port=c.QmpPort,qmp_name=legacyName});
try
{
    await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-File",launcher,"-Plan",plan},env,timeout.Token,captureOutput:false);
    var legacyRecord=VmService.Runtime(c)!;
    Check(legacyRecord["qmp_socket"]==null&&legacyRecord["qmp_port"]!.GetValue<int>()==c.QmpPort,"plan from an older version keeps its TCP record");
    var legacy=await VmService.State(c); Check(!legacy.CanEdit&&legacy.Running,"VM started by an older version is still managed over TCP");
    await VmService.Power(c,"quit");
    for(int i=0;i<20;i++){if((await VmService.State(c)).CanEdit)break;await Task.Delay(100);}
    Check((await VmService.State(c)).CanEdit,"VM started by an older version quits over TCP");
}
finally { await StopTestQemu(); }
// Force a runtime publication failure; helper must roll back its newly launched child.
var badRuntime=Path.Combine(root,"runtime-is-a-directory"); Directory.CreateDirectory(badRuntime);
JsonFiles.Write(plan,new{executable=engine.Qemu,arguments=command,runtime=badRuntime,run_directory=run,qmp_socket=socket,qmp_name=name});
bool failed=false; try{await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-File",launcher,"-Plan",plan},env,timeout.Token,captureOutput:false);}catch(IOException){failed=true;}
Check(failed,"runtime publication failure reported");
// The helper hard-kills the child, which may leave the socket file behind with nobody listening on it.
bool childGone=false; try { using var probe=await QmpClient.Connect(socket,timeout.Token); } catch(SocketException) { childGone=true; }
Check(childGone,"runtime publication failure leaves no child answering on the socket");
QmpEndpoint.Prepare(socket,socket); Check(!File.Exists(socket),"leftover socket of a killed QEMU is cleaned up");
File.WriteAllText(Path.Combine(root,"result.txt"),$"{checks} integration checks passed. Synthetic recovery fixture; no macOS install performed.");
Console.WriteLine($"{checks} integration checks passed. Evidence: {root}");
