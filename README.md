# WatchTogether

Minimal ASP.NET Core MVP for shared browsing through a server-side Chromium instance.

## What works in this MVP

- Create a room from any `http` or `https` URL.
- Share `/r/{roomId}` with a viewer.
- The host controls one server-side Chromium page.
- Clicks, double-clicks, wheel scroll, typing, common keys, URL navigation, and browser back are forwarded to Chromium.
- Viewers receive an MP4 video stream with audio captured from the same browser page.

## Current MVP limitation

This version captures Chromium through Xvfb, PulseAudio, and FFmpeg. It is good enough for a small MVP room, but it is not a low-latency WebRTC media server yet. Every viewer opens their own FFmpeg stream, so CPU and outbound traffic grow with viewers.

DRM-protected services can still fail inside server-side Chromium.

## Run with Docker Compose

```bash
docker compose up --build
```

Open:

```text
http://localhost:8090
```

On your Ubuntu server, expose port `8090` or put the app behind a reverse proxy. The first user creates a room and gets a URL like:

```text
http://your-server:8090/r/abc123
```

The host URL keeps `?host={token}`; the shared URL does not.

## Tune stream quality

Edit `docker-compose.yml`:

- `BrowserStream__CaptureFps`: default `24`
- `BrowserStream__VideoBitrateKbps`: default `2500`
- `BrowserStream__AudioBitrateKbps`: default `128`
- `BrowserStream__ViewportWidth` / `BrowserStream__ViewportHeight`: default `1366x768`

Higher values increase CPU and outgoing traffic.
