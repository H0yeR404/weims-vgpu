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
var ensureRoot = Path.Combine(test, "reuse-check");
var reused = await EnvironmentSetup.Ensure(engine, ensureRoot, new Progress<string>(Console.WriteLine), timeout.Token);
if (!ReferenceEquals(engine, reused) || Directory.Exists(ensureRoot)) throw new Exception("Valid runtime should be reused without downloading or changing paths.");
Console.WriteLine("PASS one-click setup reuses validated runtime without downloading");
bool cancelled = false;
try { await EnvironmentSetup.Ensure(new EngineSettings(), Path.Combine(test, "cancelled"), new Progress<string>(), new CancellationToken(true)); }
catch (OperationCanceledException) { cancelled = true; }
if (!cancelled || Directory.Exists(Path.Combine(test, "cancelled"))) throw new Exception("Cancelled setup must not create runtime files.");
Console.WriteLine("PASS cancelled automatic setup leaves paths untouched");
// A runtime that Manager installed is replaced once the catalog names another version; the tested runtime is moved, not copied.
string data = Path.Combine(test, "managed"), installId = Guid.NewGuid().ToString("N");
string current = Path.Combine(data, "runtimes", catalog.Runtime.Version + "-" + installId), old = Path.Combine(data, "runtimes", "weims-runtime-0.0.0-" + installId);
Directory.CreateDirectory(Path.GetDirectoryName(current)!); Directory.Move(dir, current);
EngineSettings At(string folder) => new() { Qemu = Path.Combine(folder,"qemu-system-x86_64.exe"), RuntimeBin = folder, Bios = Path.Combine(folder,"pc-bios") };
var managed = At(current);
if (!ReferenceEquals(managed, await EnvironmentSetup.Ensure(managed, data, new Progress<string>(Console.WriteLine), timeout.Token)) || (await EnvironmentSetup.Detect(managed, timeout.Token, data)).RuntimeOutdated)
    throw new Exception("A managed runtime of the catalog's version should be kept.");
Console.WriteLine("PASS managed runtime of the catalog's version is kept");
// QEMU has just run from the folder; Windows can hold it for a moment after the process is gone.
for (int attempt = 0; ; attempt++) { try { Directory.Move(current, old); break; } catch (IOException) when (attempt < 20) { await Task.Delay(500); } }
var outdated = At(old);
var seen = await EnvironmentSetup.Detect(outdated, timeout.Token, data);
if (!seen.RuntimeOutdated) throw new Exception("A managed runtime of another version should be reported as outdated.");
if (seen.RuntimeUpdateRequired != catalog.Runtime.Required) throw new Exception("An outdated managed runtime must be replaced exactly when the catalog says so.");
if (!ReferenceEquals(outdated, await EnvironmentSetup.Ensure(outdated, data, new Progress<string>(), timeout.Token))) throw new Exception("Starting a VM should keep a working runtime, outdated or not.");
using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
var steps = new StopAtDownload(stop, catalog.Runtime.Version); bool replaced = false;
try { await EnvironmentSetup.Ensure(outdated, data, steps, stop.Token, replaceOutdated: true); replaced = true; } catch (OperationCanceledException) { }
if (replaced || !steps.Reached || Directory.GetDirectories(Path.Combine(data, "runtimes")).Length != 1 || !File.Exists(outdated.Qemu))
    throw new Exception("An outdated managed runtime should go on to the catalog's download, and stay in place when that is cancelled.");
Console.WriteLine("PASS outdated managed runtime is reported, goes on to the catalog's download, and stays when that is cancelled");
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
    var installed = await EnvironmentSetup.Ensure(new EngineSettings(), Path.Combine(test,"download"), new Progress<string>(Console.WriteLine), timeout.Token);
    if (!Path.IsPathFullyQualified(installed.PowerShell) || !installed.SupportsFreePointer) throw new Exception("Private runtime was not prepared.");
    await EnvironmentSetup.CheckPowerShell(installed.PowerShell, timeout.Token);
    Console.WriteLine("PASS clean-PATH automatic runtime and PowerShell installation");
}

// Cancels at the step that would download the runtime, before any request is sent.
sealed class StopAtDownload(CancellationTokenSource stop, string version) : IProgress<string>
{
    public bool Reached;
    public void Report(string value)
    {
        Console.WriteLine(value);
        if (value.EndsWith(version) && (value.StartsWith("Downloading ") || value.StartsWith("下载 "))) { Reached = true; stop.Cancel(); }
    }
}
