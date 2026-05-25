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
app.MapGet("/stream/{sessionId}.mp4", async (
    string sessionId,
    BrowserSessionManager sessions,
    HttpContext context) =>
{
    var session = sessions.Get(sessionId);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await session.StreamVideoAsync(context);
});
app.MapFallbackToFile("index.html");

app.Run();
