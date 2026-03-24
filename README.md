# Instant File Share

Windows 11-only MVP for creating temporary download links directly from a local machine. The repo is a polyglot monorepo with:

- `src/agent`: C# native agent, SQLite store, localhost/public HTTP server, cloudflared supervision
- `src/shell-extension`: C++/Win32 named-pipe shell helper scaffold built with CMake
- `src/ui`: Electron + Vue + TypeScript dashboard
- `tests`: .NET tests for shared backend utilities

## Prerequisites

- .NET 8 SDK
- Node.js 22+
- npm
- CMake 3.20+
- Windows 11 SDK / Visual C++ Build Tools
- `winget` for managed `cloudflared` installation

## Getting started

One-shot full build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-all.ps1
```

1. Build the backend and tests:

   ```powershell
   dotnet build InstantFileShare.slnx
   dotnet test tests/InstantFileShare.Core.Tests/InstantFileShare.Core.Tests.csproj
   ```

2. Install and build the UI:

   ```powershell
   cd src/ui
   npm install
   npm run build
   ```

3. Configure and build the shell helper:

   ```powershell
   cmake -S src/shell-extension -B build/shell-extension
   cmake --build build/shell-extension --config Debug
   ```

4. Run the agent during development:

   ```powershell
   dotnet run --project src/agent/InstantFileShare.Agent/InstantFileShare.Agent.csproj
   ```

5. Run the Electron dashboard during development in a second terminal:

   ```powershell
   cd src/ui
   npm run electron:dev
   ```

## Current MVP behaviors

- local agent owns the share database, tray notifications, download server, and `cloudflared` process supervision
- Explorer integration communicates through the `InstantFileShare.Agent` named pipe
- public downloads are served from `/s/{token}` and `/s/{token}/{slug}`
- quick tunnel, managed Cloudflare, and manual publish modes are modeled in the agent
- shell helper remains a thin command forwarder

## Notes

- `cloudflared` quick tunnels are best-effort and session-scoped
- the app serves live file references only

## To-Do

- receiving files
- remote upload/server-hosted mode for 24/7 hosting and multi-user support
- protected links
- E2E encryption
- folder sharing
- ZIP generation and compression
- Windows file ID resiliency
- QR code support
- HTTPS certificate management
