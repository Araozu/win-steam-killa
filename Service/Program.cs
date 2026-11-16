using Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "SteamTimer");
// Workout-gated Dota (v7): credit API consumer. The replacement Notify/Tasker
// server currently captures samples only and returns 503 for credit queries.
// Configure with Workout:BaseUrl + Workout:ApiToken (appsettings.json) or
// WORKOUT_BASE_URL + WORKOUT_API_TOKEN env. Unconfigured => Null source,
// earned stays 0 (fail-closed: Dota blocked until a workout sync succeeds).
var workoutSettings = WorkoutConnectionSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton<IWorkoutCreditSource>(services => workoutSettings.IsConfigured
    ? new HttpWorkoutCreditSource(services.GetRequiredService<HttpClient>(), workoutSettings.BaseUrl, workoutSettings.ApiToken)
    : new NullWorkoutCreditSource());
builder.Services.AddSingleton<TimerEngine>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkoutSyncWorker>();

var host = builder.Build();
host.Run();
