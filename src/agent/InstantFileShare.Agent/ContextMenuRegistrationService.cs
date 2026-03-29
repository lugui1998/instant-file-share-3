using InstantFileShare.Core;
using Microsoft.Win32;
using System.Diagnostics;

namespace InstantFileShare.Agent;

internal sealed class ContextMenuRegistrationService : IContextMenuRegistrationService
{
    private const string FileVerbKeyPath = @"Software\Classes\*\shell\InstantFileShare";
    private const string FileCommandKeyPath = @"Software\Classes\*\shell\InstantFileShare\command";
    private const string FolderZipVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderZip";
    private const string FolderZipCommandKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderZip\command";
    private const string FolderZipBackgroundVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderZip";
    private const string FolderZipBackgroundCommandKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderZip\command";
    private const string FolderBrowseVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderBrowse";
    private const string FolderBrowseCommandKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderBrowse\command";
    private const string FolderBrowseBackgroundVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse";
    private const string FolderBrowseBackgroundCommandKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse\command";

    public async Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var helperPath = ResolveShellHelperPath();
        if (helperPath is not null)
        {
            await RunHelperRegistrationAsync(helperPath, settings.AddFileContextMenuButton, "--register-file-context-menu", "--unregister-file-context-menu", cancellationToken);
            await RunHelperRegistrationAsync(helperPath, settings.AddFolderZipContextMenuButton, "--register-folder-zip-context-menu", "--unregister-folder-zip-context-menu", cancellationToken);
            await RunHelperRegistrationAsync(helperPath, settings.AddFolderBrowseContextMenuButton, "--register-folder-browse-context-menu", "--unregister-folder-browse-context-menu", cancellationToken);
        }

        if (helperPath is null)
        {
            if (!settings.AddFileContextMenuButton)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FileVerbKeyPath, throwOnMissingSubKey: false);
            }

            if (!settings.AddFolderZipContextMenuButton)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FolderZipVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(FolderZipBackgroundVerbKeyPath, throwOnMissingSubKey: false);
            }

            if (!settings.AddFolderBrowseContextMenuButton)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FolderBrowseVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(FolderBrowseBackgroundVerbKeyPath, throwOnMissingSubKey: false);
            }

            return;
        }

        ApplyDirectRegistration(
            settings.AddFileContextMenuButton,
            FileVerbKeyPath,
            FileCommandKeyPath,
            "Copy Share Link",
            $"\"{helperPath}\" --share-file \"%1\"");
        ApplyDirectRegistration(
            settings.AddFolderZipContextMenuButton,
            FolderZipVerbKeyPath,
            FolderZipCommandKeyPath,
            "Share Folder as ZIP",
            $"\"{helperPath}\" --share-folder-zip \"%1\"");
        ApplyDirectRegistration(
            settings.AddFolderZipContextMenuButton,
            FolderZipBackgroundVerbKeyPath,
            FolderZipBackgroundCommandKeyPath,
            "Share Folder as ZIP",
            $"\"{helperPath}\" --share-folder-zip \"%V\"");
        ApplyDirectRegistration(
            settings.AddFolderBrowseContextMenuButton,
            FolderBrowseVerbKeyPath,
            FolderBrowseCommandKeyPath,
            "Share Folder for Browsing",
            $"\"{helperPath}\" --share-folder-browse \"%1\"");
        ApplyDirectRegistration(
            settings.AddFolderBrowseContextMenuButton,
            FolderBrowseBackgroundVerbKeyPath,
            FolderBrowseBackgroundCommandKeyPath,
            "Share Folder for Browsing",
            $"\"{helperPath}\" --share-folder-browse \"%V\"");
    }

    private static async Task RunHelperRegistrationAsync(string helperPath, bool enabled, string registerArgument, string unregisterArgument, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = enabled ? registerArgument : unregisterArgument,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is not null)
        {
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    private static void ApplyDirectRegistration(bool enabled, string verbKeyPath, string commandKeyPath, string label, string command)
    {
        if (enabled)
        {
            RegisterDirectly(verbKeyPath, commandKeyPath, label, command);
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(verbKeyPath, throwOnMissingSubKey: false);
    }

    private static string? ResolveShellHelperPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var repositoryRoot = AgentPaths.GetRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "instant_file_share_shell.exe"),
            Path.Combine(baseDirectory, "shell", "instant_file_share_shell.exe"),
            Path.Combine(repositoryRoot, "build", "shell-extension", "Debug", "instant_file_share_shell.exe"),
            Path.Combine(repositoryRoot, "build", "shell-extension", "Release", "instant_file_share_shell.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void RegisterDirectly(string verbKeyPath, string commandKeyPath, string label, string command)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(verbKeyPath);
        using var commandKey = Registry.CurrentUser.CreateSubKey(commandKeyPath);
        if (verbKey is null || commandKey is null)
        {
            return;
        }

        verbKey.SetValue(string.Empty, label, RegistryValueKind.String);
        verbKey.SetValue("MUIVerb", label, RegistryValueKind.String);
        verbKey.SetValue("Icon", ResolveContextMenuIconPath() ?? string.Empty, RegistryValueKind.String);
        commandKey.SetValue(string.Empty, command, RegistryValueKind.String);
    }

    private static string? ResolveContextMenuIconPath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        var executableName = "InstantFileShare.Agent.exe";
        var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
        return File.Exists(executablePath) ? executablePath : null;
    }
}
