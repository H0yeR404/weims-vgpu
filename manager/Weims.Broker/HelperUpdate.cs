// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Weims.Broker;

/// <summary>
/// Keeps the helper in the guest current without the install disk. Manager ships the helper as an
/// archive next to the broker. When a helper that announces "self-update" connects with an older
/// version, the broker sends it the archive in one frame over the authenticated connection.
///
/// The broker decides nothing about trust here. The helper installs the archive only if the app in it
/// is validly signed by the signer of the helper that is running, and is newer; a helper built with
/// an ad-hoc signature therefore never updates itself. The hash in the frame only catches damage.
/// </summary>
public sealed class HelperUpdate
{
    public const string Capability = "self-update";
    const string Prefix = "weims-guest-tools-helper-", Suffix = ".tar";
    readonly byte[] archive;
    readonly string sha256;
    // Helpers that answered no in this run, so that every reconnect does not send the archive again.
    readonly HashSet<string> declined = new(StringComparer.Ordinal);
    public string Version { get; }

    HelperUpdate(string version, byte[] archive)
    {
        Version = version; this.archive = archive;
        sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    }

    /// <summary>The newest helper archive in the folder, or null when there is none that fits into one frame.</summary>
    public static HelperUpdate? Load(string directory, int limit = ClipboardSync.TextLimit)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            string? best = null, bestPath = null;
            foreach (var path in Directory.GetFiles(directory, Prefix + "*" + Suffix))
            {
                var version = Path.GetFileName(path)[Prefix.Length..^Suffix.Length];
                if (Parse(version) != null && (best == null || Newer(version, best))) { best = version; bestPath = path; }
            }
            if (bestPath == null || new FileInfo(bestPath).Length is 0 or > int.MaxValue || new FileInfo(bestPath).Length > limit) return null;
            return new HelperUpdate(best!, File.ReadAllBytes(bestPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static int[]? Parse(string version)
    {
        var parts = version.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i])) return null;
        return parts.Length is > 0 and <= 4 ? numbers : null;
    }

    /// <summary>Compares dotted numbers ("0.10.0" is newer than "0.9.3"). Anything else is never newer and never older.</summary>
    public static bool Newer(string candidate, string than)
    {
        if (Parse(candidate) is not { } a || Parse(than) is not { } b) return false;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public bool Wanted(HelperSession helper)
        => helper.Capabilities.Contains(Capability) && Newer(Version, helper.Version) && !declined.Contains(helper.User + "\n" + helper.Version);

    public Task<bool> Send(IHelperLink helper)
        => helper.Send(new JsonObject { ["type"] = "update", ["version"] = Version, ["sha256"] = sha256 }, archive);

    public void Declined(HelperSession helper) { lock (declined) declined.Add(helper.User + "\n" + helper.Version); }
}
