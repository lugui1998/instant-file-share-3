# Test workflow

Run commands from the repository root unless a command changes into `src/ui`.

## Backend unit and integration tests

```powershell
dotnet test InstantFileShare.slnx
```

When a local `InstantFileShare.Agent` process is already running and locks normal build outputs, run focused .NET tests with an isolated output directory:

```powershell
dotnet test tests\InstantFileShare.Agent.Tests\InstantFileShare.Agent.Tests.csproj --no-restore -p:OutDir=.codex-build\verify-test-out\
```

## UI unit tests

```powershell
cd src\ui
npm test
```

## Public-share build

```powershell
cd src\ui
npm run build:public-share
```

## Browser E2E tests

```powershell
cd src\ui
npx playwright install chromium
npm run test:e2e
```

`npm run test:e2e` builds the public-share bundle, launches `tests\InstantFileShare.E2EHost` with temp DB, log, received-file, and source-file paths under `.tmp\e2e`, then runs Playwright smoke tests against randomized loopback ports. The launcher disables tray, startup tasks, and named-pipe hosting, and Playwright stops the host after the tests finish.
