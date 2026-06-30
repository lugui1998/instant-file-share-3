using InstantFileShare.Core;
using Microsoft.Win32;
using System.Diagnostics;

namespace InstantFileShare.Agent;

internal sealed class ContextMenuRegistrationService : IContextMenuRegistrationService
{
    private const string FileVerbKeyPath = @"Software\Classes\*\shell\InstantFileShare";
    private const string FileCommandKeyPath = @"Software\Classes\*\shell\InstantFileShare\command";
    private const string ContextMenuGroupLabel = "Instant File Share";
    private const string FolderGroupVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShare";
    private const string FolderGroupSubCommandsValue = @"Directory\ContextMenus\InstantFileShare";
    private const string FolderGroupSubCommandsKeyPath = @"Software\Classes\Directory\ContextMenus\InstantFileShare";
    private const string FolderBackgroundGroupVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShare";
    private const string FolderBackgroundGroupSubCommandsValue = @"Directory\Background\ContextMenus\InstantFileShare";
    private const string FolderBackgroundGroupSubCommandsKeyPath = @"Software\Classes\Directory\Background\ContextMenus\InstantFileShare";
    private const string DesktopFolderBackgroundGroupVerbKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShare";
    private const string DesktopFolderBackgroundGroupSubCommandsValue = @"DesktopBackground\ContextMenus\InstantFileShare";
    private const string DesktopFolderBackgroundGroupSubCommandsKeyPath = @"Software\Classes\DesktopBackground\ContextMenus\InstantFileShare";
    private const string FolderZipVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderZip";
    private const string FolderZipCommandKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderZip\command";
    private const string FolderZipBackgroundVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderZip";
    private const string FolderZipBackgroundCommandKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderZip\command";
    private const string DesktopFolderZipBackgroundVerbKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderZip";
    private const string DesktopFolderZipBackgroundCommandKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderZip\command";
    private const string FolderBrowseVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderBrowse";
    private const string FolderBrowseCommandKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderBrowse\command";
    private const string FolderBrowseBackgroundVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse";
    private const string FolderBrowseBackgroundCommandKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse\command";
    private const string DesktopFolderBrowseBackgroundVerbKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderBrowse";
    private const string DesktopFolderBrowseBackgroundCommandKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderBrowse\command";
    private const string FolderReceiveVerbKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderReceive";
    private const string FolderReceiveCommandKeyPath = @"Software\Classes\Directory\shell\InstantFileShareFolderReceive\command";
    private const string FolderReceiveBackgroundVerbKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderReceive";
    private const string FolderReceiveBackgroundCommandKeyPath = @"Software\Classes\Directory\Background\shell\InstantFileShareFolderReceive\command";
    private const string DesktopFolderReceiveBackgroundVerbKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderReceive";
    private const string DesktopFolderReceiveBackgroundCommandKeyPath = @"Software\Classes\DesktopBackground\Shell\InstantFileShareFolderReceive\command";

    public async Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var helperPath = ResolveShellHelperPath();
        var folderBackgroundTargetPath = ResolveFolderBackgroundTargetPath();
        var desktopBackgroundTargetPath = ResolveDesktopBackgroundTargetPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (helperPath is not null)
        {
            await RunHelperRegistrationAsync(helperPath, settings.AddFileContextMenuButton, "--register-file-context-menu", "--unregister-file-context-menu", cancellationToken);
            await RunHelperRegistrationAsync(helperPath, settings.AddFolderZipContextMenuButton, "--register-folder-zip-context-menu", "--unregister-folder-zip-context-menu", cancellationToken);
            await RunHelperRegistrationAsync(helperPath, settings.AddFolderBrowseContextMenuButton, "--register-folder-browse-context-menu", "--unregister-folder-browse-context-menu", cancellationToken);
            await RunHelperRegistrationAsync(helperPath, settings.AddFolderReceiveContextMenuButton, "--register-folder-receive-context-menu", "--unregister-folder-receive-context-menu", cancellationToken);
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
                Registry.CurrentUser.DeleteSubKeyTree(DesktopFolderZipBackgroundVerbKeyPath, throwOnMissingSubKey: false);
            }

