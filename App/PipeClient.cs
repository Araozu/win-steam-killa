using System.IO.Pipes;
using System.Text.Json;
using Shared;

namespace App;

public static class PipeClient
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<PipeResponse?> SendAsync(PipeRequest req, int timeoutMs = 3000)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var client = new NamedPipeClientStream(".", Limits.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req).AsMemory(), timeout.Token).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return null;
            return JsonSerializer.Deserialize<PipeResponse>(line, Opts);
        }
        catch { return null; }
    }

    // Never block a caller's SynchronizationContext (e.g. the WinForms UI
    // thread): hop to the pool first. Blocking GetResult on an async method
    // that captures the UI context is what froze the app on Start.
    public static PipeResponse? Send(PipeRequest req, int timeoutMs = 3000) =>
        Task.Run(() => SendAsync(req, timeoutMs)).GetAwaiter().GetResult();
}
