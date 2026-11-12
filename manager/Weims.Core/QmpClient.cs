// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weims.Core;

public sealed class QmpClient : IDisposable
{
    readonly TcpClient client = new();
    StreamReader reader = null!;
    StreamWriter writer = null!;
    public static async Task<QmpClient> Connect(int port, CancellationToken token)
    {
        var q = new QmpClient();
        try
        {
            await q.client.ConnectAsync("127.0.0.1", port, token);
            q.reader = new(q.client.GetStream(), Encoding.UTF8, false, 4096, true);
            q.writer = new(q.client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
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
    public async Task<JsonNode?> Execute(string command, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, id }).AsMemory(), token);
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
        if (name != expected) throw new IOException(Text.Get("QMP 端口属于另一台虚拟机，操作已停止。", "The QMP port belongs to another VM. Operation stopped."));
    }
    public void Dispose() { writer?.Dispose(); reader?.Dispose(); client.Dispose(); }
}
