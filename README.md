# Instant File Share

Windows 11-only MVP for creating temporary download links directly from a local machine. The repo is a polyglot monorepo with:

- `src/agent`: .NET 8 native agent, SQLite store, localhost/public HTTP server, tray runtime, `cloudflared` supervision
- `src/shell-extension`: C++/Win32 named-pipe shell helper built with CMake
- `src/ui`: Electron + Vue + TypeScript dashboard
- `tests`: .NET tests for shared backend utilities
- `docs`: architecture and protocol notes

## Prerequisites

- .NET 8 SDK
- Node.js 22+
- npm
- CMake 3.20+
- Windows 11 SDK / Visual C++ Build Tools
- `winget` for managed `cloudflared` installation

## Getting started

Bootstrap dependencies and the native build directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\bootstrap.ps1
```

One-shot full build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-all.ps1
```

1. Build the backend and run core tests:

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
   powershell -ExecutionPolicy Bypass -File .\scripts\dev-agent.ps1
   ```

5. Run the Electron dashboard during development in a second terminal:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\dev-ui.ps1
   ```

## Installer build

The repo now includes a Windows installer pipeline that stages:

- a published `win-x64` self-contained agent
- the Release shell helper
- a packaged Electron dashboard directory

To build the staged release artifacts and, if Inno Setup is installed, compile the installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

Notes:

- the script outputs staged files to `artifacts\package\stage`
- if `iscc` is available on `PATH`, the installer is written to `artifacts\package\installer`
- if Inno Setup is not installed, the script still prepares the staged files so the installer can be compiled later

For a fully clean local uninstall before reinstalling, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-clean.ps1
```

That script stops the installed processes, runs the installer uninstaller when present, removes startup/context-menu registration, and deletes the local app data under `%LOCALAPPDATA%\InstantFileShare`.

## Current MVP behaviors

- the local agent owns the share database, tray notifications, download server, startup integration, and `cloudflared` process supervision
- Explorer integration communicates through the `InstantFileShare.Agent` named pipe
- the shell helper forwards the selected file path to the agent; share creation stays in the agent
- public downloads are served from `/s/{token}` and `/s/{token}/{slug}`
- quick tunnel, managed Cloudflare, and manual publish modes are modeled in the agent
- runtime data is exposed through the local REST API and `/ws/runtime`

## Notes

- `cloudflared` quick tunnels are best-effort and session-scoped
- the app serves live file references only
- `Start on login` is applied through the current-user Windows Run key
- file context-menu integration depends on the built shell helper and appears in the classic Windows 11 menu under `Show more options`

## To-Do

- receiving files
- remote upload/server-hosted mode for 24/7 hosting and multi-user support
- protected links
- E2E encryption
- folder sharing
- ZIP generation and compression
- QR code support
- HTTPS certificate management
