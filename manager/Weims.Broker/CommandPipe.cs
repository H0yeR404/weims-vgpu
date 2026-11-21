// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weims.Broker;

/// <summary>
/// How Manager and the CLI give the broker orders: a named pipe that only the current Windows user can
/// open. It is a pipe and not a TCP port because the guest can reach this machine's loopback ports but
/// cannot reach a pipe. One request and one answer per connection, each a single line of JSON.
/// </summary>
public sealed class CommandPipe(string name, Func<JsonObject, JsonObject> handle, Action<string> log)
{
    const int RequestLimit = 64 * 1024;

    public async Task Run(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreateServer(name);
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                JsonObject answer;
                try
                {
                    var request = JsonNode.Parse(await ReadLine(pipe, timeout.Token)) as JsonObject ?? throw new InvalidDataException("not a JSON object");
                    answer = handle(request);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException) { answer = new JsonObject { ["ok"] = false, ["error"] = "Invalid request: " + e.Message }; }
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n"), timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                log("command pipe: " + e.Message);
                try { await Task.Delay(200, stop); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// A pipe that this Windows user owns and may open, and that nobody arriving over the network may
    /// open, whoever they log on as. The client side asks for CurrentUserOnly, which makes it check that
    /// the pipe it connected to is owned by the same user, so another account cannot pose as the broker.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.Owner ?? identity.User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    static async Task<string> ReadLine(Stream stream, CancellationToken token)
    {
        var buffer = new byte[RequestLimit]; int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (read == 0) break;
            int newline = Array.IndexOf(buffer, (byte)'\n', used, read);
            used += read;
            if (newline >= 0) return Encoding.UTF8.GetString(buffer, 0, newline);
        }
        throw new InvalidDataException("request without a line end or over 64 KiB");
    }
}
