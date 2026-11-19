using Service;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "SteamTimer");
// Workout settings apply to the archive sync. Credit is calculated on this PC.
var workoutSettings = WorkoutConnectionSettings.FromConfiguration(builder.Configuration);
string workoutDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamTimer");
builder.Services.AddSingleton(services => new WorkoutConnectionStore(Path.Combine(workoutDirectory, "workout-connection.json"),
    workoutSettings, services.GetRequiredService<ILogger<WorkoutConnectionStore>>()));
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton<IWorkoutCreditSource>(services => new ConfiguredWorkoutCreditSource(
    services.GetRequiredService<WorkoutConnectionStore>(), services.GetRequiredService<HttpClient>(),
    Path.Combine(workoutDirectory, "workouts.json"), services.GetRequiredService<ILogger<WorkoutDataSync>>()));
builder.Services.AddSingleton<TimerEngine>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<WorkoutSyncWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<WorkoutSyncWorker>());

var host = builder.Build();
host.Run();
