namespace Shared;

public sealed record WorkoutRecordInfo(string Id, string Source, DateTimeOffset ModifiedAt, string? RecordingMethod);
public sealed record WorkoutSession(WorkoutRecordInfo Info, DateTimeOffset Start, DateTimeOffset End,
    int DurationSeconds, string? Type, string? Title);
public sealed record WorkoutHeartRate(WorkoutRecordInfo Info, DateTimeOffset Time,
    double Bpm, double? Minimum, double? Maximum, bool IsAggregate);
public sealed record WorkoutCalories(WorkoutRecordInfo Info, DateTimeOffset Start, DateTimeOffset End, double Kilocalories);
public sealed record WorkoutDeletion(string Type, string Id);

public sealed record WorkoutDataBatch(DateTimeOffset UploadedAt, WorkoutSession[] Sessions, WorkoutHeartRate[] HeartRate,
    WorkoutCalories[] TotalCalories, WorkoutDeletion[] Deleted)
{
    public const string SimpleHealthSource = "com.mc.simplehealth";
    public const int PageSize = 8;
    public bool HasData() => Sessions.Length + HeartRate.Length + TotalCalories.Length + Deleted.Length > 0;
}

public sealed record WorkoutDelivery(string Id, WorkoutDataBatch Data);
public sealed record WorkoutAcknowledgement(string[] Ids);
