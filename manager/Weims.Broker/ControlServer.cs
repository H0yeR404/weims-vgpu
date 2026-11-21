// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Weims.Core;

namespace Weims.Broker;

/// <summary>Something a frame can be sent to. The clipboard logic only knows this much about a helper.</summary>
public interface IHelperLink
{
    Task<bool> Send(JsonObject header, ReadOnlyMemory<byte> body = default);
}

/// <summary>An authenticated control connection from a guest helper.</summary>
public sealed class HelperSession(Stream stream, string user, string version, IReadOnlySet<string> capabilities) : IHelperLink
{
    static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    readonly SemaphoreSlim writing = new(1, 1);
    public string User { get; } = user;
    public string Version { get; } = version;
    public IReadOnlySet<string> Capabilities { get; } = capabilities;

    /// <summary>Sends one frame. A helper that does not take it within ten seconds is disconnected.</summary>
    public async Task<bool> Send(JsonObject header, ReadOnlyMemory<byte> body = default)
    {
        try
        {
            await writing.WaitAsync();
            try { using var timeout = new CancellationTokenSource(WriteTimeout); await Frames.Write(stream, header, body, timeout.Token); return true; }
            finally { writing.Release(); }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { Close(); return false; }
    }
    public void Close() { try { stream.Dispose(); } catch (IOException) { } }
}

/// <summary>
/// The broker's listener for guest helpers. The guest reaches it at 10.0.2.2, which QEMU's user-mode
/// network maps to this machine's loopback; any process in any running VM can therefore connect, and
/// so can any local process. Nothing is trusted until the peer has shown a token or has been paired
/// inside TLS, and until then the connection is held to small frames, a short deadline and a low count.
/// </summary>
public sealed class ControlServer
{
    public static readonly string[] Capabilities = ["clipboard-text", HelperUpdate.Capability];
    public const int ProtocolVersion = 1;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxUnauthenticated { get; init; } = 8;
    public int FrameLimit { get; init; } = ClipboardSync.TextLimit + Frames.HeaderLimit + 4;
    /// <summary>How long a helper's number stays on show waiting for the user's answer.</summary>
    public TimeSpan PairingDecisionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    readonly TcpListener listener;
    readonly BrokerSecrets secrets;
    readonly PairingSession pairing;
    readonly BrokerStatus status;
    readonly Action<string> log;
    /// <summary>The one-time code for a helper installed from the install image.</summary>
    public InstallTicket Ticket { get; }
    X509Certificate2 certificate;
    int unauthenticated;
    HelperSession? current;

