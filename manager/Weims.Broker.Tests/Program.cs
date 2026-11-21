// SPDX-License-Identifier: GPL-2.0-or-later
// Broker tests. The guest helper is played by a loopback client; the Windows clipboard is never read
// or written here (the clipboard rules are tested against a stand-in), and no macOS guest is involved.
// With two arguments (repository-root msys2-ucrt64-bin) the real Weims.Broker.exe is also run against
// a diskless test QEMU.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weims.Broker;
using Weims.Core;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
async Task Rejects<T>(Func<Task> action, string name) where T : Exception { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
async Task<bool> Eventually(Func<bool> condition, int milliseconds = 5000) { var until = DateTime.UtcNow.AddMilliseconds(milliseconds); while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(50); } return condition(); }
var temp = Path.Combine(Path.GetTempPath(), "weims-broker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
var quiet = new Action<string>(_ => { });
VmConfig NewVm(string name) { var state = Path.Combine(temp, name, "state"); Directory.CreateDirectory(state); var c = new VmConfig(Path.Combine(state, "config.json"), new JsonObject()); JsonFiles.Write(c.ConfigPath, c.Data); return c; }

async Task<SslStream> Tls(int port, string fingerprint)
{
    var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port);
    var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant() == fingerprint);
    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "10.0.2.2", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
    return tls;
}
async Task<Frame> Hello(SslStream tls, int version = 1, params string[] capabilities)
{
    await Frames.Write(tls, new JsonObject { ["type"] = "hello", ["version"] = version, ["helper"] = "0.1-test", ["capabilities"] = new JsonArray([.. capabilities.Select(c => (JsonNode)c)]) }, default, default);
    return await Frames.Read(tls, Frames.PreAuthLimit, default);
}
async Task<Frame> Exchange(SslStream tls, JsonObject header) { await Frames.Write(tls, header, default, default); return await Frames.Read(tls, Frames.PreAuthLimit, default); }
// What a helper does to ask for pairing: commit to a random value, take the broker's, reveal, and compute the number it will show.
async Task<(string Digits, Frame? Refusal)> AskToPair(SslStream tls, string fingerprint, string user)
{
    var mine = RandomNumberGenerator.GetBytes(32);
    var answer = await Exchange(tls, new JsonObject { ["type"] = "pair_begin", ["commit"] = Convert.ToHexString(SHA256.HashData(mine)).ToLowerInvariant(), ["user"] = user });
    if (answer.Type != "pair_nonce") return ("", answer);
    await Frames.Write(tls, new JsonObject { ["type"] = "pair_reveal", ["nonce"] = Convert.ToHexString(mine).ToLowerInvariant() }, default, default);
    return (PairingSession.Digits(fingerprint, mine, Convert.FromHexString(answer.Text("nonce")!)), null);
}
async Task<bool> Closed(Stream stream, int milliseconds = 3000)
{
    using var timeout = new CancellationTokenSource(milliseconds);
    try { return await stream.ReadAsync(new byte[1], timeout.Token) == 0; }
    catch (OperationCanceledException) { return false; }
    catch (IOException) { return true; }
}

