// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weims.Core;

/// <summary>
/// Where a VM's QMP control channel lives. QMP has no authentication, and QEMU's user-mode network
/// forwards guest connections for 10.0.2.2 to the host's 127.0.0.1, so a TCP listener on loopback is
/// reachable from inside the guest. A Unix domain socket is a file path: the guest network cannot
/// reach it, and the folder's permissions keep other local accounts out.
/// </summary>
public static class QmpEndpoint
{
    // QEMU and .NET both limit a Unix socket path to 108 bytes; stay clear of the edge and its terminator.
    public const int MaxPathBytes = 104;
    public const string FolderOverride = "WEIMS_QMP_DIR";

    /// <summary>
    /// A short per-user folder that inherits the private permissions of the user profile.
    /// It is deliberately not under AppData: for a process started from a packaged (MSIX) app, Windows
    /// redirects AppData through a filter, and a socket there cannot be connected to (WSAEINVAL).
    /// The profile root is not redirected.
    /// </summary>
    public static string Folder()
    {
        var custom = Environment.GetEnvironmentVariable(FolderOverride);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weims", "q")
            : custom);
    }

    /// <summary>The socket path for one launch. <paramref name="runName"/> is unique per start, so the path is too.</summary>
    public static string PathFor(string runName, string? folder = null)
    {
        var file = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runName)), 0, 6).ToLowerInvariant() + ".sock";
        var path = Path.Combine(folder ?? Folder(), file);
        int bytes = Encoding.UTF8.GetByteCount(path);
        if (bytes > MaxPathBytes)
            throw new InvalidDataException(Text.Get(
                $"虚拟机控制通道的路径过长（{bytes} 字节，上限 {MaxPathBytes}）：{path}。请把环境变量 {FolderOverride} 设为一个只有你能访问的短路径文件夹后重试。",
                $"The VM control socket path is too long ({bytes} bytes, limit {MaxPathBytes}): {path}. Set the {FolderOverride} environment variable to a short folder that only you can access, then try again."));
        return path;
    }

    /// <summary>Creates the folder and removes the socket a previous, no longer running launch left behind.</summary>
    public static void Prepare(string socket, string? previous)
    {
        var folder = Path.GetDirectoryName(socket)!;
        Directory.CreateDirectory(folder);
        if (string.IsNullOrEmpty(previous)) return;
        try
        {
            // Only ever delete a file this code could have created: a .sock directly inside the same folder.
            var full = Path.GetFullPath(previous);
            if (full.EndsWith(".sock", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}

public sealed class QmpClient : IDisposable
{
    readonly Socket socket;
    NetworkStream? stream;
    StreamReader reader = null!;
    StreamWriter writer = null!;
    QmpClient(Socket socket) => this.socket = socket;

    /// <summary>Connects to a VM started by this version: QMP on a Unix domain socket.</summary>
    public static Task<QmpClient> Connect(string socketPath, CancellationToken token) => Open(new UnixDomainSocketEndPoint(socketPath), token);

    /// <summary>
    /// Connects over loopback TCP. Kept only for a VM that an older version started and that is still
    /// running; this version never asks QEMU to listen on TCP.
    /// </summary>
    public static Task<QmpClient> Connect(int port, CancellationToken token) => Open(new IPEndPoint(IPAddress.Loopback, port), token);

    static async Task<QmpClient> Open(EndPoint endpoint, CancellationToken token)
    {
        var q = new QmpClient(endpoint is IPEndPoint
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified));
        try
        {
            await q.socket.ConnectAsync(endpoint, token);
            q.stream = new NetworkStream(q.socket, ownsSocket: false);
            q.reader = new(q.stream, Encoding.UTF8, false, 4096, true);
            q.writer = new(q.stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
            var greeting = await q.Read(token);
            if (greeting["QMP"] == null) throw new IOException(Text.Get("无效的 QMP 握手。", "Invalid QMP greeting."));
            await q.Execute("qmp_capabilities", token);
            return q;
        } catch { q.Dispose(); throw; }
    }
    async Task<JsonObject> Read(CancellationToken token)
    {
        var line = await reader.ReadLineAsync(token) ?? throw new IOException(Text.Get("QMP 已断开。", "QMP disconnected."));
        return JsonNode.Parse(line)?.AsObject() ?? throw new IOException(Text.Get("空 QMP 响应。", "Empty QMP response."));
    }
    public Task<JsonNode?> Execute(string command, CancellationToken token) => Execute(command, null, token);
    public async Task<JsonNode?> Execute(string command, JsonObject? arguments, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject { ["execute"] = command, ["id"] = id };
        if (arguments != null) request["arguments"] = arguments;
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), token);
        for (int i = 0; i < 256; i++)
        {
            var response = await Read(token);
            if (response["id"]?.GetValue<string>() != id) continue;
            if (response["error"] != null) throw new IOException("QMP：" + response["error"]!["desc"]);
            return response["return"]?.DeepClone();
        }
        throw new IOException(Text.Get("QMP 响应过多，未收到匹配的请求结果。", "Too many QMP responses without a matching request result."));
    }
    public async Task VerifyName(string expected, CancellationToken token)
    {
        var name = (await Execute("query-name", token))?["name"]?.GetValue<string>();
        if (name != expected) throw new IOException(Text.Get("QMP 通道属于另一台虚拟机，操作已停止。", "The QMP channel belongs to another VM. Operation stopped."));
    }
    public void Dispose() { writer?.Dispose(); reader?.Dispose(); stream?.Dispose(); socket.Dispose(); }
}
