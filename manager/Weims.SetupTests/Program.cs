// SPDX-License-Identifier: GPL-2.0-or-later
// Isolated real runtime test. Never loads a VM configuration or starts a guest.
using System.Security.Cryptography;
using Weims.Core;

if (args.Length < 2) throw new ArgumentException("Arguments: runtime-zip isolated-test-directory [--download]");
string test = Path.GetFullPath(args[1]);
if (Directory.Exists(test)) throw new IOException("Use a new test directory.");
Directory.CreateDirectory(test);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var catalog = EnvironmentSetup.Catalog();
string dir = Path.Combine(test, "runtime");
EnvironmentSetup.ExtractVerified(args[0], dir, catalog.Runtime.Sha256, timeout.Token);
var engine = new EngineSettings { Qemu = Path.Combine(dir,"qemu-system-x86_64.exe"), RuntimeBin = dir, Bios = Path.Combine(dir,"pc-bios") };
await EnvironmentSetup.ValidateRuntime(engine, timeout.Token);
Console.WriteLine("PASS verified packaged runtime and input extension");
var report = await EnvironmentSetup.Detect(engine, timeout.Token); Console.WriteLine(report.Details);
if (!report.SupportedHost || !report.RuntimeReady || !report.PowerShellReady) throw new Exception("Host/runtime detection failed.");
Console.WriteLine("PASS real environment detection");
if (args.Contains("--powershell"))
{
    string psDir = await EnvironmentSetup.InstallPackage(catalog.PowerShell, test, new Progress<string>(Console.WriteLine), timeout.Token);
    await EnvironmentSetup.CheckPowerShell(Path.Combine(psDir, "pwsh.exe"), timeout.Token);
    Console.WriteLine("PASS official PowerShell download, checksum and private execution");
}
if (args.Contains("--download"))
{
    // Remove development PowerShell and MSYS2 from PATH to exercise private PowerShell download.
    Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
    var installed = await EnvironmentSetup.Prepare(Path.Combine(test,"download"), new Progress<string>(Console.WriteLine), timeout.Token);
    if (!Path.IsPathFullyQualified(installed.PowerShell) || !installed.SupportsFreePointer) throw new Exception("Private runtime was not prepared.");
    await EnvironmentSetup.CheckPowerShell(installed.PowerShell, timeout.Token);
    Console.WriteLine("PASS clean-PATH automatic runtime and PowerShell installation");
}
