using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shared;

namespace Service;

internal sealed class WorkoutDataSync(HttpClient http, string baseUrl, string apiToken, string archivePath,
    ILogger? log = null, TimeProvider? clock = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<WorkoutArchive> SyncAsync(CancellationToken ct)
    {
        WorkoutArchive? archive = null;
        while (true)
        {
            using var fetch = Request(HttpMethod.Get, "/api/workouts");
            using var response = await http.SendAsync(fetch, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var deliveries = await response.Content.ReadFromJsonAsync<WorkoutDelivery[]>(Json, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Workout feed response is empty.");
            if (deliveries.Length == 0)
            {
                if (archive is not null) return archive;
                // Nothing new: still expire old records on the regular sync cycle.
                archive = WorkoutArchive.Load(archivePath, log);
                if (archive.Prune(_clock.GetUtcNow()))
                {
                    archive.LinkSensors();
                    archive.Save(archivePath);
                }
                return archive;
            }
            if (deliveries.Length > WorkoutDataBatch.PageSize || deliveries.Any(item =>
                item?.Data is null || !Guid.TryParseExact(item.Id, "N", out _)))
                throw new InvalidDataException("Workout feed response contains an invalid delivery.");

            archive ??= WorkoutArchive.Load(archivePath, log);
            foreach (var delivery in deliveries)
                archive.Apply(delivery.Data);
            archive.Prune(_clock.GetUtcNow());
            archive.LinkSensors();
            archive.Save(archivePath);

            using var acknowledge = Request(HttpMethod.Post, "/api/workouts/ack");
            acknowledge.Content = JsonContent.Create(new WorkoutAcknowledgement(deliveries.Select(item => item.Id).ToArray()));
            using var receipt = await http.SendAsync(acknowledge, ct).ConfigureAwait(false);
            receipt.EnsureSuccessStatusCode();
            if (deliveries.Length < WorkoutDataBatch.PageSize) return archive;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        return request;
    }
}
