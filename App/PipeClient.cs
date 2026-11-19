using System.IO.Pipes;
using System.Text.Json;
using Shared;

namespace App;

// Response is null when the call failed; Problem then says why.
public readonly record struct PipeReply(PipeResponse? Response, string Problem);

public static class PipeClient
{
    public const string Unreachable = "Service unreachable — is SteamTimer running?";
    public const string NoReply = "The service closed the connection without replying.";
    public const string Unreadable = "The service's reply could not be read — reinstall so the app and service match.";

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<PipeReply> SendAsync(PipeRequest req, int timeoutMs = 3000)
    {
        string? line;
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var client = new NamedPipeClientStream(".", Limits.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req).AsMemory(), timeout.Token).ConfigureAwait(false);
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connect, write, read or timeout failures: the transport is down.
            return new(null, Unreachable);
        }
        if (string.IsNullOrWhiteSpace(line)) return new(null, NoReply);
        try
        {
            var response = JsonSerializer.Deserialize<PipeResponse>(line, Opts);
            return response is null ? new(null, Unreadable) : new(response, "");
        }
        catch (JsonException) { return new(null, Unreadable); }
    }
}
