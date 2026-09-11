using Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SteamTimer");
builder.Services.AddSingleton<TimerEngine>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
