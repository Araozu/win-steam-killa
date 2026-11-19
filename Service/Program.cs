using Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "SteamTimer");
// Workout settings apply to the archive sync. Credit is calculated on this PC.
var workoutSettings = WorkoutConnectionSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton<IWorkoutCreditSource>(services => workoutSettings.IsConfigured
    ? new LocalWorkoutCreditSource(new WorkoutDataSync(services.GetRequiredService<HttpClient>(),
        workoutSettings.BaseUrl, workoutSettings.ApiToken,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamTimer", "workouts.json"),
        services.GetRequiredService<ILogger<WorkoutDataSync>>()))
    : new NullWorkoutCreditSource());
builder.Services.AddSingleton<TimerEngine>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkoutSyncWorker>();

var host = builder.Build();
host.Run();
