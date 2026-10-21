using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace rMic.Core.Ipc;

/// <summary>Client half of the tray-to-service channel. One request/response per connection.</summary>
public static class ControlClient
{
    public static async Task<ControlResponse> SendAsync(
        ControlCommand command,
        string? instanceId = null,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            Protocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // The service has no business impersonating the caller.
            TokenImpersonationLevel.None);

        await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using (var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { AutoFlush = true })
        {
            var payload = JsonSerializer.Serialize(ControlRequest.For(command, instanceId), Protocol.Json);
            await writer.WriteLineAsync(payload.AsMemory(), cts.Token).ConfigureAwait(false);
        }

        using var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("The rMic service closed the connection without replying.");

        return JsonSerializer.Deserialize<ControlResponse>(line, Protocol.Json)
               ?? throw new IOException("The rMic service sent an unreadable reply.");
    }
}