            if (!settings.AddFolderBrowseContextMenuButton)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FolderBrowseVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(FolderBrowseBackgroundVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(DesktopFolderBrowseBackgroundVerbKeyPath, throwOnMissingSubKey: false);
            }

            if (!settings.AddFolderReceiveContextMenuButton)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FolderReceiveVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(FolderReceiveBackgroundVerbKeyPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(DesktopFolderReceiveBackgroundVerbKeyPath, throwOnMissingSubKey: false);
            }

            return;
        }

        ApplyDirectRegistration(
            settings.AddFileContextMenuButton,
            FileVerbKeyPath,
            FileCommandKeyPath,
            "Copy Share Link",
            $"\"{helperPath}\" --share-file \"%1\"");
        ApplyFolderContextMenu(
            FolderGroupVerbKeyPath,
            FolderGroupSubCommandsValue,
            FolderGroupSubCommandsKeyPath,
            BuildFolderContextMenuItems(
                settings,
                FolderZipVerbKeyPath,
                FolderZipCommandKeyPath,
                FolderBrowseVerbKeyPath,
                FolderBrowseCommandKeyPath,
                FolderReceiveVerbKeyPath,
                FolderReceiveCommandKeyPath,
                $"\"{helperPath}\" --share-folder-zip \"%1\"",
                $"\"{helperPath}\" --share-folder-browse \"%1\"",
                $"\"{helperPath}\" --receive-here \"%1\""));
        ApplyFolderContextMenu(
            FolderBackgroundGroupVerbKeyPath,
            FolderBackgroundGroupSubCommandsValue,
            FolderBackgroundGroupSubCommandsKeyPath,
            BuildFolderContextMenuItems(
                settings,
                FolderZipBackgroundVerbKeyPath,
                FolderZipBackgroundCommandKeyPath,
                FolderBrowseBackgroundVerbKeyPath,
                FolderBrowseBackgroundCommandKeyPath,
                FolderReceiveBackgroundVerbKeyPath,
                FolderReceiveBackgroundCommandKeyPath,
                $"\"{helperPath}\" --share-folder-zip \"{folderBackgroundTargetPath}\"",
                $"\"{helperPath}\" --share-folder-browse \"{folderBackgroundTargetPath}\"",
                $"\"{helperPath}\" --receive-here \"{folderBackgroundTargetPath}\""));
        ApplyFolderContextMenu(
            DesktopFolderBackgroundGroupVerbKeyPath,
            DesktopFolderBackgroundGroupSubCommandsValue,
            DesktopFolderBackgroundGroupSubCommandsKeyPath,
            BuildFolderContextMenuItems(
                settings,
                DesktopFolderZipBackgroundVerbKeyPath,
                DesktopFolderZipBackgroundCommandKeyPath,
                DesktopFolderBrowseBackgroundVerbKeyPath,
                DesktopFolderBrowseBackgroundCommandKeyPath,
                DesktopFolderReceiveBackgroundVerbKeyPath,
                DesktopFolderReceiveBackgroundCommandKeyPath,
                $"\"{helperPath}\" --share-folder-zip \"{desktopBackgroundTargetPath}\"",
                $"\"{helperPath}\" --share-folder-browse \"{desktopBackgroundTargetPath}\"",
                $"\"{helperPath}\" --receive-here \"{desktopBackgroundTargetPath}\""));
    }

    internal static string ResolveDesktopBackgroundTargetPath(string? desktopFolderPath)
    {
        if (string.IsNullOrWhiteSpace(desktopFolderPath))
        {
            return "%V";
        }

        return Path.GetFullPath(Path.TrimEndingDirectorySeparator(desktopFolderPath));
    }

    internal static string ResolveFolderBackgroundTargetPath() => "%V";

    internal static bool ShouldUseCascadingContextMenu(params bool[] enabledItems) => enabledItems.Count(enabled => enabled) > 1;

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

    private static void ApplyDirectRegistration(bool enabled, string verbKeyPath, string commandKeyPath, string label, string command, string? appliesTo = null)
    {
        if (enabled)
        {
            RegisterDirectly(verbKeyPath, commandKeyPath, label, command, appliesTo);
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(verbKeyPath, throwOnMissingSubKey: false);
    }

    private static FolderContextMenuItem[] BuildFolderContextMenuItems(
        AppSettings settings,
        string zipVerbKeyPath,
        string zipCommandKeyPath,
        string browseVerbKeyPath,
        string browseCommandKeyPath,
        string receiveVerbKeyPath,
        string receiveCommandKeyPath,
        string zipCommand,
        string browseCommand,
        string receiveCommand)
    {
        return
        [
            new(settings.AddFolderBrowseContextMenuButton, browseVerbKeyPath, browseCommandKeyPath, "ShareFolderBrowse", "Share Folder for Browsing", browseCommand),
            new(settings.AddFolderReceiveContextMenuButton, receiveVerbKeyPath, receiveCommandKeyPath, "ReceiveHere", "Receive files here", receiveCommand),
            new(settings.AddFolderZipContextMenuButton, zipVerbKeyPath, zipCommandKeyPath, "ShareFolderZip", "Share Folder as ZIP", zipCommand),
        ];
    }

    private static void ApplyFolderContextMenu(
        string groupVerbKeyPath,
        string subCommandsValue,
        string subCommandsKeyPath,
        IReadOnlyCollection<FolderContextMenuItem> items)
    {
        if (ShouldUseCascadingContextMenu(items.Select(item => item.Enabled).ToArray()))
        {
            Registry.CurrentUser.DeleteSubKeyTree(subCommandsKeyPath, throwOnMissingSubKey: false);
            foreach (var item in items)
            {
                Registry.CurrentUser.DeleteSubKeyTree(item.LegacyVerbKeyPath, throwOnMissingSubKey: false);
            }

            RegisterCascadingMenu(groupVerbKeyPath, subCommandsValue, subCommandsKeyPath, items.Where(item => item.Enabled));
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(groupVerbKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(subCommandsKeyPath, throwOnMissingSubKey: false);
        foreach (var item in items)
        {
            ApplyDirectRegistration(item.Enabled, item.LegacyVerbKeyPath, item.LegacyCommandKeyPath, item.Label, item.Command);
        }
    }

    private static void RegisterCascadingMenu(
        string groupVerbKeyPath,
        string subCommandsValue,
        string subCommandsKeyPath,
        IEnumerable<FolderContextMenuItem> enabledItems)
    {
        Registry.CurrentUser.DeleteSubKeyTree(groupVerbKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(subCommandsKeyPath, throwOnMissingSubKey: false);

        using var groupKey = Registry.CurrentUser.CreateSubKey(groupVerbKeyPath);
        if (groupKey is null)
        {
            return;
        }

        groupKey.DeleteValue(string.Empty, throwOnMissingValue: false);
        groupKey.SetValue("MUIVerb", ContextMenuGroupLabel, RegistryValueKind.String);
        groupKey.SetValue("Icon", ResolveContextMenuIconPath() ?? string.Empty, RegistryValueKind.String);
        groupKey.SetValue("ExtendedSubCommandsKey", subCommandsValue, RegistryValueKind.String);

        foreach (var item in enabledItems)
        {
            var childVerbKeyPath = $@"{subCommandsKeyPath}\shell\{item.ChildKeyName}";
            var childCommandKeyPath = $@"{childVerbKeyPath}\command";
            RegisterDirectly(childVerbKeyPath, childCommandKeyPath, item.Label, item.Command, appliesTo: null);
        }
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

    private static void RegisterDirectly(string verbKeyPath, string commandKeyPath, string label, string command, string? appliesTo)
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
        if (appliesTo is not null)
        {
            verbKey.SetValue("AppliesTo", appliesTo, RegistryValueKind.String);
        }
        else
        {
            verbKey.DeleteValue("AppliesTo", throwOnMissingValue: false);
        }

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

    private sealed record FolderContextMenuItem(
        bool Enabled,
        string LegacyVerbKeyPath,
        string LegacyCommandKeyPath,
        string ChildKeyName,
        string Label,
        string Command);
}