    public Action<HelperSession>? Connected { get; set; }
    public Action<HelperSession>? Disconnected { get; set; }
    public Action<HelperSession, Frame>? Received { get; set; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public ControlServer(TcpListener listener, BrokerSecrets secrets, PairingSession pairing, BrokerStatus status, Action<string> log, InstallTicket? ticket = null)
    {
        this.listener = listener; this.secrets = secrets; this.pairing = pairing; this.status = status; this.log = log;
        Ticket = ticket ?? new InstallTicket();
        certificate = secrets.ServerCertificate();
        status.Update(s => { s.Port = Port; s.Fingerprint = secrets.Fingerprint; s.PairedUsers = secrets.PairedUsers; });
    }

    /// <summary>Listens on the preferred port, or on the next free one in the integration range.</summary>
    public static TcpListener Bind(int preferred)
    {
        if (!Integration.InRange(preferred)) preferred = Integration.PortBase;
        for (int i = 0; i < Integration.PortCount; i++)
        {
            var candidate = new TcpListener(IPAddress.Loopback, Integration.PortBase + (preferred - Integration.PortBase + i) % Integration.PortCount) { ExclusiveAddressUse = true };
            try { candidate.Start(); return candidate; }
            catch (SocketException) { candidate.Dispose(); }
        }
        throw new IOException(Text.Get($"端口 {Integration.PortBase}–{Integration.PortBase + Integration.PortCount - 1} 全部被占用。", $"All ports {Integration.PortBase}–{Integration.PortBase + Integration.PortCount - 1} are in use."));
    }

    public async Task Run(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => Handle(client, stop), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        // Disposing the certificate removes the temporary key container its import created.
        finally { listener.Stop(); Volatile.Read(ref current)?.Close(); certificate.Dispose(); }
    }

    /// <summary>After a pairing reset: serve the new certificate and drop whoever is connected.</summary>
    public void ReloadIdentity()
    {
        var old = certificate;
        certificate = secrets.ServerCertificate();
        status.Update(s => { s.Fingerprint = secrets.Fingerprint; s.PairedUsers = secrets.PairedUsers; });
        Volatile.Read(ref current)?.Close();
        old.Dispose();
    }

    async Task Handle(TcpClient client, CancellationToken stop)
    {
        using var owned = client;
        bool counted = true;
        if (Interlocked.Increment(ref unauthenticated) > MaxUnauthenticated) { Interlocked.Decrement(ref unauthenticated); return; }
        HelperSession? session = null;
        try
        {
            client.NoDelay = true;
            using var tls = new SslStream(client.GetStream(), false);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                deadline.CancelAfter(HandshakeTimeout);
                session = await Authenticate(tls, deadline.Token, stop);
            }
            counted = false; Interlocked.Decrement(ref unauthenticated);
            if (session == null) return;
            // One helper at a time. The newest wins: a helper that was killed may leave a dead connection behind.
            Interlocked.Exchange(ref current, session)?.Close();
            status.Update(s => { s.HelperUser = session.User; s.HelperVersion = session.Version; s.HelperSinceUtc = DateTime.UtcNow; s.PairedUsers = secrets.PairedUsers; });
            log($"helper connected: user={session.User} version={session.Version}");
            Connected?.Invoke(session);
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(IdleTimeout);
                var frame = await Frames.Read(tls, FrameLimit, idle.Token);
                if (frame.Type == "ping") await session.Send(new JsonObject { ["type"] = "pong" });
                // The helper has unmounted the install volume; Manager may now unplug it (the broker never talks to QMP).
                else if (frame.Type == "install_media") { if (frame.Flag("ejected")) { status.Update(s => s.InstallMediaEjected = true); log("the guest ejected the install volume"); } }
                else Received?.Invoke(session, frame);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            if (session != null && e is not (EndOfStreamException or OperationCanceledException or ObjectDisposedException)) log("helper connection ended: " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref unauthenticated);
            if (session != null)
            {
                if (Interlocked.CompareExchange(ref current, null, session) == session)
                    status.Update(s => { s.HelperUser = null; s.HelperVersion = null; s.HelperSinceUtc = null; });
                Disconnected?.Invoke(session);
                log($"helper disconnected: user={session.User}");
            }
        }
    }

    /// <summary>TLS, version exchange, then a token or a request to pair. Returns null when there is no session to run.</summary>
    async Task<HelperSession?> Authenticate(SslStream tls, CancellationToken token, CancellationToken stop)
    {
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate, ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, token);

        var hello = await Frames.Read(tls, Frames.PreAuthLimit, token);
        long theirs = hello.Number("version") ?? 0;
        if (hello.Type != "hello" || theirs < 1 || theirs > int.MaxValue) return await Refuse(tls, "protocol", token);
        var offered = hello.Header["capabilities"] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet() : [];
        var shared = Capabilities.Where(offered.Contains).ToHashSet();
        var helperVersion = Clean(hello.Text("helper"), 32);
        await Frames.Write(tls, new JsonObject
        {
            ["type"] = "hello", ["version"] = (int)Math.Min(ProtocolVersion, theirs),
            ["capabilities"] = new JsonArray([.. shared.Select(c => (JsonNode)c)]), ["broker"] = BrokerStatus.Version
        }, default, token);

        var credentials = await Frames.Read(tls, Frames.PreAuthLimit, token);
        string user;
        if (credentials.Type == "auth")
        {
            if (secrets.Match(credentials.Text("token")) is not { } helper) return await Refuse(tls, "auth", token);
            user = helper.User;
            await Frames.Write(tls, new JsonObject { ["type"] = "ok", ["user"] = user }, default, token);
        }
        // A pairing connection only pairs: the helper stores what it was given and connects again with its token.
        else if (credentials.Type == "pair_begin") { await Pair(tls, credentials, token, stop); return null; }
        else if (credentials.Type == "ticket_begin") { await PairByTicket(tls, credentials, token); return null; }
        else return await Refuse(tls, "protocol", token);
        return new HelperSession(tls, user, helperVersion, shared);
    }

