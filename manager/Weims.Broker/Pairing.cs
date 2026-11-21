// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Weims.Core;

namespace Weims.Broker;

/// <summary>One helper's request to pair, from its first frame until the user has answered.</summary>
public sealed class PendingPairing(string user)
{
    public string User { get; } = user;
    /// <summary>The four digits both sides show; null until the exchange that produces them is complete.</summary>
    public string? Digits { get; internal set; }
    internal TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes with true when the user says the numbers match, false otherwise.</summary>
    public Task<bool> Decided => Decision.Task;
}

/// <summary>
/// Pairing by comparing numbers, as Bluetooth does. Nothing is typed. The user opens a pairing window
/// in Manager and chooses Pair in the helper. The two sides then derive a four-digit number from the
/// broker's certificate and a random value from each side, both show it, and the user confirms in
/// Manager that the two numbers are the same.
///
/// Why this is safe with only four digits: the helper commits to its random value (sends its hash)
/// before it sees the broker's, and the broker picks its value before the helper's is revealed. So
/// neither side, and nobody sitting between them with another certificate, can steer the number.
/// An impostor gets one try per attempt at a one-in-ten-thousand chance, and the user sees every
/// failed try as two different numbers.
/// </summary>
public sealed class PairingSession(Func<DateTime>? clock = null)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    readonly object gate = new();
    DateTime? until; int attempts; PendingPairing? pending;

    /// <summary>Opens the window in which a helper may ask to pair.</summary>
    public DateTime Open()
    {
        lock (gate)
        {
            pending?.Decision.TrySetResult(false); pending = null;
            attempts = 0; until = now() + Lifetime;
            return until.Value;
        }
    }
    public void Close() { lock (gate) { until = null; pending?.Decision.TrySetResult(false); pending = null; } }
    public DateTime? OpenUntilUtc { get { lock (gate) return until is { } u && now() < u ? u : null; } }
    /// <summary>The request whose number is on show, if any.</summary>
    public PendingPairing? Showing { get { lock (gate) return pending?.Digits != null ? pending : null; } }

    /// <summary>
    /// A helper asks to pair. Null when no window is open, when another request is in progress, or
    /// after five requests in one window; the fifth closes the window.
    /// </summary>
    public PendingPairing? Start(string user)
    {
        lock (gate)
        {
            if (until is not { } u || now() >= u) { until = null; return null; }
            if (pending != null) return null;
            if (++attempts > MaxAttempts) { until = null; return null; }
            return pending = new PendingPairing(user);
        }
    }
    public void Show(PendingPairing request, string digits) { lock (gate) if (ReferenceEquals(pending, request)) request.Digits = digits; }

    /// <summary>The user's answer in Manager. False when there is no number on show to answer for.</summary>
    public bool Decide(bool same)
    {
        lock (gate)
        {
            if (pending?.Digits == null) return false;
            var request = pending; pending = null;
            if (same) until = null;
            request.Decision.TrySetResult(same);
            return true;
        }
    }
    /// <summary>The helper went away or broke the exchange.</summary>
    public void Abandon(PendingPairing request)
    {
        lock (gate) if (ReferenceEquals(pending, request)) pending = null;
        request.Decision.TrySetResult(false);
    }

    /// <summary>The number both sides show: derived from the broker certificate's fingerprint and both random values.</summary>
    public static string Digits(string fingerprintHex, ReadOnlySpan<byte> helperNonce, ReadOnlySpan<byte> brokerNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("weims-pair-v1"));
        hash.AppendData(Convert.FromHexString(fingerprintHex));
        hash.AppendData(helperNonce);
        hash.AppendData(brokerNonce);
        return (BinaryPrimitives.ReadUInt32BigEndian(hash.GetHashAndReset()) % 10000).ToString("D4");
    }
}

/// <summary>
/// Pairing without any input, for a helper installed from the install disk that Manager hot-plugs
/// into the VM. The disk's USB serial number carries a ticket: which port the broker listens on and
/// a one-time code. The installer in the guest reads the serial number from the device tree, which
/// needs no permission; reading a file on the disk would make macOS ask the user for access to
/// removable volumes. The serial number of this kind of disk is limited to 20 characters, hence
/// the format: 'W', one character for the port, 18 characters (108 bits) of code.
///
/// The code is never sent. Helper and broker each prove that they know it, with an HMAC over the
/// certificate the connection actually uses and a random value from each side. A program in
/// between, which has another certificate, gets proofs it cannot use, and the helper learns that
/// the certificate it sees is the broker's own before it remembers it.
///
/// Every user logged in to the guest can read the serial number. Whoever uses the code first
/// pairs; that is the documented limit of this route, and the code is therefore short-lived, good
/// for one use, and void after a few wrong proofs.
/// </summary>
public sealed class InstallTicket(Func<DateTime>? clock = null)
{
    public const int MaxWrongGuesses = 5, CodeLength = 18;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    readonly object gate = new();
    byte[]? key; DateTime until; int wrong;

    /// <summary>Makes a new code and voids any earlier one. It is kept in memory only.</summary>
    public (string Code, DateTime UntilUtc) Issue()
    {
        var code = string.Create(CodeLength, 0, (span, _) => { foreach (ref var c in span) c = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]; });
        lock (gate)
        {
            key = Encoding.ASCII.GetBytes(code); wrong = 0; until = now() + Lifetime;
            return (code, until);
        }
    }
    public DateTime? ValidUntilUtc { get { lock (gate) return key != null && now() < until ? until : null; } }
    public void Cancel() { lock (gate) key = null; }

    /// <summary>The USB serial number that carries the ticket: 20 characters, the most this kind of disk may have.</summary>
    public static string Serial(int port, string code)
        => Integration.InRange(port) && code.Length == CodeLength ? "W" + Alphabet[port - Integration.PortBase] + code : throw new ArgumentException("Not a ticket.");

    /// <summary>What one side sends to prove it knows the code, bound to the certificate in use and to both random values.</summary>
    public static byte[] Mac(ReadOnlySpan<byte> key, string side, ReadOnlySpan<byte> fingerprint, ReadOnlySpan<byte> helperNonce, ReadOnlySpan<byte> brokerNonce)
    {
        using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        mac.AppendData(Encoding.ASCII.GetBytes("weims-ticket-v1-" + side));
        mac.AppendData(fingerprint); mac.AppendData(helperNonce); mac.AppendData(brokerNonce);
        return mac.GetHashAndReset();
    }

    /// <summary>The broker's proof for a helper that asks; null when no code is good.</summary>
    public byte[]? Proof(ReadOnlySpan<byte> fingerprint, ReadOnlySpan<byte> helperNonce, ReadOnlySpan<byte> brokerNonce)
    {
        lock (gate)
        {
            if (key == null || now() >= until) { key = null; return null; }
            return Mac(key, "broker", fingerprint, helperNonce, brokerNonce);
        }
    }

    /// <summary>True exactly once, for a helper's right proof inside the code's lifetime.</summary>
    public bool Redeem(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> fingerprint, ReadOnlySpan<byte> helperNonce, ReadOnlySpan<byte> brokerNonce)
    {
        lock (gate)
        {
            if (key == null || now() >= until) { key = null; return false; }
            bool right = CryptographicOperations.FixedTimeEquals(proof, Mac(key, "helper", fingerprint, helperNonce, brokerNonce));
            if (right || ++wrong >= MaxWrongGuesses) key = null;
            return right;
        }
    }
}
