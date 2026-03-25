using InstantFileShare.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace InstantFileShare.Agent;

internal sealed class ContextMenuRegistrationService : IContextMenuRegistrationService
{
    private const string VerbKeyPath = @"Software\Classes\*\shell\InstantFileShare";
    private const string CommandKeyPath = @"Software\Classes\*\shell\InstantFileShare\command";

    public async Task ApplyAsync(bool enabled, CancellationToken cancellationToken)
    {
        var helperPath = ResolveShellHelperPath();
        if (helperPath is not null)
        {
            var arguments = enabled ? "--register-context-menu" : "--unregister-context-menu";
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }

        if (VerifyRegistrationState(enabled))
        {
            return;
        }

        if (enabled && helperPath is not null)
        {
            RegisterDirectly(helperPath);
            return;
        }

        UnregisterDirectly();
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

    private static bool VerifyRegistrationState(bool enabled)
    {
        return enabled
            ? Registry.CurrentUser.OpenSubKey(VerbKeyPath) is not null
            : Registry.CurrentUser.OpenSubKey(VerbKeyPath) is null;
    }

    private static void RegisterDirectly(string helperPath)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(VerbKeyPath);
        using var commandKey = Registry.CurrentUser.CreateSubKey(CommandKeyPath);
        if (verbKey is null || commandKey is null)
        {
            return;
        }

        verbKey.SetValue(string.Empty, "Copy Share Link", RegistryValueKind.String);
        verbKey.SetValue("MUIVerb", "Copy Share Link", RegistryValueKind.String);
        verbKey.SetValue("Icon", helperPath, RegistryValueKind.String);
        commandKey.SetValue(string.Empty, $"\"{helperPath}\" \"%1\"", RegistryValueKind.String);
    }

    private static void UnregisterDirectly()
    {
        Registry.CurrentUser.DeleteSubKeyTree(VerbKeyPath, throwOnMissingSubKey: false);
    }
}
