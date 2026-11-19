using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Shared;

namespace Service;

// Tiny JSON-over-named-pipe server. One request per connection.
// The service owns a TimerEngine instance and delegates to it.
internal sealed class PipeServer
{
    private readonly TimerEngine _engine;
    private readonly ILogger<PipeServer> _log;
    private readonly PipeSecurity _security;
    private readonly WorkoutSyncWorker _workouts;

    public PipeServer(TimerEngine engine, ILogger<PipeServer> log, IConfiguration configuration, WorkoutSyncWorker workouts)
    {
        _engine = engine;
        _log = log;
        _workouts = workouts;
        _security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        _security.SetOwner(system);
        _security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        _security.AddAccessRule(new PipeAccessRule(
            system,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        _security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
        if (configuration["Pipe:AllowedUserSid"] is { Length: > 0 } sid)
            _security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(sid),
                PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
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
                server = NamedPipeServerStreamAcl.Create(
                    Limits.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, pipeSecurity: _security,
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                // A silent client must not occupy one of the four instances forever.
                // Bound the line too: requests contain just a short command.
                var lineBuffer = new System.Text.StringBuilder();
                var one = new char[1];
                while (lineBuffer.Length <= 8192)
                {
                    int n = await reader.ReadAsync(one.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (n == 0 || one[0] == '\n') break;
                    lineBuffer.Append(one[0]);
                }
                if (lineBuffer.Length > 8192) return;
                string line = lineBuffer.ToString();
                if (string.IsNullOrWhiteSpace(line)) return;
                var req = JsonSerializer.Deserialize<PipeRequest>(line);
                PipeResponse resp = req?.Cmd switch
                {
                    "workout-status" => WorkoutReply(null),
                    "workout-save" => WorkoutReply(_workouts.Save(req.WorkoutBaseUrl, req.WorkoutApiToken)),
                    "workout-sync" => WorkoutReply(_workouts.RequestSync()),
                    _ => _engine.Handle(req)
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogDebug(ex, "pipe handle failed"); }
        }
    }

    private PipeResponse WorkoutReply(string? error) =>
        new() { Ok = error is null, Error = error ?? "", WorkoutSync = _workouts.Status() };
}
