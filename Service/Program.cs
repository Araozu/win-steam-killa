using Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SteamTimer");
// Workout-gated Dota (v7): verified Strava credit via WorkoutService.
// Configure with Workout:BaseUrl + Workout:ApiToken (appsettings.json) or
// WORKOUT_BASE_URL + WORKOUT_API_TOKEN env. Unconfigured => Null source,
// earned stays 0 (fail-closed: Dota blocked until a workout sync succeeds).
IWorkoutCreditSource workoutSource;
var workoutUrl = builder.Configuration["Workout:BaseUrl"]
    ?? Environment.GetEnvironmentVariable("WORKOUT_BASE_URL") ?? "";
var workoutToken = builder.Configuration["Workout:ApiToken"]
    ?? Environment.GetEnvironmentVariable("WORKOUT_API_TOKEN") ?? "";
if (!string.IsNullOrWhiteSpace(workoutUrl) && !string.IsNullOrWhiteSpace(workoutToken))
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    workoutSource = new HttpWorkoutCreditSource(http, workoutUrl, workoutToken);
}
else workoutSource = new NullWorkoutCreditSource();
builder.Services.AddSingleton(workoutSource);
builder.Services.AddSingleton<TimerEngine>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
