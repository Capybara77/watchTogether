# WatchTogether

Minimal ASP.NET Core MVP for shared browsing through a server-side Chromium instance.

## What works in this MVP

- Create a room from any `http` or `https` URL.
- Share `/r/{roomId}` with a viewer.
- The host controls one server-side Chromium page.
- Clicks, double-clicks, wheel scroll, typing, and common keys are forwarded to Chromium.
- Viewers receive a JPEG frame stream from the same browser page.

## Current MVP limitation

This version streams screen frames over SignalR. It does **not** stream audio. For watching sites with sound together, the next layer should replace the frame stream with WebRTC and capture Chromium audio through PulseAudio/PipeWire plus FFmpeg/GStreamer or a WebRTC media server.

DRM-protected services can still fail inside server-side Chromium.

## Run with Docker Compose

```bash
docker compose up --build
```

Open:

```text
http://localhost:8080
```

On your Ubuntu server, expose port `8080` or put the app behind a reverse proxy. The first user creates a room and gets a URL like:

```text
http://your-server:8080/r/abc123
```

The host URL keeps `?host=1`; the shared URL does not.

## Tune stream quality

Edit `docker-compose.yml`:

- `BrowserStream__CaptureFps`: default `8`
- `BrowserStream__JpegQuality`: default `65`
- `BrowserStream__ViewportWidth` / `BrowserStream__ViewportHeight`: default `1366x768`

Higher values increase CPU and outgoing traffic.
