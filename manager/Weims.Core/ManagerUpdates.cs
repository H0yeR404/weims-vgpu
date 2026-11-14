// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weims.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Preview) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string value)
    {
        var m = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!m.Success) throw new FormatException("Invalid release version: " + value);
        var pre = m.Groups[4].Success ? m.Groups[4].Value : null;
        if (pre?.Split('.').Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsDigit)) == true) throw new FormatException("Invalid prerelease number.");
        return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), pre);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        foreach (var c in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (c != 0) return c;
        if (Preview == null || other.Preview == null) return Preview == other.Preview ? 0 : Preview == null ? 1 : -1;
        var a = Preview.Split('.'); var b = other.Preview.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = BigInteger.TryParse(a[i], out var ai), bn = BigInteger.TryParse(b[i], out var bi);
            int c = an && bn ? ai.CompareTo(bi) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}

public sealed record ManagerRelease(string Tag, ReleaseVersion Version, string Notes, string AssetName, string DownloadUrl, long Size, string Sha256);

public sealed class ManagerUpdates : IDisposable
{
    public const string Repository = "H0yeR404/weims-vgpu";
    readonly HttpClient http;
    public ManagerUpdates(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Weims-Manager-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public static ManagerRelease? Select(string json, string current)
    {
        var installed = ReleaseVersion.Parse(current);
        using var doc = JsonDocument.Parse(json);
        ManagerRelease? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString()!;
            ReleaseVersion version;
            try { version = ReleaseVersion.Parse(tag); } catch (Exception ex) when (ex is FormatException or OverflowException) { continue; }
            if (installed.Preview == null && (version.Preview != null || release.GetProperty("prerelease").GetBoolean())) continue;
            if (version.CompareTo(installed) <= 0 || best != null && version.CompareTo(best.Version) <= 0) continue;
            string name = $"Weims-Manager-{tag.TrimStart('v')}-Setup-x64.exe";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                string url = asset.GetProperty("browser_download_url").GetString()!;
                if (!TrustedAsset(url, tag, name)) throw new InvalidDataException("Unexpected update download URL.");
                string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                // Never offer executable updates without GitHub's published SHA-256 digest.
                if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException(Text.Get("发布包缺少 SHA256 校验信息，请通过 GitHub 手动检查。", "Release has no SHA256 digest. Please review it on GitHub."));
                long size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid installer size.");
                best = new(tag, version, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", name, url, size, digest[7..]);
            }
        }
        return best;
    }
    static bool TrustedAsset(string url, string tag, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" &&
        Uri.UnescapeDataString(uri.AbsolutePath) == $"/{Repository}/releases/download/{tag}/{name}";

    public async Task<ManagerRelease?> Check(string current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ManagerRelease? best = null;
        for (int page = 1; page <= 10; page++)
        {
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}", timeout.Token);
            var candidate = Select(json, current);
            if (candidate != null && (best == null || candidate.Version.CompareTo(best.Version) > 0)) best = candidate;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() < 100) return best;
        }
        throw new InvalidDataException("Release listing is too large; check GitHub directly.");
    }
    public async Task<string> Download(ManagerRelease release, string directory, IProgress<double>? progress, CancellationToken token)
    {
        if (!TrustedAsset(release.DownloadUrl, release.Tag, release.AssetName) || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid release metadata.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + release.AssetName);
        string partial = path + ".partial";
        try
        {
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    total += read; if (total > release.Size) throw new InvalidDataException("Installer exceeds advertised size.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token); progress?.Report(total * 100.0 / release.Size);
                }
                if (total != release.Size) throw new InvalidDataException("Incomplete installer download.");
            }
            await Verify(partial, release.Sha256, token);
            File.Move(partial, path); return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public static async Task Verify(string path, string expected, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Text.Get("更新包 SHA256 校验失败，未执行安装。", "Update SHA256 verification failed. Installation was not started."));
    }
    public void Dispose() => http.Dispose();
}
