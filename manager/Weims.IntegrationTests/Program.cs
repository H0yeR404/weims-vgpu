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
Check(new FileInfo(image).Length==128L*1024*1024&&File.Exists(request.Vars),"source image and firmware retained");
Check(engine.SupportsFreePointer,"input capability marker matches built DLL");
var launcher=Path.Combine(repo,"manager","launch-vm.ps1");
using(var locked=new FileStream(Path.Combine(Path.GetDirectoryName(c.ConfigPath)!,"manager-launch.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
{
    bool rejected=false;try{await VmService.Start(c,engine,new string('x',64),launcher);}catch(IOException){rejected=true;}
    Check(rejected,"concurrent manager launch rejected before QEMU starts");
}
// No boot firmware, disk or accelerator is needed for this stopped QMP-only QEMU process.
var run=Path.Combine(root,"qmp"); Directory.CreateDirectory(run); var name="weims-test-"+Guid.NewGuid().ToString("N");
var plan=Path.Combine(run,"command.json");
string[] command={"-machine","none,accel=tcg","-nodefaults","-display","none","-S","-name",name,"-qmp",$"tcp:127.0.0.1:{c.QmpPort},server=on,wait=off"};
JsonFiles.Write(plan,new{executable=engine.Qemu,arguments=command,runtime=c.RuntimePath,run_directory=run,qmp_port=c.QmpPort,qmp_name=name});
try
{
    await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-File",launcher,"-Plan",plan},env,timeout.Token,captureOutput:false);
    var state=await VmService.State(c); Check(!state.CanEdit&&state.Running,"real QMP identifies paused test process");
    await VmService.Power(c,"quit");
    for(int i=0;i<20;i++){if((await VmService.State(c)).CanEdit)break;await Task.Delay(100);}
    Check((await VmService.State(c)).CanEdit,"real QMP quit releases process");
}
finally { var rt=VmService.Runtime(c); if(rt!=null&&VmService.ProcessMatches(rt))await VmService.Power(c,"quit"); }
// Force a runtime publication failure; helper must roll back its newly launched child.
var badRuntime=Path.Combine(root,"runtime-is-a-directory"); Directory.CreateDirectory(badRuntime);
JsonFiles.Write(plan,new{executable=engine.Qemu,arguments=command,runtime=badRuntime,run_directory=run,qmp_port=c.QmpPort,qmp_name=name});
bool failed=false; try{await ProcessRunner.Run("pwsh.exe",new[]{"-NoProfile","-File",launcher,"-Plan",plan},env,timeout.Token,captureOutput:false);}catch(IOException){failed=true;}
Check(failed,"runtime publication failure reported");
VmService.CheckPorts(c); Check(true,"runtime publication failure leaves no listening child");
File.WriteAllText(Path.Combine(root,"result.txt"),$"{checks} integration checks passed. Synthetic recovery fixture; no macOS install performed.");
Console.WriteLine($"{checks} integration checks passed. Evidence: {root}");
