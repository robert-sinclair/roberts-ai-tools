using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using rMic.Core;
using rMic.Core.Ipc;

namespace rMic.Service;

/// <summary>
/// Named-pipe server the tray client talks to. The pipe carries an explicit ACL because the
/// service runs as LocalSystem, whose default DACL would shut out the ordinary user account
/// the tray runs under.
/// </summary>
public sealed class ControlServer
{
    private const int MaxServerInstances = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Logger _log;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlResponse>> _handler;

    public ControlServer(Logger log, Func<ControlRequest, CancellationToken, Task<ControlResponse>> handler)
    {
        _log = log;
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.Info($@"Listening on \\.\pipe\{Protocol.PipeName}.");

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var accepted = pipe;
                pipe = null; // ownership moves to the serving task
                _ = Task.Run(() => ServeAsync(accepted, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Pipe accept loop failed; retrying in one second", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        _log.Info("Control server stopped.");
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Anyone logged on at the console or over RDP may flip their own microphone.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            Protocol.PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);

            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

            ControlResponse response;
            if (string.IsNullOrWhiteSpace(line))
            {
                response = ControlResponse.Failure("Empty request.");
            }
            else
            {
                try
                {
                    var request = JsonSerializer.Deserialize<ControlRequest>(line, Protocol.Json)
                                  ?? new ControlRequest();
                    response = await _handler(request, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("Request handler failed", ex);
                    response = ControlResponse.Failure(ex.Message);
                }
            }

            await using var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(response, Protocol.Json);
            await writer.WriteLineAsync(payload.AsMemory(), cts.Token).ConfigureAwait(false);

            pipe.WaitForPipeDrain();
        }
        catch (OperationCanceledException)
        {
            // Client gave up or the service is shutting down.
        }
        catch (IOException)
        {
            // Client disconnected mid-exchange; nothing useful to say.
        }
        catch (Exception ex)
        {
            _log.Error("Failed to serve a control request", ex);
        }
        finally
        {
            try
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
            catch
            {
                // Disconnect races with client teardown; not worth reporting.
            }
            pipe.Dispose();
        }
    }
}
