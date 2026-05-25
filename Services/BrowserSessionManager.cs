using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WatchTogether.Hubs;

namespace WatchTogether.Services;

public sealed class BrowserSessionManager(
    IHubContext<SessionHub> hub,
    IOptions<BrowserStreamOptions> options,
    ILogger<BrowserSessionManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly Lazy<Task<IPlaywright>> _playwright = new(() => Playwright.CreateAsync());

    public BrowserSession? Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public async Task<BrowserSession> CreateAsync(string rawUrl)
    {
        var normalizedUrl = NormalizeUrl(rawUrl);
        var id = GenerateId();
        var session = new BrowserSession(
            id,
            normalizedUrl,
            await _playwright.Value,
            hub,
            options.Value,
            logger);

        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("Could not create a unique session.");
        }

        try
        {
            await session.StartAsync();
            return session;
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync();
        }
    }

    public void DetachConnection(string connectionId)
    {
        foreach (var session in _sessions.Values)
        {
            session.DetachConnection(connectionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }

    private static string NormalizeUrl(string rawUrl)
    {
        var value = rawUrl.Trim();
        if (value.Length == 0)
        {
            throw new HubException("URL is required.");
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HubException("Only http and https URLs are supported.");
        }

        return uri.ToString();
    }

    private static string GenerateId()
        => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()[..10];

    private static string GenerateToken()
        => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
}

public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IHubContext<SessionHub> _hub;
    private readonly BrowserStreamOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly HashSet<string> _controllers = [];
    private readonly string _hostToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private Task? _captureLoop;

    public BrowserSession(
        string id,
        string startUrl,
        IPlaywright playwright,
        IHubContext<SessionHub> hub,
        BrowserStreamOptions options,
        ILogger logger)
    {
        Id = id;
        StartUrl = startUrl;
        _playwright = playwright;
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    public string Id { get; }

    public string StartUrl { get; private set; }

    public string HostToken => _hostToken;

    public string GroupName => $"session:{Id}";

    public async Task StartAsync()
    {
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-features=Translate,AutomationControlled"
            ]
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = _options.ViewportWidth,
                Height = _options.ViewportHeight
            },
            IgnoreHTTPSErrors = true,
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125 Safari/537.36"
        });

        _page = await _context.NewPageAsync();
        _page.SetDefaultNavigationTimeout(_options.NavigationTimeoutMs);
        await NavigateAsync(StartUrl);
        _captureLoop = CaptureLoopAsync(_stop.Token);
    }

    public bool AttachConnection(string connectionId, string? hostToken)
    {
        var controller = string.Equals(hostToken, _hostToken, StringComparison.Ordinal);
        if (controller)
        {
            lock (_controllers)
            {
                _controllers.Add(connectionId);
            }
        }

        return controller;
    }

    public void DetachConnection(string connectionId)
    {
        lock (_controllers)
        {
            _controllers.Remove(connectionId);
        }
    }

    public bool CanControl(string connectionId)
    {
        lock (_controllers)
        {
            return _controllers.Contains(connectionId);
        }
    }

    public object GetState(bool controller)
        => new
        {
            id = Id,
            url = StartUrl,
            controller,
            viewport = new
            {
                width = _options.ViewportWidth,
                height = _options.ViewportHeight
            },
            fps = _options.CaptureFps
        };

    public async Task NavigateAsync(string rawUrl)
    {
        if (_page is null)
        {
            return;
        }

        StartUrl = NormalizeUrl(rawUrl);
        await SendStatusAsync("Loading " + StartUrl);
        try
        {
            await _page.GotoAsync(StartUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });
        }
        catch (PlaywrightException ex)
        {
            await SendStatusAsync("Navigation failed: " + ex.Message);
            throw new HubException("Could not open this page.");
        }

        await SendPageInfoAsync();
    }

    public async Task ApplyInputAsync(BrowserInput input)
    {
        if (_page is null)
        {
            return;
        }

        await _inputLock.WaitAsync(_stop.Token);
        try
        {
            var x = Math.Clamp(input.X, 0, 1) * _options.ViewportWidth;
            var y = Math.Clamp(input.Y, 0, 1) * _options.ViewportHeight;

            switch (input.Type)
            {
                case "click":
                    await _page.Mouse.ClickAsync((float)x, (float)y);
                    break;
                case "doubleClick":
                    await _page.Mouse.DblClickAsync((float)x, (float)y);
                    break;
                case "wheel":
                    await _page.Mouse.MoveAsync((float)x, (float)y);
                    await _page.Mouse.WheelAsync((float)input.DeltaX, (float)input.DeltaY);
                    break;
                case "key":
                    if (!string.IsNullOrWhiteSpace(input.Key))
                    {
                        await _page.Keyboard.PressAsync(MapKey(input.Key));
                    }
                    break;
                case "text":
                    if (!string.IsNullOrEmpty(input.Text))
                    {
                        await _page.Keyboard.TypeAsync(input.Text);
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Session is closing.
        }
        finally
        {
            _inputLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync();
        }

        if (_captureLoop is not null)
        {
            try
            {
                await _captureLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _inputLock.Dispose();
        _stop.Dispose();
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_options.CaptureFps, 1, 30));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_page is not null)
                {
                    var bytes = await _page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Jpeg,
                        Quality = Math.Clamp(_options.JpegQuality, 35, 90),
                        FullPage = false
                    });

                    await _hub.Clients.Group(GroupName).SendAsync(
                        "frame",
                        Convert.ToBase64String(bytes),
                        cancellationToken);
                }

                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture failed for session {SessionId}", Id);
                await SendStatusAsync("Capture error: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private async Task SendPageInfoAsync()
    {
        if (_page is null)
        {
            return;
        }

        string? title = null;
        try
        {
            title = await _page.TitleAsync();
        }
        catch
        {
            // Title is best-effort metadata.
        }

        await _hub.Clients.Group(GroupName).SendAsync("pageInfo", new
        {
            url = _page.Url,
            title
        });
    }

    private Task SendStatusAsync(string message)
        => _hub.Clients.Group(GroupName).SendAsync("status", message);

    private static string NormalizeUrl(string rawUrl)
    {
        var value = rawUrl.Trim();
        if (value.Length == 0)
        {
            throw new HubException("URL is required.");
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HubException("Only http and https URLs are supported.");
        }

        return uri.ToString();
    }

    private static string MapKey(string key)
        => key switch
        {
            " " => "Space",
            "ArrowUp" => "ArrowUp",
            "ArrowDown" => "ArrowDown",
            "ArrowLeft" => "ArrowLeft",
            "ArrowRight" => "ArrowRight",
            "Backspace" => "Backspace",
            "Delete" => "Delete",
            "Enter" => "Enter",
            "Escape" => "Escape",
            "Tab" => "Tab",
            "Home" => "Home",
            "End" => "End",
            "PageUp" => "PageUp",
            "PageDown" => "PageDown",
            _ => key.Length == 1 ? key : key
        };
}
