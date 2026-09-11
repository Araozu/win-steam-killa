using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Shared;

namespace Service;

// Tiny JSON-over-named-pipe server. One request per connection.
// The service owns a TimerEngine instance and delegates to it.
public class PipeServer
{
    private readonly TimerEngine _engine;
    private readonly ILogger<PipeServer> _log;

    public PipeServer(TimerEngine engine, ILogger<PipeServer> log)
    {
        _engine = engine;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Ownership of each accepted instance transfers to HandleOneAsync,
            // which disposes it. Never dispose here: the previous code had
            // `using` in this loop, killing connections mid-request.
            NamedPipeServerStream? server = null;
            try
            {
                // Explicit ACL: the service runs as SYSTEM but the UI runs as
                // you (non-elevated). Default pipe security would lock you out.
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                server = NamedPipeServerStreamAcl.Create(
                    Limits.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, pipeSecurity: ps,
                    inheritability: HandleInheritability.None,
                    additionalAccessRights: (PipeAccessRights)0);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleOneAsync(server, ct);
                server = null; // handed off
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "pipe accept failed");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleOneAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;
                var req = JsonSerializer.Deserialize<PipeRequest>(line);
                PipeResponse resp = _engine.Handle(req);
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp)).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogDebug(ex, "pipe handle failed"); }
        }
    }
}
