// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;

namespace Weims.Broker;

/// <summary>
/// Holds what the guest sends to this machine to a number of bytes per second, for a setup in which fast
/// uploads make the guest's picture stutter. Slowing the receiver is enough: whoever takes the guest's data calls
/// <see cref="Spend"/> for each piece and is made to wait, so the guest's TCP connection fills up and the
/// guest sends slower. One limit is shared by everything that receives from one VM.
/// </summary>
public sealed class RateLimit(long bytesPerSecond, Func<TimeSpan>? clock = null, Action<TimeSpan>? wait = null)
{
    /// <summary>Shorter waits are carried over instead: a sleep cannot be that short, and small writes would crawl.</summary>
    public static readonly TimeSpan ShortestWait = TimeSpan.FromMilliseconds(20);
    /// <summary>
    /// How much unused allowance is kept: a quarter of a second's worth. A piece takes time to arrive, and
    /// without credit for that time the sender would stay under the limit (measured with a guest: 9 MiB/s at 12).
    /// </summary>
    public static readonly TimeSpan Credit = TimeSpan.FromMilliseconds(250);
    static readonly long Start = Stopwatch.GetTimestamp();
    readonly Func<TimeSpan> clock = clock ?? (() => Stopwatch.GetElapsedTime(Start));
    readonly Action<TimeSpan> wait = wait ?? Thread.Sleep;
    readonly object gate = new();
    long rate = bytesPerSecond;
    double ahead;       // bytes taken beyond what the rate has allowed so far; negative while there is credit
    TimeSpan last;

    /// <summary>Bytes per second; 0 or less for no limit.</summary>
    public long BytesPerSecond { get => Volatile.Read(ref rate); set => Volatile.Write(ref rate, value); }

    /// <summary>Accounts for bytes that were received and returns once the sender may go on.</summary>
    public void Spend(int bytes)
    {
        TimeSpan pause;
        lock (gate)
        {
            long perSecond = BytesPerSecond; var now = clock();
            if (perSecond <= 0) { ahead = 0; last = now; return; }
            ahead = Math.Max(-Credit.TotalSeconds * perSecond, ahead - (now - last).TotalSeconds * perSecond) + bytes;
            last = now;
            pause = TimeSpan.FromSeconds(Math.Max(0, ahead) / perSecond);
        }
        if (pause >= ShortestWait) wait(pause);
    }
}
