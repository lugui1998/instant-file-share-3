using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent.Tests;

public sealed class CloudflaredSupervisorTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "ifs-cloudflared-supervisor-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveExecutableCandidate_UsesUpdatedUserPathWhenProcessPathIsStale()
    {
        Directory.CreateDirectory(_rootPath);
        var userPathDirectory = Path.Combine(_rootPath, "user-path");
        Directory.CreateDirectory(userPathDirectory);
        var executablePath = CreatePlaceholderExecutable(userPathDirectory);

        var resolvedPath = CloudflaredSupervisor.ResolveExecutableCandidate(
            configuredPath: null,
            processPath: Path.Combine(_rootPath, "stale-process-path"),
            userPath: userPathDirectory,
            machinePath: null,
            localAppDataPath: Path.Combine(_rootPath, "local-app-data"),
            programFilesPath: Path.Combine(_rootPath, "program-files"),
            programFilesX86Path: Path.Combine(_rootPath, "program-files-x86"));

        Assert.Equal(executablePath, resolvedPath);
    }

    [Fact]
    public void ResolveExecutableCandidate_FallsBackToWingetPackageContentsWhenLinksAreMissing()
    {
        var localAppDataPath = Path.Combine(_rootPath, "local-app-data");
        var packageDirectory = Path.Combine(
            localAppDataPath,
            "Microsoft",
            "WinGet",
            "Packages",
            "Cloudflare.cloudflared_2026.4.0_x64__8wekyb3d8bbwe",
            "tools");
        Directory.CreateDirectory(packageDirectory);
        var executablePath = CreatePlaceholderExecutable(packageDirectory);

        var resolvedPath = CloudflaredSupervisor.ResolveExecutableCandidate(
            configuredPath: null,
            processPath: null,
            userPath: null,
            machinePath: null,
            localAppDataPath: localAppDataPath,
            programFilesPath: Path.Combine(_rootPath, "program-files"),
            programFilesX86Path: Path.Combine(_rootPath, "program-files-x86"));

        Assert.Equal(executablePath, resolvedPath);
    }

    [Fact]
    public void ResolveExecutableCandidate_PrefersExplicitConfiguredPath()
    {
        Directory.CreateDirectory(_rootPath);
        var configuredDirectory = Path.Combine(_rootPath, "configured");
        Directory.CreateDirectory(configuredDirectory);
        var configuredPath = CreatePlaceholderExecutable(configuredDirectory);

        var resolvedPath = CloudflaredSupervisor.ResolveExecutableCandidate(
            configuredPath,
            processPath: null,
            userPath: null,
            machinePath: null,
            localAppDataPath: Path.Combine(_rootPath, "local-app-data"),
            programFilesPath: Path.Combine(_rootPath, "program-files"),
            programFilesX86Path: Path.Combine(_rootPath, "program-files-x86"));

        Assert.Equal(configuredPath, resolvedPath);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
        }
    }

    private static string CreatePlaceholderExecutable(string directoryPath)
    {
        var executablePath = Path.Combine(directoryPath, "cloudflared.exe");
        File.WriteAllBytes(executablePath, []);
        return executablePath;
    }
}
