using System.Collections.Concurrent;
using System.Diagnostics;
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
    private int _nextDisplay = 90;

    public BrowserSession? Get(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public async Task<BrowserSession> CreateAsync(string rawUrl)
    {
        var normalizedUrl = NormalizeUrl(rawUrl);
        var id = GenerateId();
        var session = new BrowserSession(
            id,
            normalizedUrl,
            Interlocked.Increment(ref _nextDisplay),
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
    private readonly ConcurrentDictionary<int, ConcurrentQueue<string>> _processErrors = new();
    private readonly string _hostToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
    private readonly string _displayName;
    private readonly string _runtimeDir;
    private readonly string _pulseSocket;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private Process? _xvfb;
    private Process? _pulse;

    public BrowserSession(
        string id,
        string startUrl,
        int displayNumber,
        IPlaywright playwright,
        IHubContext<SessionHub> hub,
        BrowserStreamOptions options,
        ILogger logger)
    {
        Id = id;
        StartUrl = startUrl;
        _displayName = $":{displayNumber}";
        _runtimeDir = Path.Combine(Path.GetTempPath(), "watchtogether", id);
        _pulseSocket = Path.Combine(_runtimeDir, "pulse", "native");
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
        Directory.CreateDirectory(_runtimeDir);
        var pulseDir = Path.GetDirectoryName(_pulseSocket)!;
        Directory.CreateDirectory(pulseDir);

        _xvfb = StartProcess(
            _options.XvfbPath,
            [
                _displayName,
                "-screen",
                "0",
                $"{_options.ViewportWidth}x{_options.ViewportHeight}x24",
                "-ac",
                "-nolisten",
                "tcp"
            ],
            "xvfb");

        _pulse = StartProcess(
            _options.PulseAudioPath,
            [
                "--daemonize=no",
                "--exit-idle-time=-1",
                "--disable-shm=true",
                "--log-target=stderr",
                "-L",
                $"module-native-protocol-unix auth-anonymous=1 socket={_pulseSocket}",
                "-L",
                "module-null-sink sink_name=watch sink_properties=device.description=WatchTogether",
                "-L",
                "module-always-sink"
            ],
            "pulseaudio",
            RuntimeEnvironment());

        await Task.Delay(700, _stop.Token);
        EnsureProcessAlive(_xvfb, "Xvfb");
        EnsureProcessAlive(_pulse, "PulseAudio");

        var browserEnv = RuntimeEnvironment();
        browserEnv["DISPLAY"] = _displayName;
        browserEnv["PULSE_SERVER"] = "unix:" + _pulseSocket;
        browserEnv["PULSE_SINK"] = "watch";

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Env = browserEnv,
            Args =
            [
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--autoplay-policy=no-user-gesture-required",
                "--disable-features=Translate,AutomationControlled",
                "--disable-gpu",
                "--use-gl=swiftshader",
                "--window-position=0,0",
                $"--window-size={_options.ViewportWidth},{_options.ViewportHeight}"
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
            streamUrl = $"/stream/{Id}.mp4",
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

    public async Task BackAsync()
    {
        if (_page is null)
        {
            return;
        }

        try
        {
            await _page.GoBackAsync(new PageGoBackOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });
            StartUrl = _page.Url;
            await SendPageInfoAsync();
        }
        catch (PlaywrightException ex)
        {
            await SendStatusAsync("Back failed: " + ex.Message);
        }
    }

    public async Task StreamVideoAsync(HttpContext context)
    {
        EnsureProcessAlive(_xvfb, "Xvfb");
        EnsureProcessAlive(_pulse, "PulseAudio");

        context.Response.ContentType = "video/mp4";
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var ffmpeg = StartProcess(
            _options.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel",
                "warning",
                "-thread_queue_size",
                "512",
                "-f",
                "x11grab",
                "-draw_mouse",
                "0",
                "-video_size",
                $"{_options.ViewportWidth}x{_options.ViewportHeight}",
                "-framerate",
                Math.Clamp(_options.CaptureFps, 1, 60).ToString(),
                "-i",
                $"{_displayName}.0",
                "-thread_queue_size",
                "512",
                "-f",
                "pulse",
                "-i",
                "watch.monitor",
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-tune",
                "zerolatency",
                "-pix_fmt",
                "yuv420p",
                "-profile:v",
                "baseline",
                "-b:v",
                $"{Math.Max(300, _options.VideoBitrateKbps)}k",
                "-maxrate",
                $"{Math.Max(300, _options.VideoBitrateKbps)}k",
                "-bufsize",
                $"{Math.Max(600, _options.VideoBitrateKbps * 2)}k",
                "-g",
                Math.Clamp(_options.CaptureFps * 2, 2, 120).ToString(),
                "-keyint_min",
                Math.Clamp(_options.CaptureFps * 2, 2, 120).ToString(),
                "-sc_threshold",
                "0",
                "-c:a",
                "aac",
                "-b:a",
                $"{Math.Max(32, _options.AudioBitrateKbps)}k",
                "-ar",
                "48000",
                "-ac",
                "2",
                "-movflags",
                "frag_keyframe+empty_moov+default_base_moof",
                "-f",
                "mp4",
                "pipe:1"
            ],
            "ffmpeg",
            StreamEnvironment(),
            redirectStandardOutput: true);

        try
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The viewer disconnected.
        }
        finally
        {
            KillProcess(ffmpeg, "ffmpeg");
        }
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

        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        KillProcess(_pulse, "pulseaudio");
        KillProcess(_xvfb, "xvfb");

        _inputLock.Dispose();
        _stop.Dispose();

        try
        {
            if (Directory.Exists(_runtimeDir))
            {
                Directory.Delete(_runtimeDir, true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete runtime directory for session {SessionId}", Id);
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

    private Dictionary<string, string> RuntimeEnvironment()
        => new(StringComparer.Ordinal)
        {
            ["XDG_RUNTIME_DIR"] = _runtimeDir,
            ["PULSE_RUNTIME_PATH"] = Path.Combine(_runtimeDir, "pulse")
        };

    private Dictionary<string, string> StreamEnvironment()
    {
        var env = RuntimeEnvironment();
        env["DISPLAY"] = _displayName;
        env["PULSE_SERVER"] = "unix:" + _pulseSocket;
        return env;
    }

    private Process StartProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string name,
        IReadOnlyDictionary<string, string>? environment = null,
        bool redirectStandardOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStandardOutput
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {name}.");
        var stderrLines = new ConcurrentQueue<string>();
        _processErrors[process.Id] = stderrLines;

        _ = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                _logger.LogDebug("{ProcessName} [{SessionId}]: {Line}", name, Id, line);
                stderrLines.Enqueue(line);
                while (stderrLines.Count > 20)
                {
                    stderrLines.TryDequeue(out _);
                }
            }
        });

        return process;
    }

    private void EnsureProcessAlive(Process? process, string name)
    {
        if (process is null)
        {
            throw new InvalidOperationException($"{name} is not running.");
        }

        if (process.HasExited)
        {
            var details = "";
            if (_processErrors.TryGetValue(process.Id, out var lines))
            {
                details = string.Join(" | ", lines);
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? $"{name} exited with code {process.ExitCode}."
                    : $"{name} exited with code {process.ExitCode}: {details}");
        }
    }

    private void KillProcess(Process? process, string name)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop {ProcessName} for session {SessionId}", name, Id);
        }
        finally
        {
            _processErrors.TryRemove(process.Id, out _);
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
