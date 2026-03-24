# AGENTS.md

This repository is a Windows 11-only monorepo for the Instant File Share MVP.

## Repo layout

- `src/agent`: .NET 8 Windows agent, SQLite persistence, local/public HTTP endpoints, tray runtime, named-pipe server, `cloudflared` supervision
- `src/shell-extension`: C++/Win32 shell-side helper built with CMake
- `src/ui`: Electron + Vue + TypeScript desktop UI
- `tests`: .NET tests for shared/core behavior
- `docs`: architecture and protocol notes
- `scripts`: local bootstrap and dev helpers

## Current architecture

- The C# agent is the process owner.
- The agent exposes:
  - local control API on `127.0.0.1:46430`
  - public/manual download listener on port `46431`
  - WebSocket runtime events at `/ws/runtime`
- Explorer-side integration talks to the agent through the named pipe `InstantFileShare.Agent`.
- Public download routes are:
  - `/s/{token}`
  - `/s/{token}/{slug}`
- Share tokens are base62 with a 128-bit entropy floor.
- Shares are live file references, not snapshots.

## Development rules

- Prefer changing the smallest relevant surface.
- Do not revert unrelated user changes.
- Keep the shell-side native code thin; backend logic belongs in the agent.
- Keep the Electron app UI-only; long-running responsibilities belong in the agent.
- When editing files, preserve the current CRLF/Windows-oriented setup.
- Use the chrome-debug tool to inspect and interact with the UI

## Terminal rules
- When using temrinal commands, ensure unused terminals are terminated. We don't want process running forever in the background.

## Build and test

- Restore/build backend:
  - `dotnet build InstantFileShare.slnx`
- Run core tests:
  - `dotnet test tests/InstantFileShare.Core.Tests/InstantFileShare.Core.Tests.csproj`
- Run agent locally:
  - `dotnet run --project src/agent/InstantFileShare.Agent/InstantFileShare.Agent.csproj`
- Install UI deps:
  - `cd src/ui`
  - `npm install`
- Run UI in development:
  - `npm run electron:dev`
- Build UI:
  - `npm run build`
- Configure/build shell helper:
  - `cmake -S src/shell-extension -B build/shell-extension`
  - `cmake --build build/shell-extension --config Debug`

## Important implementation notes

- `cloudflared` detection order is:
  - explicit path override
  - `PATH`
- Automatic `cloudflared` updates are only allowed for winget-managed installs.
- `StartOnLogin` is applied through the current-user Windows Run key.
- Managed Cloudflare mode currently supports external login plus profile-driven relaunch, not full in-app provisioning.
- The shell project is currently a thin Win32 forwarder, not a packaged Explorer COM extension yet.

## Deferred items

- receiving files
- remote upload/server-hosted mode
- protected links
- E2E encryption
- folder sharing
- ZIP generation and compression
- Windows file ID resiliency
- HTTPS certificate management
