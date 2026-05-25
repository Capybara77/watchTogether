using WatchTogether.Hubs;
using WatchTogether.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 128 * 1024;
});

builder.Services.Configure<BrowserStreamOptions>(
    builder.Configuration.GetSection(BrowserStreamOptions.SectionName));
builder.Services.AddSingleton<BrowserSessionManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<SessionHub>("/hub/session");
app.MapFallbackToFile("index.html");

app.Run();
