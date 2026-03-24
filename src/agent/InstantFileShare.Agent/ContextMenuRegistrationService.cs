using InstantFileShare.Core;
using System.Diagnostics;

namespace InstantFileShare.Agent;

internal sealed class ContextMenuRegistrationService : IContextMenuRegistrationService
{
    public async Task ApplyAsync(bool enabled, CancellationToken cancellationToken)
    {
        var helperPath = ResolveShellHelperPath();
        if (helperPath is null)
        {
            return;
        }

        var arguments = enabled ? "--register-context-menu" : "--unregister-context-menu";
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        await process.WaitForExitAsync(cancellationToken);
    }

    private static string? ResolveShellHelperPath()
    {
        var repositoryRoot = AgentPaths.GetRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(repositoryRoot, "build", "shell-extension", "Debug", "instant_file_share_shell.exe"),
            Path.Combine(repositoryRoot, "build", "shell-extension", "Release", "instant_file_share_shell.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