try
{
    // ---- Frames ----
    {
        var stream = new MemoryStream();
        await Frames.Write(stream, new JsonObject { ["type"] = "clipboard", ["seq"] = 7, ["sensitive"] = true }, new byte[] { 1, 2, 3 }, default);
        await Frames.Write(stream, new JsonObject { ["type"] = "ping" }, default, default);
        stream.Position = 0;
        var first = await Frames.Read(stream, Frames.PreAuthLimit, default);
        Check(first.Type == "clipboard" && first.Number("seq") == 7 && first.Flag("sensitive") && first.Body.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }), "frame round trip keeps header and body");
        var second = await Frames.Read(stream, Frames.PreAuthLimit, default);
        Check(second.Type == "ping" && second.Body.Length == 0 && stream.Position == stream.Length, "consecutive frames are delimited exactly");
        Check(first.Text("seq") == null && first.Number("type") == null && !first.Flag("seq") && first.Text("missing") == null, "header values of the wrong kind read as absent instead of throwing");

        MemoryStream Raw(uint total, uint header, byte[] rest) { var b = new byte[8 + rest.Length]; BinaryPrimitives.WriteUInt32BigEndian(b, total); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), header); rest.CopyTo(b, 8); return new MemoryStream(b); }
        var huge = Raw((uint)Frames.PreAuthLimit + 1, 2, Encoding.UTF8.GetBytes("{}"));
        await Rejects<InvalidDataException>(() => Frames.Read(huge, Frames.PreAuthLimit, default), "frame over the limit is rejected");
        Check(huge.Position == 8, "oversized frame is rejected from its length alone, before the payload is read");
        await Rejects<InvalidDataException>(() => Frames.Read(Raw(0xFFFFFFFF, 2, []), Frames.PreAuthLimit, default), "4 GiB length field is rejected");
        await Rejects<InvalidDataException>(() => Frames.Read(Raw(6, 9, Encoding.UTF8.GetBytes("{}")), Frames.PreAuthLimit, default), "header length beyond the frame is rejected");
        await Rejects<InvalidDataException>(() => Frames.Read(Raw(6, 0, Encoding.UTF8.GetBytes("{}")), Frames.PreAuthLimit, default), "empty header is rejected");
        await Rejects<InvalidDataException>(() => Frames.Read(Raw(6, 2, Encoding.UTF8.GetBytes("[]")), Frames.PreAuthLimit, default), "header that is not a JSON object is rejected");
        await Rejects<InvalidDataException>(() => Frames.Read(Raw(7, 3, Encoding.UTF8.GetBytes("{x}")), Frames.PreAuthLimit, default), "malformed JSON header is rejected");
        var deep = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"a\":", 64)) + "1" + new string('}', 64));
        await Rejects<InvalidDataException>(() => Frames.Read(Raw((uint)deep.Length + 4, (uint)deep.Length, deep), Frames.PreAuthLimit, default), "deeply nested header is rejected");
        await Rejects<EndOfStreamException>(() => Frames.Read(Raw(100, 2, Encoding.UTF8.GetBytes("{}")), Frames.PreAuthLimit, default), "truncated frame is reported");
    }

    // ---- Secrets ----
    {
        var path = Path.Combine(temp, "secrets", "integration-secrets.bin");
        var secrets = BrokerSecrets.LoadOrCreate(path);
        Check(File.Exists(path) && !secrets.Replaced && Regex.IsMatch(secrets.Fingerprint, "^[0-9a-f]{64}$"), "new identity is created and stored");
        using (var cert = secrets.ServerCertificate())
            Check(cert.HasPrivateKey && cert.GetECDsaPublicKey()!.KeySize == 256 && Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant() == secrets.Fingerprint, "certificate is P-256, has its key, and matches the fingerprint");
        var token = secrets.AddHelper("alice");
        Check(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length == 32, "token is 256 bits");
        Check(secrets.Match(token)?.User == "alice" && secrets.Match(token + "x") == null && secrets.Match("") == null && secrets.Match(null) == null && secrets.Match(new string('a', 500)) == null, "only the issued token matches");
        var stored = File.ReadAllBytes(path); var storedText = Encoding.Latin1.GetString(stored);
        Check(!storedText.Contains(token) && !storedText.Contains("PrivateKey") && !storedText.Contains("alice"), "stored secrets are encrypted: no token, key label or user name in the file");
        var again = BrokerSecrets.LoadOrCreate(path);
        Check(!again.Replaced && again.Fingerprint == secrets.Fingerprint && again.Match(token)?.User == "alice", "identity and pairing survive a restart");
        var newer = again.AddHelper("alice");
        Check(again.Match(token) == null && again.Match(newer)?.User == "alice" && again.PairedUsers.SequenceEqual(new[] { "alice" }), "pairing the same guest user again replaces the earlier token");
        var bob = again.AddHelper("bob");
        Check(again.Match(bob)?.User == "bob" && again.Match(newer)?.User == "alice" && again.PairedUsers.Count == 2, "each guest user has their own token");
        var before = again.Fingerprint; again.Reset();
        Check(again.Fingerprint != before && again.Match(newer) == null && again.Match(bob) == null && again.PairedUsers.Count == 0, "reset revokes every token and replaces the certificate");
        Check(BrokerSecrets.LoadOrCreate(path).Fingerprint == again.Fingerprint, "reset identity is what gets stored");
        stored[stored.Length / 2] ^= 0xFF; File.WriteAllBytes(path, stored);
        var replaced = BrokerSecrets.LoadOrCreate(path);
        Check(replaced.Replaced && replaced.Fingerprint != again.Fingerprint && replaced.PairedUsers.Count == 0, "unreadable secrets (other account, other machine, damage) give a new identity and no pairing");
    }

    // ---- Pairing by comparing numbers ----
    {
        var time = DateTime.UtcNow; var pairing = new PairingSession(() => time);
        Check(pairing.Start("alice") == null && pairing.OpenUntilUtc == null && !pairing.Decide(true), "nobody can ask to pair before a window is opened");
        var until = pairing.Open();
        Check(until == time + PairingSession.Lifetime && pairing.OpenUntilUtc == until, "a pairing window stays open for ten minutes");
        var request = pairing.Start("alice")!;
        Check(pairing.Showing == null && !pairing.Decide(true), "a request cannot be confirmed before its number exists");
        Check(pairing.Start("eve") == null, "a second request is refused while one is in progress");
        pairing.Show(request, "0427");
        Check(pairing.Showing is { Digits: "0427", User: "alice" } && !request.Decided.IsCompleted, "the number is on show and waits for the user");
        Check(pairing.Decide(true) && await request.Decided && pairing.OpenUntilUtc == null && pairing.Showing == null, "the user's yes pairs and closes the window");
        Check(pairing.Start("alice") == null, "a window serves one pairing");
        pairing.Open(); request = pairing.Start("eve")!; pairing.Show(request, "1111");
        Check(pairing.Decide(false) && !await request.Decided && pairing.OpenUntilUtc != null && pairing.Showing == null, "the user's no declines that request and keeps the window open for the right one");
        request = pairing.Start("alice")!; pairing.Abandon(request);
        Check(!await request.Decided && pairing.Start("alice") != null, "a helper that goes away frees the window for another try");
        pairing.Open();
        for (int i = 0; i < PairingSession.MaxAttempts; i++) pairing.Abandon(pairing.Start("eve")!);
        Check(pairing.Start("alice") == null && pairing.OpenUntilUtc == null, "five requests in one window close it");
        pairing.Open(); time += PairingSession.Lifetime;
        Check(pairing.Start("alice") == null && pairing.OpenUntilUtc == null, "an expired window accepts nothing");
        pairing.Open(); request = pairing.Start("alice")!; pairing.Show(request, "2222"); pairing.Close();
        Check(!await request.Decided && pairing.Showing == null && pairing.OpenUntilUtc == null, "cancelling the window declines whatever was waiting");

        string print = new string('a', 64), other = new string('b', 64);
        var seen = new HashSet<string>(); int certificate = 0, helperValue = 0, brokerValue = 0;
        for (int i = 0; i < 2000; i++)
        {
            byte[] h = RandomNumberGenerator.GetBytes(32), b = RandomNumberGenerator.GetBytes(32);
            var digits = PairingSession.Digits(print, h, b);
            if (!Regex.IsMatch(digits, "^[0-9]{4}$") || digits != PairingSession.Digits(print, h, b)) throw new Exception("FAIL: digits are not four stable digits");
            seen.Add(digits);
            if (PairingSession.Digits(other, h, b) != digits) certificate++;
            if (PairingSession.Digits(print, RandomNumberGenerator.GetBytes(32), b) != digits) helperValue++;
            if (PairingSession.Digits(print, h, RandomNumberGenerator.GetBytes(32)) != digits) brokerValue++;
        }
        Check(seen.Count > 1700, $"the number is four digits, the same on both sides, and spread over the whole range ({seen.Count} different in 2000)");
        Check(certificate > 1990 && helperValue > 1990 && brokerValue > 1990, "another certificate or another random value on either side gives another number (a party in the middle shows up as two different numbers)");
    }

    // ---- Control server over loopback TLS ----
    {
        using var blocker = new TcpListener(IPAddress.Loopback, Integration.PortBase + 9) { ExclusiveAddressUse = true }; blocker.Start();
        var moved = ControlServer.Bind(Integration.PortBase + 9);
        int movedPort = ((IPEndPoint)moved.LocalEndpoint).Port;
        Check(movedPort != Integration.PortBase + 9 && Integration.InRange(movedPort), "occupied port moves to the next free one inside the range");
        moved.Stop();
        var wrapped = ControlServer.Bind(1); Check(Integration.InRange(((IPEndPoint)wrapped.LocalEndpoint).Port), "port outside the range falls back into it"); wrapped.Stop();

        var secrets = BrokerSecrets.LoadOrCreate(Path.Combine(temp, "server", "integration-secrets.bin"));
        var pairing = new PairingSession(); var status = new BrokerStatus(0);
        var server = new ControlServer(ControlServer.Bind(Integration.PortBase + 20), secrets, pairing, status, quiet) { HandshakeTimeout = TimeSpan.FromSeconds(2) };
        var received = new List<(string User, Frame Frame)>(); var connected = new List<string>(); var disconnected = new List<string>();
        server.Received = (s, f) => { lock (received) received.Add((s.User, f)); };
        server.Connected = s => { lock (connected) connected.Add(s.User); };
        server.Disconnected = s => { lock (disconnected) disconnected.Add(s.User); };
        using var stop = new CancellationTokenSource(); var running = server.Run(stop.Token);
        int port = server.Port; string fp = secrets.Fingerprint;
        Check(status.Read(s => s.Port) == port && status.Read(s => s.Fingerprint) == fp, "status carries the listening port and fingerprint");

        await Rejects<AuthenticationException>(() => Tls(port, new string('0', 64)), "a client pinning another fingerprint refuses the broker");
        using var probe = await Tls(port, fp);
        Console.WriteLine("INFO negotiated " + probe.SslProtocol + " " + probe.NegotiatedCipherSuite);
        Check(probe.SslProtocol is SslProtocols.Tls13 or SslProtocols.Tls12, "TLS 1.2 or newer is negotiated");
        var hello = await Hello(probe, 3, "clipboard-text", "something-new");
        Check(hello.Type == "hello" && hello.Number("version") == 1 && hello.Header["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "clipboard-text" }), "version and capabilities are negotiated down to what both sides know");
        var (_, refused) = await AskToPair(probe, fp, "mallory");
        Check(refused is { Type: "error" } && refused.Text("code") == "pair" && await Closed(probe), "pairing is refused while no pairing window is open");

        using (var bad = await Tls(port, fp))
        {
            await Hello(bad);
            var answer = await Exchange(bad, new JsonObject { ["type"] = "auth", ["token"] = "not-a-token" });
            Check(answer.Type == "error" && answer.Text("code") == "auth" && await Closed(bad), "an unknown token is refused and the connection closed");
        }
        using (var odd = await Tls(port, fp))
        {
            await Frames.Write(odd, new JsonObject { ["type"] = "clipboard", ["kind"] = "text" }, Encoding.UTF8.GetBytes("stolen?"), default);
            var answer = await Frames.Read(odd, Frames.PreAuthLimit, default);
            Check(answer.Type == "error" && answer.Text("code") == "protocol" && await Closed(odd) && received.Count == 0, "data sent before authentication is not delivered");
        }
        using (var big = await Tls(port, fp))
        {
            var prefix = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)Frames.PreAuthLimit + 1); BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(4), 2);
            await big.WriteAsync(prefix);
            Check(await Closed(big), "a frame over 64 KiB before authentication closes the connection");
        }
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, port);
            var watch = Stopwatch.StartNew();
            Check(await Closed(silent.GetStream(), 6000) && watch.Elapsed < TimeSpan.FromSeconds(5), "a connection that does not complete the handshake in time is dropped");
        }

        pairing.Open(); server.PublishPairing();
        string token;
        using (var asking = await Tls(port, fp))
        {
            await Hello(asking, 1, "clipboard-text");
            var (digits, _) = await AskToPair(asking, fp, "alice\u0007\n");
            Check(Regex.IsMatch(digits, "^[0-9]{4}$") && await Eventually(() => status.Read(s => s.PairingDigits) == digits && status.Read(s => s.PairingUser) == "alice"), "both sides arrive at the same four digits, and Manager is given them with the cleaned user name");
            using (var cutting = await Tls(port, fp)) { await Hello(cutting); var (_, no) = await AskToPair(cutting, fp, "eve"); Check(no?.Text("code") == "pair", "a second helper cannot cut in while a number is on show"); }
            Check(secrets.PairedUsers.Count == 0, "nothing is paired until the user answers");
            pairing.Decide(true);
            var paired = await Frames.Read(asking, Frames.PreAuthLimit, default);
            token = paired.Text("token") ?? "";
            Check(paired.Type == "paired" && paired.Text("user") == "alice" && secrets.Match(token)?.User == "alice" && await Closed(asking), "the user's yes issues a token and ends the pairing connection");
            Check(await Eventually(() => status.Read(s => s.PairingDigits) == null && status.Read(s => s.PairingUntilUtc) == null) && connected.Count == 0, "the number leaves Manager, the window is closed, and pairing itself starts no session");
        }
        using var alice = await Tls(port, fp); await Hello(alice, 1, "clipboard-text");
        Check((await Exchange(alice, new JsonObject { ["type"] = "auth", ["token"] = token })).Type == "ok", "the token from pairing authenticates the helper");
        Check(await Eventually(() => status.Read(s => s.HelperUser) == "alice" && connected.Count == 1) && connected.SequenceEqual(new[] { "alice" }) && status.Read(s => s.PairedUsers).SequenceEqual(new[] { "alice" }), "a paired helper is connected and shown in the status");
        Check((await Exchange(alice, new JsonObject { ["type"] = "ping" })).Type == "pong", "ping is answered");
        await Frames.Write(alice, new JsonObject { ["type"] = "clipboard", ["kind"] = "text" }, Encoding.UTF8.GetBytes("hi"), default);
        await Frames.Write(alice, new JsonObject { ["type"] = "from-the-future" }, default, default);
        Check(await Eventually(() => received.Count == 2) && received[0].User == "alice" && Encoding.UTF8.GetString(received[0].Frame.Body.Span) == "hi", "frames from an authenticated helper are delivered, unknown types included");

        using var second = await Tls(port, fp); await Hello(second, 1, "clipboard-text");
        var ok = await Exchange(second, new JsonObject { ["type"] = "auth", ["token"] = token });
        Check(ok.Type == "ok" && ok.Text("user") == "alice", "the stored token authenticates a later connection");
        Check(await Closed(alice) && await Eventually(() => disconnected.Count == 1), "a newer connection replaces the older one");
        Check(await Eventually(() => connected.Count == 2) && status.Read(s => s.HelperUser) == "alice", "status still shows the helper after the replacement");

        pairing.Open();
        using (var mallory = await Tls(port, fp))
        {
            await Hello(mallory);
            var (digits, _) = await AskToPair(mallory, fp, "mallory");
            await Eventually(() => status.Read(s => s.PairingDigits) == digits);
            pairing.Decide(false);
            var no = await Frames.Read(mallory, Frames.PreAuthLimit, default);
            Check(no.Type == "error" && no.Text("code") == "pair-declined" && secrets.PairedUsers.SequenceEqual(new[] { "alice" }) && pairing.OpenUntilUtc != null, "the user's no turns that helper away, pairs nothing and leaves the window open");
        }
        using (var cheat = await Tls(port, fp))
        {
            await Hello(cheat);
            var begun = await Exchange(cheat, new JsonObject { ["type"] = "pair_begin", ["commit"] = Convert.ToHexString(SHA256.HashData(new byte[32])).ToLowerInvariant(), ["user"] = "cheat" });
            var no = await Exchange(cheat, new JsonObject { ["type"] = "pair_reveal", ["nonce"] = new string('1', 64) });
            Check(begun.Type == "pair_nonce" && no.Type == "error" && status.Read(s => s.PairingDigits) == null, "a helper that reveals another value than the one it committed to is refused and no number is shown");
        }
        using (var quitter = await Tls(port, fp))
        {
            await Hello(quitter);
            var (digits, _) = await AskToPair(quitter, fp, "quitter");
            Check(await Eventually(() => status.Read(s => s.PairingDigits) == digits), "another helper may try in the same window");
        }
        Check(await Eventually(() => status.Read(s => s.PairingDigits) == null) && !pairing.Decide(true), "when the helper goes away its number leaves Manager and cannot be confirmed any more");
        pairing.Open();
        for (int i = 0; i < PairingSession.MaxAttempts; i++)
        {
            using var again = await Tls(port, fp); await Hello(again);
            await AskToPair(again, fp, "eve");
            again.Close(); await Eventually(() => pairing.Showing == null && status.Read(s => s.PairingDigits) == null);
        }
        using (var late = await Tls(port, fp))
        {
            await Hello(late);
            var (_, no) = await AskToPair(late, fp, "eve");
            Check(no?.Type == "error" && pairing.OpenUntilUtc == null && secrets.PairedUsers.SequenceEqual(new[] { "alice" }), "five requests in one window close it");
        }

        secrets.Reset(); server.ReloadIdentity();
        Check(await Closed(second) && status.Read(s => s.Fingerprint) == secrets.Fingerprint && secrets.Fingerprint != fp, "resetting pairing drops the helper and publishes a new fingerprint");
        await Rejects<AuthenticationException>(() => Tls(port, fp), "after a reset the old fingerprint no longer matches");
        using (var stale = await Tls(port, secrets.Fingerprint))
        {
            await Hello(stale);
            Check((await Exchange(stale, new JsonObject { ["type"] = "auth", ["token"] = token })).Text("code") == "auth", "after a reset the old token is refused");
        }
        stop.Cancel(); await running;
        await Rejects<SocketException>(async () => { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); }, "a stopped server no longer listens");
    }
    {
        // Limits on connections that have not authenticated, and on ones that have.
        var secrets = BrokerSecrets.LoadOrCreate(Path.Combine(temp, "limits", "integration-secrets.bin"));
        var pairing = new PairingSession(); var status = new BrokerStatus(0);
        var server = new ControlServer(ControlServer.Bind(Integration.PortBase + 30), secrets, pairing, status, quiet) { MaxUnauthenticated = 2, HandshakeTimeout = TimeSpan.FromSeconds(4), IdleTimeout = TimeSpan.FromSeconds(1), FrameLimit = 1024, PairingDecisionTimeout = TimeSpan.FromSeconds(1) };
        using var stop = new CancellationTokenSource(); var running = server.Run(stop.Token);
        using var a = new TcpClient(); await a.ConnectAsync(IPAddress.Loopback, server.Port);
        using var b = new TcpClient(); await b.ConnectAsync(IPAddress.Loopback, server.Port);
        await Task.Delay(200);
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, server.Port);
            Check(await Closed(c.GetStream(), 1500), "connections beyond the unauthenticated limit are closed at once");
        }
        Check(!await Closed(a.GetStream(), 300), "connections inside the limit are kept until the handshake deadline");
        a.Close(); b.Close();
        var token = secrets.AddHelper("carol");
        SslStream? helper = null;
        Check(await Eventually(() => { try { helper = Tls(server.Port, secrets.Fingerprint).GetAwaiter().GetResult(); return true; } catch (Exception e) when (e is IOException or AuthenticationException) { return false; } }), "the limit frees up when waiting connections go away");
        await Hello(helper!);
        Check((await Exchange(helper!, new JsonObject { ["type"] = "auth", ["token"] = token })).Type == "ok", "authentication works after the limit was hit");
        var watch = Stopwatch.StartNew();
        Check(await Closed(helper!, 4000) && watch.Elapsed > TimeSpan.FromMilliseconds(700), "a helper that stays silent past the idle timeout is disconnected");
        using var large = await Tls(server.Port, secrets.Fingerprint); await Hello(large);
        await Exchange(large, new JsonObject { ["type"] = "auth", ["token"] = token });
        await Frames.Write(large, new JsonObject { ["type"] = "clipboard", ["kind"] = "text" }, new byte[2048], default);
        Check(await Closed(large), "a frame over the limit after authentication closes the connection");
        pairing.Open();
        using (var waiting = await Tls(server.Port, secrets.Fingerprint))
        {
            await Hello(waiting);
            await AskToPair(waiting, secrets.Fingerprint, "slow");
            var timeWaited = Stopwatch.StartNew();
            var late = await Frames.Read(waiting, Frames.PreAuthLimit, default);
            Check(late.Type == "error" && late.Text("code") == "pair-timeout" && timeWaited.Elapsed > TimeSpan.FromMilliseconds(700) && secrets.PairedUsers.SequenceEqual(new[] { "carol" }), "a number nobody answers for is withdrawn after its time and pairs nothing");
        }
        stop.Cancel(); await running;
    }

    // ---- Clipboard rules ----
    {
        var time = DateTime.UtcNow; string mode = "text"; string? note = null;
        var host = new FakeHost(); var link = new FakeLink();
        var sync = new ClipboardSync(host, () => mode, n => note = n, () => time);
        Frame Guest(string text, long? replyTo = null, bool sensitive = false, string kind = "text")
        {
            var h = new JsonObject { ["type"] = "clipboard", ["kind"] = kind }; if (replyTo != null) h["reply_to"] = replyTo; if (sensitive) h["sensitive"] = true;
            return new Frame(h, Encoding.UTF8.GetBytes(text));
        }
        string Types() => string.Join(",", link.Sent.Select(s => s.Header["type"]!.GetValue<string>()));
        string LastText() => Encoding.UTF8.GetString(link.Sent.Last(s => s.Header["type"]!.GetValue<string>() == "clipboard").Body);

        host.Content = new ClipContent("windows text\r\nline 2", false);
        sync.Start(hostHasForeignText: true, windowFocused: false);
        sync.HostChanged(false);
        Check(link.Sent.Count == 0, "nothing is sent while no helper is connected");
        sync.HelperConnected(link);
        Check(Types() == "focus" && link.Sent[0].Header["active"]!.GetValue<bool>() == false, "a helper connecting in the background is told the focus state and gets no clipboard");
        sync.FocusChanged(true);
        Check(Types() == "focus,focus,clipboard" && LastText() == "windows text\nline 2" && link.Sent[2].Header["seq"]!.GetValue<long>() == 1, "coming to the foreground sends Windows text that changed meanwhile, with macOS line ends");
        sync.FocusChanged(true);
        Check(link.Sent.Count == 3, "a repeated focus event does nothing");

        // Copy an image in macOS, switch away and back: the old Windows text must not be pushed over it.
        sync.FocusChanged(false);
        Check(Types().EndsWith("focus,clipboard_request") && link.Sent.Last().Header["id"]!.GetValue<long>() == 1, "leaving the foreground asks the guest once for its latest content");
        sync.FocusChanged(true);
        Check(Types().EndsWith("clipboard_request,focus") && link.Sent.Count(s => s.Header["type"]!.GetValue<string>() == "clipboard") == 1, "returning without a Windows clipboard change does not send stale Windows text again");

        host.Content = new ClipContent("typed in windows", false); sync.HostChanged(false);
        Check(LastText() == "typed in windows", "a Windows clipboard change in the foreground is sent at once");
        sync.FromGuest(link, Guest("from mac\nline 2", sensitive: true));
        Check(host.Writes.Count == 1 && host.Writes[0].Text == "from mac\r\nline 2" && host.Writes[0].Sensitive, "guest text in the foreground reaches Windows with Windows line ends and its sensitive mark");
        int sent = link.Sent.Count; sync.HostChanged(true);
        Check(link.Sent.Count == sent, "the broker's own write is not echoed back to the guest");

        sync.FocusChanged(false); long request = link.Sent.Last().Header["id"]!.GetValue<long>();
        sync.FromGuest(link, Guest("background push"));
        Check(host.Writes.Count == 1, "guest content pushed while in the background is dropped");
        sync.FromGuest(link, Guest("wrong id", request + 5));
        Check(host.Writes.Count == 1, "a reply to some other request is dropped");
        sync.FromGuest(link, Guest("copied just before switching", request));
        Check(host.Writes.Count == 2 && host.Writes[1].Text == "copied just before switching", "the reply to the blur request is accepted in the background");
        sync.HostChanged(true);
        sync.FromGuest(link, Guest("second reply", request));
        Check(host.Writes.Count == 2, "a request is answered only once");
        sent = link.Sent.Count; sync.FocusChanged(true);
        Check(link.Sent.Count == sent + 1, "content that came from this VM is not sent back when its window returns");

        sync.FocusChanged(false); request = link.Sent.Last().Header["id"]!.GetValue<long>(); time += ClipboardSync.BlurReplyWindow + TimeSpan.FromSeconds(1);
        sync.FromGuest(link, Guest("too late", request));
        Check(host.Writes.Count == 2, "a reply after the three-second window is dropped");
        host.Content = new ClipContent("copied in another windows app", true); sync.HostChanged(false);
        sent = link.Sent.Count; sync.FocusChanged(true);
        Check(link.Sent.Count == sent + 2 && LastText() == "copied in another windows app" && link.Sent.Last().Header["sensitive"]!.GetValue<bool>(), "a change made while away is sent on return, with the sensitive mark of a password manager");

        sync.FromGuest(link, new Frame(new JsonObject { ["type"] = "clipboard", ["kind"] = "text" }, new byte[] { 0xC3, 0x28 }));
        sync.FromGuest(link, Guest("an image", kind: "png"));
        sync.FromGuest(link, Guest(""));
        sync.FromGuest(new FakeLink(), Guest("from a replaced connection"));
        sync.FromGuest(link, new Frame(new JsonObject { ["type"] = "clipboard", ["kind"] = 5 }, Encoding.UTF8.GetBytes("x")));
        Check(host.Writes.Count == 2, "invalid UTF-8, other kinds, empty text and frames from a replaced connection are ignored");

        sync.FromGuest(link, new Frame(new JsonObject { ["type"] = "clipboard", ["kind"] = "text" }, new byte[ClipboardSync.TextLimit + 1]));
        Check(host.Writes.Count == 2 && note != null, "guest text over 4 MiB is not copied and the status says so");
        var exact = new string('a', ClipboardSync.TextLimit);
        sync.FromGuest(link, Guest(exact));
        Check(host.Writes.Count == 3 && host.Writes[2].Text.Length == ClipboardSync.TextLimit && note == null, "guest text of exactly 4 MiB is copied");
        host.Content = new ClipContent("", false, true); sent = link.Sent.Count; sync.HostChanged(false);
        Check(link.Sent.Count == sent && note != null, "Windows text over the limit is not sent and the status says so");
        host.Content = new ClipContent(new string('é', ClipboardSync.TextLimit / 2 + 1), false); sync.HostChanged(false);
        Check(link.Sent.Count == sent, "the limit is measured in UTF-8 bytes");
        var fidelity = "中文 日本語かな ไทย 🎉👨‍👩‍👧 \t tab\nnew line";
        host.Content = new ClipContent(fidelity.Replace("\n", "\r\n"), false); sync.HostChanged(false);
        Check(LastText() == fidelity && note == null, "Chinese, Japanese, combining emoji, tabs and line breaks reach the guest unchanged");
        sync.FromGuest(link, Guest(fidelity));
        Check(host.Writes.Last().Text == fidelity.Replace("\n", "\r\n"), "the same text reaches Windows unchanged apart from line ends");

        mode = "off"; sent = link.Sent.Count; int writes = host.Writes.Count;
        host.Content = new ClipContent("while off", false); sync.HostChanged(false); sync.FromGuest(link, Guest("while off"));
        sync.FocusChanged(false);
        Check(link.Sent.Count == sent + 1 && host.Writes.Count == writes && link.Sent.Last().Header["type"]!.GetValue<string>() == "focus", "with clipboard mode off nothing crosses and the guest is not asked");
        mode = "text"; sync.FocusChanged(true);
        Check(LastText() == "while off", "turning the mode back on delivers the pending Windows text at the next focus");

        var replacement = new FakeLink();
        sync.HelperDisconnected(link); host.Content = new ClipContent("while disconnected", false); sync.HostChanged(false);
        sync.HelperDisconnected(replacement);
        sync.HelperConnected(replacement);
        Check(replacement.Sent.Count == 2 && Encoding.UTF8.GetString(replacement.Sent[1].Body) == "while disconnected", "a helper that connects while its VM is in front receives the pending Windows text");
        int kept = host.Writes.Count; host.Fail = true; sync.FromGuest(replacement, Guest("clipboard busy"));
        Check(host.Writes.Count == kept, "a Windows clipboard that cannot be opened loses that one update and nothing else");
        Check(ClipboardSync.ToWindows("a\r\nb\nc") == "a\r\nb\r\nc" && ClipboardSync.ToGuest("a\r\nb\rc") == "a\nb\rc", "line end conversion does not double existing CR LF and leaves lone CR alone");
    }

    // ---- What Manager and the CLI see ----
    {
        var c = NewVm("config-defaults");
        Check(!c.IntegrationEnabled && c.ClipboardMode == "text" && Integration.InRange(c.IntegrationPort) && c.IntegrationPort == Integration.DefaultPort(c), "integration is off by default; the port default is derived from the VM and inside the range");
        Check(c.Data.Count == 0, "reading the defaults writes nothing into an existing configuration");
        Check(Integration.PortBase + Integration.PortCount <= 49152, "the integration range lies below the Windows dynamic port range");
        var other = NewVm("config-other");
        Check(Integration.PipeName(c) != Integration.PipeName(other) && Regex.IsMatch(Integration.PipeName(c), "^weims-broker-[0-9a-f]{16}$"), "each VM has its own command pipe name");
        c.IntegrationEnabled = true; c.IntegrationPort = Integration.PortBase + 3; c.ClipboardMode = "off"; c.Data["future_key"] = "kept";
        JsonFiles.Write(c.ConfigPath, c.Data);
        var loaded = VmConfig.Load(c.ConfigPath);
        Check(loaded.IntegrationEnabled && loaded.IntegrationPort == Integration.PortBase + 3 && loaded.ClipboardMode == "off" && loaded.Data["future_key"]!.GetValue<string>() == "kept", "integration keys round trip and unknown keys are kept");

        Check(!BrokerState.Read(c).Online, "no state file means offline");
        var status = new BrokerStatus(1234);
        status.Update(s => { s.Port = 46403; s.Fingerprint = "ab"; s.HelperUser = "alice"; s.HelperVersion = "0.1"; s.PairedUsers = ["alice"]; });
        status.Write(Integration.StatePath(c), quiet);
        var read = BrokerState.Read(c);
        Check(read.Online && read.Connected && read.Port == 46403 && read.HelperUser == "alice" && read.PairedUsers.SequenceEqual(new[] { "alice" }), "a fresh heartbeat from a live broker reads as online and connected");
        var raw = File.ReadAllText(Integration.StatePath(c));
        Check(!raw.Contains("token", StringComparison.OrdinalIgnoreCase) && !raw.Contains("\"code\"") && !raw.Contains("key", StringComparison.OrdinalIgnoreCase), "the state file has no token, code or key fields");
        Check(!BrokerState.Read(c, DateTime.UtcNow + Integration.HeartbeatTimeout + TimeSpan.FromSeconds(1)).Online, "a heartbeat older than ten seconds reads as offline");
        var json = JsonNode.Parse(raw)!.AsObject(); json["pid"] = 4; JsonFiles.Write(Integration.StatePath(c), json);
        Check(!BrokerState.Read(c).Online, "a state file left by a broker that is gone reads as offline");
        status.Update(s => { s.Stopped = true; s.LastError = "port range busy"; }); status.Write(Integration.StatePath(c), quiet);
        var stopped = BrokerState.Read(c);
        Check(!stopped.Online && stopped.Error == "port range busy" && stopped.Label.Contains("port range busy"), "a stopped broker reads as offline and its last error is shown");
        File.WriteAllText(Integration.StatePath(c), "{ not json"); Check(!BrokerState.Read(c).Online, "a damaged state file reads as offline");
        File.Delete(Integration.StatePath(c));

        // Supervision: whoever notices the broker is gone starts it again, three times at most.
        var now = DateTime.UtcNow; int starts = 0; void Start(VmConfig _) => starts++;
        c.IntegrationEnabled = false;
        Integration.Supervise(c, true, () => now, Start);
        Check(starts == 0, "integration off: the broker is never started");
        c.IntegrationEnabled = true;
        Integration.Supervise(c, false, () => now, Start);
        Check(starts == 0, "VM not running: the broker is not started");
        JsonFiles.Write(c.RuntimePath, new JsonObject { ["pid"] = Environment.ProcessId, ["qmp_port"] = 4444 });
        var legacy = Integration.Supervise(c, true, () => now, Start);
        Check(starts == 0 && !legacy.Online && legacy.Error!.Contains("older version"), "VM started by a version with TCP QMP: no broker, and the reason is shown");
        JsonFiles.Write(c.RuntimePath, new JsonObject { ["pid"] = Environment.ProcessId, ["qmp_socket"] = @"C:\x\y.sock" });
        Integration.Supervise(c, true, () => now, Start);
        Check(starts == 1, "integration on, VM running, broker missing: it is started");
        now += TimeSpan.FromSeconds(5); Integration.Supervise(c, true, () => now, Start);
        Check(starts == 1, "a broker that was just started gets ten seconds before another attempt");
        now += TimeSpan.FromSeconds(6); Integration.Supervise(c, true, () => now, Start);
        now += TimeSpan.FromSeconds(11); Integration.Supervise(c, true, () => now, Start);
        Check(starts == 3, "it is started again while it stays missing");
        now += TimeSpan.FromSeconds(11); var gaveUp = Integration.Supervise(c, true, () => now, Start);
        now += TimeSpan.FromSeconds(60); Integration.Supervise(c, true, () => now, Start);
        Check(starts == 3 && !gaveUp.Online && gaveUp.Error!.Contains("no longer retrying"), "after three attempts in a row it stops and says so");
        Integration.Supervise(c, false, () => now, Start); Integration.Supervise(c, true, () => now, Start);
        Check(starts == 4, "a VM restart clears the count");
        for (int i = 0; i < 5; i++) { now += TimeSpan.FromSeconds(11); Integration.Supervise(c, true, () => now, Start); }
        Check(starts == 6, "the limit applies again to that run");
        JsonFiles.Write(c.RuntimePath, new JsonObject { ["pid"] = Environment.ProcessId, ["started_utc"] = "2026-11-19T00:00:00Z", ["qmp_socket"] = "C:/x/y.sock" });
        Integration.Supervise(c, true, () => now, Start);
        Check(starts == 7, "a VM that was restarted between two polls starts with a clean count");
        var failing = Integration.Supervise(NewVmWithRuntime("supervise-missing"), true, () => now, _ => throw new FileNotFoundException("Missing integration component: X"));
        Check(!failing.Online && failing.Error!.Contains("Missing integration component"), "a broker that cannot be started is reported and does not throw");

        // Command pipe.
        using (var acl = CommandPipe.CreateServer("weims-broker-acl-test-" + Guid.NewGuid().ToString("N")))
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent(); var owner = me.Owner ?? me.User!;
            var security = System.IO.Pipes.PipesAclExtensions.GetAccessControl(acl);
            var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
            var network = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null);
            Check(rules.Length == 2 && rules.Any(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny && r.IdentityReference.Equals(network))
                && rules.Any(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && r.IdentityReference.Equals(owner))
                && security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Equals(owner), "the command pipe admits its owner only and denies anyone coming over the network");
        }
        using var stop = new CancellationTokenSource();
        var pipe = new CommandPipe(Integration.PipeName(c), request => request["command"]?.GetValue<string>() == "status" ? new JsonObject { ["ok"] = true, ["echo"] = "yes" } : new JsonObject { ["ok"] = false, ["error"] = "Unknown command." }, quiet).Run(stop.Token);
        Check((await Integration.Command(c, "status"))["echo"]!.GetValue<string>() == "yes", "a command over the named pipe is answered");
        Check((await Integration.Command(c, "status"))["ok"]!.GetValue<bool>(), "the pipe serves one request after another");
        await Rejects<IOException>(() => Integration.Command(c, "format-disk"), "a refused command surfaces as an error");
        await Rejects<IOException>(() => Integration.Command(other, "status"), "a VM without a broker reports that the component is not running");
        stop.Cancel(); await pipe;
    }

    // ---- The real broker process against a diskless test QEMU ----
    if (args.Length == 2) await ProcessChecks(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    else Console.WriteLine("INFO process checks skipped: pass repository-root and msys2-ucrt64-bin to run them");

    Console.WriteLine($"{passed} broker checks passed.");
    return 0;
}
finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

