using System.IO.Pipes;
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
            try
            {
                using var server = new NamedPipeServerStream(
                    Limits.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                _ = HandleOneAsync(server, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "pipe accept failed"); await Task.Delay(500, ct).ContinueWith(_ => { }); }
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
                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) return;
                var req = JsonSerializer.Deserialize<PipeRequest>(line);
                PipeResponse resp = _engine.Handle(req);
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
            }
            catch (Exception ex) { _log.LogDebug(ex, "pipe handle failed"); }
        }
    }
}
