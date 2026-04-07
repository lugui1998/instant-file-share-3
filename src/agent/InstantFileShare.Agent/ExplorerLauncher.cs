using System.ComponentModel;
using System.Diagnostics;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class ExplorerLauncher(ILogger<ExplorerLauncher> logger) : IExplorerLauncher
{
    public Task OpenDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("The directory path could not be resolved.", nameof(directoryPath));
        }

        var fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The share location is no longer available.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                WorkingDirectory = fullPath,
                ArgumentList = { fullPath },
            });

            if (process is null)
            {
                throw new InvalidOperationException("Windows Explorer could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            logger.LogError(exception, "Failed to open Windows Explorer for {DirectoryPath}.", fullPath);
            throw new InvalidOperationException("Windows Explorer could not be started.", exception);
        }

        return Task.CompletedTask;
    }
}
