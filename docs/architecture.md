# Architecture Overview

## Runtime split

- `InstantFileShare.Agent`: native owner for runtime state, HTTP endpoints, tray UX, and tunnel supervision
- `InstantFileShare.Infrastructure`: reusable runtime services such as the cloudflared supervisor and runtime event stream
- `instant_file_share_shell.exe`: Win32 shell-side helper that forwards a selected file path over the named pipe
- Electron/Vue UI: dashboard that talks to the local agent through REST and WebSocket APIs

## Main local interfaces

- Named pipe: `InstantFileShare.Agent`
- Local REST API: `http://127.0.0.1:46430/api/*`
- Runtime WebSocket: `ws://127.0.0.1:46430/ws/runtime`
- Public routes: `GET|HEAD /s/{token}` and `GET|HEAD /s/{token}/{slug}`

## Persistence

SQLite is stored under `%LocalAppData%\InstantFileShare\instant-file-share.db`. The current store tracks:

- shares
- settings
- publish profiles
- cloudflared state

## Publish modes

- `QuickTunnel`: launches `cloudflared tunnel --url http://127.0.0.1:{publicPort}`
- `ManagedCloudflare`: supports external login flow plus stored profile data for future relaunch
- `Manual`: uses explicit base URL when present, otherwise public IP, local IP, then loopback fallback
