// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weims.Broker;

/// <summary>One protocol message: a JSON header and an optional binary body.</summary>
public sealed record Frame(JsonObject Header, ReadOnlyMemory<byte> Body)
{
    public string Type => Text("type") ?? "";
    /// <summary>A header value as text, or null when it is missing or not a string. Never throws on guest input.</summary>
    public string? Text(string key) => Header[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public long? Number(string key) => Header[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
    public bool Flag(string key) => Header[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>
/// The wire format, carried inside TLS:
/// <code>u32 length of what follows | u32 header length | header (UTF-8 JSON object) | body</code>
/// Both lengths are big-endian. The reader checks them against a limit before it allocates anything,
/// because everything on this connection comes from the guest.
/// </summary>
public static class Frames
{
    /// <summary>Largest frame accepted before the peer has authenticated.</summary>
    public const int PreAuthLimit = 64 * 1024;
    public const int HeaderLimit = 64 * 1024;
    static readonly JsonDocumentOptions Parsing = new() { MaxDepth = 16 };

    public static async Task Write(Stream stream, JsonObject header, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        if (json.Length > HeaderLimit) throw new InvalidDataException("Frame header too large.");
        var buffer = new byte[8 + json.Length + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, checked((uint)(4 + json.Length + body.Length)));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), (uint)json.Length);
        json.CopyTo(buffer, 8);
        body.CopyTo(buffer.AsMemory(8 + json.Length));
        // One write per frame, so that frames from different senders cannot interleave below this call.
        await stream.WriteAsync(buffer, token);
        await stream.FlushAsync(token);
    }

    /// <param name="limit">Largest value of the first length field that is accepted.</param>
    public static async Task<Frame> Read(Stream stream, int limit, CancellationToken token)
    {
        var prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix, token);
        uint total = BinaryPrimitives.ReadUInt32BigEndian(prefix), headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        if (total < 4 || total > (uint)limit) throw new InvalidDataException("Frame length out of range.");
        if (headerLength == 0 || headerLength > total - 4 || headerLength > HeaderLimit) throw new InvalidDataException("Frame header length out of range.");
        var rest = new byte[total - 4];
        await stream.ReadExactlyAsync(rest, token);
        JsonNode? node;
        try { node = JsonNode.Parse(rest.AsSpan(0, (int)headerLength), null, Parsing); }
        catch (JsonException e) { throw new InvalidDataException("Frame header is not valid JSON.", e); }
        if (node is not JsonObject header) throw new InvalidDataException("Frame header is not a JSON object.");
        return new Frame(header, rest.AsMemory((int)headerLength));
    }
}