VmConfig NewVmWithRuntime(string name) { var c = NewVm(name); c.IntegrationEnabled = true; JsonFiles.Write(c.RuntimePath, new JsonObject { ["pid"] = Environment.ProcessId, ["qmp_socket"] = @"C:\x\y.sock" }); return c; }

async Task ProcessChecks(string repo, string runtimeBin)
{
    var engine = new EngineSettings { Qemu = Path.Combine(repo, "build", "manager-runtime", "qemu-system-x86_64.exe"), RuntimeBin = runtimeBin, Bios = Path.Combine(repo, "sources", "qemu", "pc-bios") };
    engine.Validate();
    var broker = Path.Combine(repo, "manager", "Weims.Broker", "bin", "Release", "net10.0-windows", "Weims.Broker.exe");
    if (!File.Exists(broker)) throw new FileNotFoundException("Build Weims.Broker in Release first: " + broker);
    Environment.SetEnvironmentVariable(Integration.BrokerExecutableOverride, broker);
    var launcher = Path.Combine(repo, "manager", "launch-vm.ps1");
    var env = new Dictionary<string, string> { ["PATH"] = engine.RuntimeBin + ";" + Environment.GetEnvironmentVariable("PATH"), ["WEIMS_OSK"] = new string('x', 64) };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

    var c = NewVm("process"); c.IntegrationEnabled = true; JsonFiles.Write(c.ConfigPath, c.Data);
    var run = Path.Combine(c.DirectoryPath, "logs", "run"); Directory.CreateDirectory(run);
    async Task<int> StartQemu(bool socket)
    {
        var name = "weims-test-" + Guid.NewGuid().ToString("N"); var plan = Path.Combine(run, "command.json");
        if (socket)
        {
            string path = QmpEndpoint.PathFor(name); QmpEndpoint.Prepare(path, null);
            JsonFiles.Write(plan, new { executable = engine.Qemu, arguments = new[] { "-machine", "none,accel=tcg", "-nodefaults", "-display", "none", "-S", "-name", name, "-qmp", $"unix:{LaunchPlan.Q(path)},server=on,wait=off" }, runtime = c.RuntimePath, run_directory = run, qmp_socket = path, qmp_name = name });
        }
        else JsonFiles.Write(plan, new { executable = engine.Qemu, arguments = new[] { "-machine", "none,accel=tcg", "-nodefaults", "-display", "none", "-S", "-name", name, "-qmp", $"tcp:127.0.0.1:{c.QmpPort},server=on,wait=off" }, runtime = c.RuntimePath, run_directory = run, qmp_port = c.QmpPort, qmp_name = name });
        await ProcessRunner.Run("pwsh.exe", ["-NoProfile", "-File", launcher, "-Plan", plan], env, timeout.Token, captureOutput: false);
        return VmService.Runtime(c)!["pid"]!.GetValue<int>();
    }
    async Task StopQemu() { var rt = VmService.Runtime(c); if (rt == null || !VmService.ProcessMatches(rt)) return; try { await VmService.Power(c, "quit"); } catch { try { using var p = Process.GetProcessById(rt["pid"]!.GetValue<int>()); p.Kill(); p.WaitForExit(5000); } catch { } } }
    int Exit(string exe, params string[] arguments) { using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })!; if (!p.WaitForExit(15000)) { p.Kill(); return -1; } return p.ExitCode; }
    Process[] Brokers() => Process.GetProcessesByName("Weims.Broker").Where(p => { try { return string.Equals(p.MainModule!.FileName, broker, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToArray();

    try
    {
        Check(Exit(broker) == Weims.Broker.Program.ExitUsage && Exit(broker, "--config", c.ConfigPath) == Weims.Broker.Program.ExitNoVm, "the broker refuses to run without a running VM");
        await StartQemu(socket: false);
        Check(Exit(broker, "--config", c.ConfigPath) == Weims.Broker.Program.ExitNoVm, "the broker refuses a VM whose QMP is still on TCP");
        await StopQemu(); Check(await Eventually(() => VmService.State(c).GetAwaiter().GetResult().CanEdit), "legacy test QEMU stopped");

        int qemuPid = await StartQemu(socket: true);
        Check(!Integration.Supervise(c, true).Online, "the first status poll finds no broker and starts one");
        Check(await Eventually(() => BrokerState.Read(c).Online, 15000), "the broker comes up and publishes a heartbeat");
        Check(Integration.Supervise(c, true).Online && Brokers().Length == 1, "a poll that finds the broker online starts nothing");
        var state = BrokerState.Read(c);
        Check(Integration.InRange(state.Port) && state.PairedUsers.Count == 0 && !state.Connected && state.Label.Contains("pairing"), "its state shows the port and that nobody is paired");
        Check(Brokers().Length == 1 && Exit(broker, "--config", c.ConfigPath) == Weims.Broker.Program.ExitAlreadyRunning && Brokers().Length == 1, "a second broker for the same QEMU exits at once");
        string listeners = (await ProcessRunner.Run("pwsh.exe", ["-NoProfile", "-Command", $"(Get-NetTCPConnection -State Listen -OwningProcess {Brokers()[0].Id} | ForEach-Object {{ $_.LocalAddress + ':' + $_.LocalPort }}) -join ','"], env, timeout.Token)).Trim();
        Check(listeners == "127.0.0.1:" + state.Port, "the broker listens on loopback only, on one port: " + listeners);

        await Integration.Command(c, "pair-begin");
        Check(await Eventually(() => BrokerState.Read(c) is { PairingOpen: true, PairingDigits: null }), "Manager sees the pairing window open, with no number yet");
        string token, shown;
        using (var asking = await Tls(state.Port, state.Fingerprint!))
        {
            await Hello(asking, 1, "clipboard-text");
            (shown, _) = await AskToPair(asking, state.Fingerprint!, "tester");
            Check(await Eventually(() => BrokerState.Read(c) is { PairingUser: "tester" } s && s.PairingDigits == shown), "Manager is shown the same four digits the helper computed");
            await Rejects<IOException>(() => Integration.Command(c, new JsonObject { ["command"] = "pair-confirm", ["digits"] = shown == "0000" ? "0001" : "0000" }), "an answer given for another number is refused");
            await Integration.Command(c, new JsonObject { ["command"] = "pair-confirm", ["digits"] = shown });
            var paired = await Frames.Read(asking, Frames.PreAuthLimit, timeout.Token);
            token = paired.Text("token") ?? "";
            Check(paired.Type == "paired" && await Eventually(() => BrokerState.Read(c) is { PairingOpen: false, PairingDigits: null } s && s.PairedUsers.SequenceEqual(new[] { "tester" })), "confirming in Manager pairs the helper and closes the window");
        }
        await Rejects<IOException>(() => Integration.Command(c, "pair-confirm"), "there is nothing to confirm once the pairing is done");
        using var helper = await Tls(state.Port, state.Fingerprint!); await Hello(helper, 1, "clipboard-text");
        Check((await Exchange(helper, new JsonObject { ["type"] = "auth", ["token"] = token })).Type == "ok" && await Eventually(() => BrokerState.Read(c) is { Connected: true, HelperUser: "tester" }), "the paired helper connects with its token and Manager sees who is connected");
        Check((await Frames.Read(helper, Frames.PreAuthLimit, timeout.Token)) is { Type: "focus" } focus && !focus.Flag("active"), "the helper is told its VM window is not in front");
        Check((await Integration.Command(c, "status"))["state"]!["helper_user"]!.GetValue<string>() == "tester", "the status command reports the same as the state file");
        await Rejects<IOException>(() => Integration.Command(c, "no-such-command"), "the real broker refuses unknown commands");

        Brokers()[0].Kill();
        Check(await Eventually(() => !BrokerState.Read(c).Online, 3000) && await Closed(helper), "a killed broker reads as offline although it could not say so");
        Check((await VmService.State(c)).Running, "killing the broker leaves QEMU running");
        Integration.Supervise(c, true);
        Check(await Eventually(() => BrokerState.Read(c).Online, 15000), "the next status poll brings the broker back");
        state = BrokerState.Read(c);
        using var back = await Tls(state.Port, state.Fingerprint!); await Hello(back, 1, "clipboard-text");
        Check((await Exchange(back, new JsonObject { ["type"] = "auth", ["token"] = token })).Type == "ok", "after the restart the same certificate and token still work");
        Check((await Frames.Read(back, Frames.PreAuthLimit, timeout.Token)).Type == "focus", "the reconnected helper is told the focus state again");

        await Integration.Command(c, "pair-reset");
        Check(await Closed(back) && await Eventually(() => BrokerState.Read(c) is { Online: true, PairedUsers.Count: 0 } s && s.Fingerprint != state.Fingerprint), "reset pairing drops the helper and changes the fingerprint");

        var logText = File.ReadAllText(Path.Combine(run, "broker.log"));
        Check(logText.Contains("paired with guest user tester") && logText.Contains("guest user tester asks to pair") && !logText.Contains(token) && !logText.Contains(shown), "the broker log records the pairing without the token or the number");
        var secretsText = Encoding.Latin1.GetString(File.ReadAllBytes(Integration.SecretsPath(c)));
        Check(!secretsText.Contains(token) && !secretsText.Contains("tester"), "the secrets file on disk is encrypted");

        int brokerPid = Brokers()[0].Id;
        await VmService.Power(c, "quit");
        Check(await Eventually(() => Brokers().Length == 0, 8000), "the broker exits when QEMU exits");
        var final = JsonNode.Parse(File.ReadAllText(Integration.StatePath(c)))!;
        Check(final["status"]!.GetValue<string>() == "stopped" && !BrokerState.Read(c).Online && final["qemu_pid"]!.GetValue<int>() == qemuPid && brokerPid == final["pid"]!.GetValue<int>(), "its last state says stopped");

        await StartQemu(socket: true);
        Integration.Supervise(c, true);
        Check(await Eventually(() => BrokerState.Read(c).Online, 15000), "a restarted VM gets a new broker");
        await Integration.Command(c, "stop");
        Check(await Eventually(() => Brokers().Length == 0, 8000) && (await VmService.State(c)).Running, "the stop command ends the broker and leaves QEMU running");
    }
    finally
    {
        foreach (var p in Brokers()) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        await StopQemu();
    }

}

sealed class FakeHost : IHostClipboard
{
    public ClipContent? Content; public List<ClipContent> Writes = []; public bool Fail;
    public ClipContent? Read() => Content;
    public bool Write(ClipContent content) { if (Fail) return false; Writes.Add(content); Content = content; return true; }
}
sealed class FakeLink : IHelperLink
{
    public List<(JsonObject Header, byte[] Body)> Sent = [];
    public Task<bool> Send(JsonObject header, ReadOnlyMemory<byte> body = default) { Sent.Add((header, body.ToArray())); return Task.FromResult(true); }
}
