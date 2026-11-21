// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Weims.Broker;

/// <summary>A guest helper that has been paired. Only the hash of its token is kept.</summary>
public sealed record PairedHelper(string Id, string User, string TokenSha256, string PairedUtc);

/// <summary>
/// The broker's long-lived secrets for one VM: its TLS identity and the tokens of paired helpers.
/// They are stored in the VM's state folder, encrypted with DPAPI for the current Windows user.
/// A copy of the VM folder on another account or machine cannot be decrypted; the broker then starts
/// with a new identity and the guest has to pair again.
/// </summary>
public sealed class BrokerSecrets
{
    sealed record Stored(string Certificate, string PrivateKey, List<PairedHelper> Helpers);
    static readonly byte[] Entropy = Encoding.ASCII.GetBytes("weims-broker-secrets-v1");

    readonly object gate = new();
    byte[] certificate, privateKey;
    readonly List<PairedHelper> helpers;
    public string FilePath { get; }
    /// <summary>True when an existing secrets file could not be read and a new identity replaced it.</summary>
    public bool Replaced { get; }

    BrokerSecrets(string path, byte[] certificate, byte[] privateKey, List<PairedHelper> helpers, bool replaced)
    { FilePath = path; this.certificate = certificate; this.privateKey = privateKey; this.helpers = helpers; Replaced = replaced; }

    public static BrokerSecrets LoadOrCreate(string path)
    {
        bool existed = File.Exists(path);
        if (existed)
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(Dpapi.Unprotect(File.ReadAllBytes(path), Entropy))!;
                var loaded = new BrokerSecrets(path, Convert.FromBase64String(stored.Certificate), Convert.FromBase64String(stored.PrivateKey), stored.Helpers ?? [], false);
                using var check = loaded.ServerCertificate();
                return loaded;
            }
            catch (Exception e) when (e is CryptographicException or Win32Exception or JsonException or FormatException or NullReferenceException or ArgumentException) { }
        }
        var (der, key) = NewIdentity();
        var created = new BrokerSecrets(path, der, key, [], existed);
        created.Save();
        return created;
    }

    static (byte[] Certificate, byte[] PrivateKey) NewIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Weims Broker", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse("10.0.2.2")); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        // The helper pins this certificate by fingerprint and does no chain or date validation, so the
        // long lifetime is not relied on for trust. A new certificate is made when pairing is reset.
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return (cert.RawData, key.ExportPkcs8PrivateKey());
    }

    public void Save()
    {
        byte[] plain;
        lock (gate) plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(Convert.ToBase64String(certificate), Convert.ToBase64String(privateKey), [.. helpers]));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, Dpapi.Protect(plain, Entropy));
            try { File.Move(temp, FilePath, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>SHA-256 of the certificate, lowercase hex. This is what a helper pins.</summary>
    public string Fingerprint { get { lock (gate) return Convert.ToHexString(SHA256.HashData(certificate)).ToLowerInvariant(); } }

    /// <summary>
    /// The certificate with its private key in a form Windows TLS can use. Windows cannot serve TLS from
    /// a key that exists only in memory, so the pair goes through a PKCS#12 import; the key container
    /// that import creates is temporary and is removed when the certificate is disposed.
    /// </summary>
    public X509Certificate2 ServerCertificate()
    {
        byte[] der, pkcs8;
        lock (gate) { der = certificate; pkcs8 = privateKey; }
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        using var pub = X509CertificateLoader.LoadCertificate(der);
        using var pair = pub.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pkcs12), null);
    }

    public IReadOnlyList<string> PairedUsers { get { lock (gate) return helpers.Select(h => h.User).Distinct().ToArray(); } }

    /// <summary>Registers a helper and returns its token. A user who pairs again replaces their earlier pairing.</summary>
    public string AddHelper(string user)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (gate)
        {
            helpers.RemoveAll(h => h.User == user);
            helpers.Add(new(Guid.NewGuid().ToString("N"), user, Hash(token), DateTime.UtcNow.ToString("o")));
        }
        Save();
        return token;
    }

    public PairedHelper? Match(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var hash = Convert.FromHexString(Hash(token));
        PairedHelper? found = null;
        lock (gate)
            // Compare against every entry in constant time; do not stop at the first match.
            foreach (var h in helpers)
                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(h.TokenSha256), hash)) found = h;
        return found;
    }

    /// <summary>Revokes every token and replaces the certificate, so every helper has to pair again.</summary>
    public void Reset()
    {
        var (der, key) = NewIdentity();
        lock (gate) { CryptographicOperations.ZeroMemory(privateKey); certificate = der; privateKey = key; helpers.Clear(); }
        Save();
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Windows data protection for the current user, without UI.</summary>
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    const int UiForbidden = 0x1;
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);
    static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned); var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var a = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var e = new Blob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref a, null, ref e, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                              : CryptUnprotectData(ref a, IntPtr.Zero, ref e, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { input.Free(); salt.Free(); }
    }
}
