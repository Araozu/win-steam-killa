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
            using var client = new NamedPipeClientStream(".", Limits.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req));
            // Read with timeout
            var readTask = reader.ReadLineAsync();
            var done = await Task.WhenAny(readTask, Task.Delay(timeoutMs));
            if (done != readTask) return null;
            string? line = await readTask;
            if (string.IsNullOrWhiteSpace(line)) return null;
            return JsonSerializer.Deserialize<PipeResponse>(line, Opts);
        }
        catch { return null; }
    }

    public static PipeResponse? Send(PipeRequest req, int timeoutMs = 3000) =>
        SendAsync(req, timeoutMs).GetAwaiter().GetResult();
}
