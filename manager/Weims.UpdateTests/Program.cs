// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Weims.Core;

int count = 0;
void Check(bool ok, string title) { if (!ok) throw new Exception(title); Console.WriteLine("PASS " + title); count++; }
async Task Reject(Func<Task> action, string title) { try { await action(); } catch { Check(true, title); return; } throw new Exception("Did not reject: " + title); }
var bytes = new byte[] { 1, 2, 3, 4 };
var digest = Convert.ToHexString(SHA256.HashData(bytes));
object Release(string tag, bool pre = false, bool draft = false, string? hash = null, string? url = null) => new {
 tag_name = tag, prerelease = pre, draft, body = "Notes", assets = new[] { new {
 name = $"Weims-Manager-{tag.TrimStart('v')}-Setup-x64.exe", size = bytes.Length, digest = "sha256:" + (hash ?? digest),
 browser_download_url = url ?? $"https://github.com/H0yeR404/weims-vgpu/releases/download/{tag}/Weims-Manager-{tag.TrimStart('v')}-Setup-x64.exe" } } };
string Json(params object[] values) => JsonSerializer.Serialize(values);
Check(ReleaseVersion.Parse("1.1.0-pre.10").CompareTo(ReleaseVersion.Parse("1.1.0-pre.2")) > 0, "numeric prerelease order");
Check(ReleaseVersion.Parse("1.1.0").CompareTo(ReleaseVersion.Parse("1.1.0-pre.99")) > 0, "stable follows preview");
Check(ReleaseVersion.Parse("v1.1.0+build").CompareTo(ReleaseVersion.Parse("1.1.0")) == 0, "build metadata ignored");
Check(ManagerUpdates.Select(Json(Release("v1.2.0-pre.1", true)), "1.1.0") == null, "stable ignores preview");
Check(ManagerUpdates.Select(Json(Release("v1.2.0", false, true)), "1.1.0") == null, "draft ignored");
Check(ManagerUpdates.Select(Json(Release("v1.0.1")), "1.1.0-pre.1") == null, "preview never downgrades to older stable");
Check(ManagerUpdates.Select(Json(Release("v1.1.0-pre.1", true)), "1.1.0-pre.1") == null, "same version not offered");
Check(ManagerUpdates.Select(Json(Release("v1.1.0")), "1.1.0-pre.1")?.Tag == "v1.1.0", "preview may advance to stable");
Check(ManagerUpdates.Select(Json(Release("v1.1.0-pre.2", true), Release("v1.1.0-pre.10", true)), "1.1.0-pre.1")?.Tag == "v1.1.0-pre.10", "highest compatible release selected");
await Reject(() => Task.FromResult(ManagerUpdates.Select(Json(Release("v1.2.0", hash:"bad")), "1.1.0")), "missing digest fails closed");
await Reject(() => Task.FromResult(ManagerUpdates.Select(Json(Release("v1.2.0", url:"https://evil.example/install.exe")), "1.1.0")), "foreign executable URL rejected");
await Reject(() => Task.FromResult(ReleaseVersion.Parse("1.1.0-pre.01")), "invalid leading zero rejected");
var folder = Path.Combine(Path.GetTempPath(), "weims-update-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try {
 var release = ManagerUpdates.Select(Json(Release("v1.2.0")), "1.1.0")!;
 using var service = new ManagerUpdates(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
 var path = await service.Download(release, folder, null, CancellationToken.None);
 Check(File.ReadAllBytes(path).SequenceEqual(bytes), "download verified before publishing installer path");
 await Reject(() => service.Download(release with { Sha256 = new string('0',64) }, folder, null, CancellationToken.None), "checksum mismatch rejected");
 await Reject(() => service.Download(release with { Size = 3 }, folder, null, CancellationToken.None), "oversized download rejected");
 await Reject(() => service.Download(release with { Size = 5 }, folder, null, CancellationToken.None), "truncated download rejected");
 await Reject(() => service.Download(release, folder, null, new CancellationToken(true)), "cancelled download rejected");
 Check(Directory.GetFiles(folder, "*.partial").Length == 0, "failed and cancelled partials cleaned up");
 File.WriteAllText(path, "tampered");
 await Reject(() => ManagerUpdates.Verify(path, digest, CancellationToken.None), "cached executable reverified before install");
 using var unavailable = new ManagerUpdates(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
 await Reject(() => unavailable.Check("1.1.0", CancellationToken.None), "GitHub failure is not reported as up-to-date");
 if (args.Contains("--live")) {
  using var live = new ManagerUpdates();
  var found = await live.Check("1.0.0", CancellationToken.None);
  Check(found != null && found.Version.Preview == null, "real GitHub API stable release discovery");
  Console.WriteLine("Live release: " + found!.Tag);
 }
 Console.WriteLine($"{count} update checks passed.");
} finally { Directory.Delete(folder, true); }
sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler {
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
}
