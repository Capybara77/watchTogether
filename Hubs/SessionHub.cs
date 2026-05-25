using Microsoft.AspNetCore.SignalR;
using WatchTogether.Services;

namespace WatchTogether.Hubs;

public sealed class SessionHub(BrowserSessionManager sessions) : Hub
{
    public async Task<CreateSessionResult> CreateSession(string url)
    {
        var session = await sessions.CreateAsync(url);
        return new CreateSessionResult(session.Id, session.StartUrl, session.HostToken);
    }

    public async Task<JoinSessionResult> JoinSession(string sessionId, string? hostToken)
    {
        var session = sessions.Get(sessionId)
            ?? throw new HubException("Session not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, session.GroupName);
        var controller = session.AttachConnection(Context.ConnectionId, hostToken);

        await Clients.Caller.SendAsync("sessionState", session.GetState(controller));
        return new JoinSessionResult(session.Id, session.StartUrl, controller);
    }

    public async Task SendInput(string sessionId, BrowserInput input)
    {
        var session = sessions.Get(sessionId)
            ?? throw new HubException("Session not found.");

        if (!session.CanControl(Context.ConnectionId))
        {
            throw new HubException("This connection is not allowed to control the browser.");
        }

        await session.ApplyInputAsync(input);
    }

    public async Task Navigate(string sessionId, string url)
    {
        var session = sessions.Get(sessionId)
            ?? throw new HubException("Session not found.");

        if (!session.CanControl(Context.ConnectionId))
        {
            throw new HubException("This connection is not allowed to navigate.");
        }

        await session.NavigateAsync(url);
    }

    public async Task Back(string sessionId)
    {
        var session = sessions.Get(sessionId)
            ?? throw new HubException("Session not found.");

        if (!session.CanControl(Context.ConnectionId))
        {
            throw new HubException("This connection is not allowed to navigate.");
        }

        await session.BackAsync();
    }

    public async Task StopSession(string sessionId)
    {
        var session = sessions.Get(sessionId);
        if (session is null || !session.CanControl(Context.ConnectionId))
        {
            return;
        }

        await Clients.Group(session.GroupName).SendAsync("sessionStopped");
        await sessions.StopAsync(sessionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.DetachConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed record CreateSessionResult(string SessionId, string Url, string HostToken);

public sealed record JoinSessionResult(string SessionId, string Url, bool Controller);