    /// <summary>
    /// Pairing by number comparison (see <see cref="PairingSession"/>): the helper has sent the hash of
    /// its random value; the broker answers with its own value, the helper reveals, both show the
    /// resulting number, and the user's answer in Manager decides.
    /// </summary>
    async Task Pair(SslStream tls, Frame begin, CancellationToken handshake, CancellationToken stop)
    {
        byte[]? commitment = Bytes(begin.Text("commit"));
        var user = Clean(begin.Text("user"), 64);
        var request = commitment == null ? null : pairing.Start(user);
        PublishPairing();
        if (request == null) { await Refuse(tls, "pair", handshake); return; }
        try
        {
            var mine = RandomNumberGenerator.GetBytes(32);
            await Frames.Write(tls, new JsonObject { ["type"] = "pair_nonce", ["nonce"] = Convert.ToHexString(mine).ToLowerInvariant() }, default, handshake);
            var reveal = await Frames.Read(tls, Frames.PreAuthLimit, handshake);
            byte[]? theirs = Bytes(reveal.Text("nonce"));
            if (reveal.Type != "pair_reveal" || theirs == null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(theirs), commitment)) { await Refuse(tls, "pair", handshake); return; }
            pairing.Show(request, PairingSession.Digits(secrets.Fingerprint, theirs, mine));
            PublishPairing();
            log($"guest user {user} asks to pair; waiting for the answer in Manager");

            // Wait for the user's answer, for the helper to give up, or for too long. The read is only
            // there to notice the helper leaving; a pairing connection carries nothing after this.
            var gone = Frames.Read(tls, Frames.PreAuthLimit, stop);
            _ = gone.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var first = await Task.WhenAny(request.Decided, gone, Task.Delay(PairingDecisionTimeout, stop));
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(stop); reply.CancelAfter(TimeSpan.FromSeconds(5));
            if (first != request.Decided || !request.Decided.Result)
            {
                log(first == request.Decided ? $"pairing with guest user {user} was declined in Manager" : $"pairing with guest user {user} was abandoned");
                if (first != gone) await Refuse(tls, first == request.Decided ? "pair-declined" : "pair-timeout", reply.Token);
                return;
            }
            var issued = secrets.AddHelper(user);
            status.Update(s => s.PairedUsers = secrets.PairedUsers);
            log($"paired with guest user {user}");
            await Frames.Write(tls, new JsonObject { ["type"] = "paired", ["token"] = issued, ["user"] = user }, default, reply.Token);
        }
        finally { pairing.Abandon(request); PublishPairing(); }
    }

    /// <summary>
    /// Pairing through the install disk (see <see cref="InstallTicket"/>): both sides prove that they know the
    /// one-time code from the disk's serial number, the broker first, and nobody has to confirm anything.
    /// </summary>
    async Task PairByTicket(SslStream tls, Frame begin, CancellationToken token)
    {
        var fingerprint = Convert.FromHexString(secrets.Fingerprint);
        var mine = RandomNumberGenerator.GetBytes(32);
        byte[]? theirs = Bytes(begin.Text("nonce"));
        var proof = theirs == null ? null : Ticket.Proof(fingerprint, theirs, mine);
        PublishPairing();
        if (proof == null) { await Refuse(tls, "ticket", token); return; }
        await Frames.Write(tls, new JsonObject { ["type"] = "ticket_challenge", ["nonce"] = Convert.ToHexString(mine).ToLowerInvariant(), ["proof"] = Convert.ToHexString(proof).ToLowerInvariant() }, default, token);
        var answer = await Frames.Read(tls, Frames.PreAuthLimit, token);
        var user = Clean(answer.Text("user"), 64);
        bool redeemed = answer.Type == "ticket_proof" && Bytes(answer.Text("proof")) is { } theirProof && Ticket.Redeem(theirProof, fingerprint, theirs, mine);
        PublishPairing();
        if (!redeemed) { log($"guest user {user} did not prove the install code: it is wrong, used or expired"); await Refuse(tls, "ticket", token); return; }
        var issued = secrets.AddHelper(user);
        status.Update(s => s.PairedUsers = secrets.PairedUsers);
        log($"paired with guest user {user} through the install disk");
        await Frames.Write(tls, new JsonObject { ["type"] = "paired", ["token"] = issued, ["user"] = user }, default, token);
    }

    /// <summary>Copies the pairing state into what Manager sees.</summary>
    public void PublishPairing()
    {
        var showing = pairing.Showing; var until = pairing.OpenUntilUtc; var ticket = Ticket.ValidUntilUtc;
        status.Update(s => { s.PairingUntilUtc = until; s.PairingDigits = showing?.Digits; s.PairingUser = showing?.User; s.InstallUntilUtc = ticket; });
    }

    static byte[]? Bytes(string? hex)
    {
        if (hex is not { Length: 64 }) return null;
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }

    static async Task<HelperSession?> Refuse(SslStream tls, string code, CancellationToken token)
    {
        await Frames.Write(tls, new JsonObject { ["type"] = "error", ["code"] = code }, default, token);
        return null;
    }

    /// <summary>Guest-supplied text that ends up in logs and in Manager: printable, bounded, never empty.</summary>
    static string Clean(string? value, int max)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();
        return text.Length == 0 ? "unknown" : text;
    }
}
