using System.Text.Json;
using Shared;

namespace Service;

internal sealed class WorkoutArchive
{
    public Dictionary<string, WorkoutSession> Sessions { get; init; } = new();
    public Dictionary<string, StoredHeartRate> HeartRate { get; init; } = new();
    public Dictionary<string, WorkoutCalories> TotalCalories { get; init; } = new();
    public Dictionary<string, DateTimeOffset> Deletions { get; init; } = new();
    public Dictionary<string, WorkoutSensorLinks> SensorLinks { get; set; } = new();

    public void Apply(WorkoutDataBatch batch)
    {
        foreach (var row in batch.Sessions)
            if (CanApply("exercise", row.Info)
                && (!Sessions.TryGetValue(row.Info.Id, out var previous) || row.Info.ModifiedAt > previous.Info.ModifiedAt))
                Sessions[row.Info.Id] = row;

        foreach (var row in batch.TotalCalories)
            if (CanApply("total_calories", row.Info)
                && (!TotalCalories.TryGetValue(row.Info.Id, out var previous) || row.Info.ModifiedAt > previous.Info.ModifiedAt))
                TotalCalories[row.Info.Id] = row;

        foreach (var row in batch.HeartRate)
        {
            if (!CanApply("heart_rate", row.Info)) continue;
            string instant = row.Time.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            string key = row.IsAggregate ? $"bucket:{row.Info.Source}:{instant}" : $"sample:{row.Info.Id}:{instant}";
            if (!HeartRate.TryGetValue(key, out var previous) || batch.UploadedAt > previous.UploadedAt)
                HeartRate[key] = new StoredHeartRate(row, batch.UploadedAt);
        }

        var heartRateDeletes = new HashSet<string>();
        foreach (var row in batch.Deleted)
        {
            string key = $"{row.Type}:{row.Id}";
            if (Deletions.TryGetValue(key, out var prior) && prior >= batch.UploadedAt) continue;
            Deletions[key] = batch.UploadedAt;
            if (row.Type == "exercise" && Sessions.TryGetValue(row.Id, out var workout) && !CanApply(row.Type, workout.Info))
                Sessions.Remove(row.Id);
            if (row.Type == "total_calories" && TotalCalories.TryGetValue(row.Id, out var calories) && !CanApply(row.Type, calories.Info))
                TotalCalories.Remove(row.Id);
            if (row.Type == "heart_rate") heartRateDeletes.Add(row.Id);
        }
        if (heartRateDeletes.Count > 0)
            foreach (var (key, entry) in HeartRate.Where(x => heartRateDeletes.Contains(x.Value.Reading.Info.Id)
                && !CanApply("heart_rate", x.Value.Reading.Info)).ToArray())
                HeartRate.Remove(key);
    }

    private bool CanApply(string type, WorkoutRecordInfo info) =>
        !Deletions.TryGetValue($"{type}:{info.Id}", out var deletedAt) || info.ModifiedAt > deletedAt;

    public void LinkSensors()
    {
        var calories = TotalCalories.Values.ToLookup(row => (row.Info.Source, row.Start, row.End));
        var readings = HeartRate.OrderBy(x => x.Value.Reading.Time).ToArray();
        var links = new Dictionary<string, WorkoutSensorLinks>();
        foreach (var (id, session) in Sessions)
        {
            // The webhook's one-minute buckets can start before the workout.
            long earliest = session.Start.UtcTicks - TimeSpan.TicksPerMinute;
            int low = 0, high = readings.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (readings[middle].Value.Reading.Time.UtcTicks < earliest) low = middle + 1;
                else high = middle;
            }

            var matching = new List<string>();
            for (int i = low; i < readings.Length && readings[i].Value.Reading.Time < session.End; i++)
            {
                var (key, entry) = readings[i];
                var row = entry.Reading;
                if (row.Info.Source == session.Info.Source
                    && (row.IsAggregate ? row.Time.UtcTicks > earliest : row.Time >= session.Start))
                    matching.Add(key);
            }

            links[id] = new WorkoutSensorLinks(matching.ToArray(),
                calories[(session.Info.Source, session.Start, session.End)].Select(row => row.Info.Id).ToArray());
        }
        SensorLinks = links;
    }

    public static WorkoutArchive Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<WorkoutArchive>(file, WorkoutDataSync.Json)
                ?? throw new InvalidDataException("Workout archive is empty.");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, this, WorkoutDataSync.Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed record StoredHeartRate(WorkoutHeartRate Reading, DateTimeOffset UploadedAt);
internal sealed record WorkoutSensorLinks(string[] HeartRateKeys, string[] TotalCalorieIds);
