// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

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
