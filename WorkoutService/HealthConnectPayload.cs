using System.Globalization;
using System.Text.Json;
using Shared;

static class HealthConnectPayload
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static WorkoutDataBatch? Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(body, Wire);
            if (payload is null || payload.Test || !HasOffset(payload.Timestamp, out var uploadedAt)) return null;

            var sessions = Valid(payload.Exercise, row =>
            {
                if (!ReadInfo(row.Metadata, out var info) || !ReadRange(row, out var start, out var end)
                    || row.DurationSeconds is not > 0 || row.DurationSeconds > (end - start).TotalSeconds + 1)
                    return null;
                return new WorkoutSession(info, start, end, row.DurationSeconds.Value, row.Type, row.Title);
            });

            var heartRate = Valid(payload.HeartRate, row =>
            {
                if (!ReadInfo(row.Metadata, out var info) || !HasOffset(row.Time, out var time)
                    || (row.Avg ?? row.Bpm) is not (> 0 and <= 300))
                    return null;
                return new WorkoutHeartRate(info, time, (row.Avg ?? row.Bpm)!.Value, row.Min, row.Max, row.Avg.HasValue);
            });

            var totalCalories = Valid(payload.TotalCalories, row =>
                ReadInfo(row.Metadata, out var info) && ReadRange(row, out var start, out var end)
                    && row.Calories is >= 0 and < double.PositiveInfinity
                    ? new WorkoutCalories(info, start, end, row.Calories.Value) : null);

            var deleted = Valid(payload.Deleted, row =>
                (row.Type is "exercise" or "heart_rate" or "total_calories") && !string.IsNullOrWhiteSpace(row.Id)
                    ? new WorkoutDeletion(row.Type, row.Id) : null);

            var batch = new WorkoutDataBatch(uploadedAt, sessions, heartRate, totalCalories, deleted);
            return batch.HasData() ? batch : null;
        }
        catch (JsonException)
        {
            // A diagnostic capture may contain a body other than the webhook JSON.
            return null;
        }
    }

    private static TResult[] Valid<TResult>(JsonElement[]? rows, Func<Row, TResult?> convert) where TResult : class
    {
        var result = new List<TResult>();
        foreach (var element in rows ?? [])
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            try
            {
                if (element.Deserialize<Row>(Wire) is { } row && convert(row) is { } converted)
                    result.Add(converted);
            }
            catch (JsonException)
            {
                // A malformed row does not discard other records in the same upload.
            }
        }
        return result.ToArray();
    }

    private static bool HasOffset(string? input, out DateTimeOffset time)
    {
        time = default;
        return input is { Length: >= 20 } && input[10] == 'T'
            && (input.EndsWith('Z') || input[^6] is '+' or '-')
            && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static bool ReadRange(Row row, out DateTimeOffset start, out DateTimeOffset end)
    {
        end = default;
        return HasOffset(row.StartTime, out start) && HasOffset(row.EndTime, out end) && start < end;
    }

    private static bool ReadInfo(Metadata? metadata, out WorkoutRecordInfo info)
    {
        info = null!;
        if (metadata?.DataOrigin != WorkoutDataBatch.SimpleHealthSource || string.IsNullOrWhiteSpace(metadata.Id)
            || !HasOffset(metadata.LastModifiedTime, out var modifiedAt)) return false;
        info = new WorkoutRecordInfo(metadata.Id, metadata.DataOrigin, modifiedAt, metadata.RecordingMethod);
        return true;
    }

    private sealed record Payload(string? Timestamp, bool Test, JsonElement[]? Exercise,
        JsonElement[]? HeartRate, JsonElement[]? TotalCalories, JsonElement[]? Deleted);

    private sealed record Row(string? StartTime, string? EndTime, string? Time, int? DurationSeconds,
        string? Type, string? Title, double? Bpm, double? Avg, double? Min, double? Max,
        double? Calories, string? Id, Metadata? Metadata);

    private sealed record Metadata(string? Id, string? DataOrigin, string? LastModifiedTime, string? RecordingMethod);
}
