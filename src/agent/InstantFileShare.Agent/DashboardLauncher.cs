using System.Diagnostics;

namespace InstantFileShare.Agent;

public sealed class DashboardLauncher(ILogger<DashboardLauncher> logger)
{
    private Process? _process;

    public Task OpenAsync()
    {
        return Launch(ResolveRepositoryRoot());
    }

    public Task Launch(string repositoryRoot)
    {
        var uiPath = Path.Combine(repositoryRoot, "src", "ui");
        var packageJson = Path.Combine(uiPath, "package.json");
        if (!File.Exists(packageJson))
        {
            return Task.CompletedTask;
        }

        if (_process is { HasExited: false })
        {
            return Task.CompletedTask;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "npm",
            Arguments = "run electron:dev",
            WorkingDirectory = uiPath,
            UseShellExecute = true,
        };

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to start the Electron dashboard.");
        }

        return Task.CompletedTask;
    }

    private static string ResolveRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
