using System.IO.Pipes;
using System.Text.Json;
using Shared;

namespace App;

// Response is null when the call failed; Problem then says why.
public readonly record struct PipeReply(PipeResponse? Response, string Problem);

public static class PipeClient
{
    public const string Unreachable = "Service unreachable — is SteamTimer running?";
    public const string ReplyTimedOut = "Connected to SteamTimer, but it did not reply in time.";
    public const int DefaultReplyTimeoutMs = 12000;
    public const string NoReply = "The service closed the connection without replying.";
    public const string Unreadable = "The service's reply could not be read — reinstall so the app and service match.";

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<PipeReply> SendAsync(PipeRequest req, int timeoutMs = DefaultReplyTimeoutMs)
    {
        bool connected = false;
        string? line;
        try
        {
            using var client = new NamedPipeClientStream(".", Limits.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000).ConfigureAwait(false);
            connected = true;
            // Enforcement can close Dota and Steam in separate four-second passes.
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req).AsMemory(), timeout.Token).ConfigureAwait(false);
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connected) { return new(null, ReplyTimedOut); }
        catch (UnauthorizedAccessException) { return new(null, "This Windows account does not have access to SteamTimer."); }
        catch (TimeoutException) { return new(null, Unreachable); }
        catch (IOException) { return new(null, connected ? NoReply : Unreachable); }
        if (string.IsNullOrWhiteSpace(line)) return new(null, NoReply);
        try
        {
            var response = JsonSerializer.Deserialize<PipeResponse>(line, Opts);
            return response is null ? new(null, Unreadable) : new(response, "");
        }
        catch (JsonException) { return new(null, Unreadable); }
    }
}
