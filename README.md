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

Run the automated test suite:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

For focused unit, integration, public-share, and browser E2E commands, see [docs/test-workflow.md](docs/test-workflow.md).

1. Build the backend and run solution tests:

   ```powershell
   dotnet build InstantFileShare.slnx
   dotnet test InstantFileShare.slnx
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
- the installer exposes an optional `cloudflared` component and uses `winget` to install it when `winget` is available for the current user

## CI and releases

GitHub Actions runs the Windows CI workflow on pull requests and pushes to `main`. The workflow builds and tests the .NET solution, runs the UI tests and build, and builds the shell helper.

Releases are automatic for version tags matching `v*.*.*`:

```powershell
git tag v1.0.2
git push origin v1.0.2
```

The release workflow runs tests, installs Inno Setup on the GitHub runner, runs `scripts\build-installer.ps1`, uploads the installer and staged package ZIP as workflow artifacts, and creates or updates the matching GitHub release.

Manual verification for the release workflow is still done by running the GitHub Actions release workflow for a real tag, because GitHub release creation and artifact upload depend on GitHub-hosted runner state and `GITHUB_TOKEN` permissions.

For a fully clean local uninstall before reinstalling, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-clean.ps1
```

That script stops the agent process tree so agent-owned child processes such as app-started `cloudflared` are also terminated, runs the installer uninstaller when present, removes startup/context-menu registration for every file/folder verb, and deletes the local app data under `%LOCALAPPDATA%\InstantFileShare`.
It also removes Electron user data/cache leftovers under the app's roaming/local profile folders. To also uninstall the optional winget-managed `cloudflared` package during cleanup, pass `-UninstallCloudflared`; this is off by default because `cloudflared` may be used by other applications.

## Current MVP behaviors

- the local agent owns the share database, tray notifications, download server, startup integration, and `cloudflared` process supervision
- Explorer integration communicates through the `InstantFileShare.Agent` named pipe
- the shell helper forwards the selected file path to the agent; share creation stays in the agent
- public downloads are served from `/s/{token}` and `/s/{token}/{slug}`
- receive links are served from `/r/{token}` and can upload files directly into a selected local folder
- folder shares support both browse-style links and ZIP downloads, depending on the chosen share mode
- quick tunnel, managed Cloudflare, and manual publish modes are modeled in the agent
- runtime data is exposed through the local REST API and `/ws/runtime`

## Notes

- `cloudflared` quick tunnels are best-effort and session-scoped
- the app serves live file references only
- `Start on login` is applied through the current-user Windows Run key
- file context-menu integration depends on the built shell helper and appears in the classic Windows 11 menu under `Show more options`
- managed Cloudflare tunnel tokens are still stored locally to support automatic relaunch; that local exposure remains an accepted temporary tradeoff

## To-Do

- remote upload/server-hosted mode for 24/7 hosting and multi-user support
- protected links
- E2E encryption
- QR code support
- HTTPS certificate management

## Disclosure

AI tools where used in the process of creating this project.

## License

FixPix is licensed under the GNU General Public License v3.0 only. See
`LICENSE` for the full license text.
