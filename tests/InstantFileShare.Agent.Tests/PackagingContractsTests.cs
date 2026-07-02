using System.Diagnostics;

namespace InstantFileShare.Agent.Tests;

public sealed class PackagingContractsTests
{
    [Fact]
    public void InstallerUninstall_UsesSupportedUnregisterCommands_AndStopsAgentTree()
    {
        var installerScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "installer", "InstantFileShare.iss"));

        Assert.Contains("--unregister-file-context-menu", installerScript);
        Assert.Contains("--unregister-folder-zip-context-menu", installerScript);
        Assert.Contains("--unregister-folder-browse-context-menu", installerScript);
        Assert.Contains("--unregister-folder-receive-context-menu", installerScript);
        Assert.Contains("InstantFileShare.Agent.exe", installerScript);
        Assert.Contains("/F /T", installerScript);
        Assert.Contains("Software\\Classes\\Directory\\shell\\InstantFileShare", installerScript);
        Assert.Contains("Software\\Classes\\Directory\\ContextMenus\\InstantFileShare", installerScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\shell\\InstantFileShare", installerScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\ContextMenus\\InstantFileShare", installerScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\Shell\\InstantFileShare", installerScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\ContextMenus\\InstantFileShare", installerScript);
        Assert.Contains("{localappdata}\\InstantFileShare", installerScript);
        Assert.Contains("{userappdata}\\{#AppName}", installerScript);
        Assert.Contains("{localappdata}\\{#AppName}", installerScript);
        Assert.DoesNotContain("--unregister-context-menu", installerScript);
    }

    [Fact]
    public void InstallerUninstall_OffersOptInCloudflaredRemoval_OffByDefault()
    {
        var installerScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "installer", "InstantFileShare.iss"));

        Assert.Contains("CloudflaredWingetUninstallArgs", installerScript);
        Assert.Contains("uninstall --id Cloudflare.cloudflared -e --disable-interactivity", installerScript);
        Assert.Contains("UninstallSilent", installerScript);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON2", installerScript);
        Assert.Contains("UninstallCloudflared := ShouldUninstallCloudflared", installerScript);
        Assert.Contains("if UninstallCloudflared then", installerScript);
    }

    [Fact]
    public void Installer_OffersOptInUserPathRegistration_AndRemovesPathOnUninstall()
    {
        var installerScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "installer", "InstantFileShare.iss"));

        Assert.Contains("ChangesEnvironment=yes", installerScript);
        Assert.Contains("Name: \"addtopath\"", installerScript);
        Assert.Contains("Add Instant File Share to the current user's PATH", installerScript);
        Assert.Contains("Flags: unchecked", installerScript);
        Assert.Contains("WizardIsTaskSelected('addtopath')", installerScript);
        Assert.Contains("RegQueryStringValue(HKCU, UserEnvironmentKey, UserPathValueName", installerScript);
        Assert.Contains("RegWriteExpandStringValue(HKCU, UserEnvironmentKey, UserPathValueName", installerScript);
        Assert.Contains("RemoveInstallDirectoryFromUserPath", installerScript);
    }

    [Fact]
    public void CleanupScript_RemovesAllContextMenuKeys_AppDataPaths_AndStopsAgentTree()
    {
        var cleanupScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "scripts", "uninstall-clean.ps1"));

        Assert.Contains("Software\\Classes\\*\\shell\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\shell\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\ContextMenus\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\shell\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\ContextMenus\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\Shell\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\ContextMenus\\InstantFileShare", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\shell\\InstantFileShareFolderZip", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\shell\\InstantFileShareFolderZip", cleanupScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\Shell\\InstantFileShareFolderZip", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\shell\\InstantFileShareFolderBrowse", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\shell\\InstantFileShareFolderBrowse", cleanupScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\Shell\\InstantFileShareFolderBrowse", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\shell\\InstantFileShareFolderReceive", cleanupScript);
        Assert.Contains("Software\\Classes\\Directory\\Background\\shell\\InstantFileShareFolderReceive", cleanupScript);
        Assert.Contains("Software\\Classes\\DesktopBackground\\Shell\\InstantFileShareFolderReceive", cleanupScript);
        Assert.Contains("/T /F", cleanupScript);
        Assert.Contains("GetFolderPath('LocalApplicationData')) 'InstantFileShare'", cleanupScript);
        Assert.Contains("GetFolderPath('ApplicationData')) $appName", cleanupScript);
        Assert.Contains("GetFolderPath('LocalApplicationData')) $appName", cleanupScript);
    }

    [Fact]
    public void CleanupScript_RemovesInstallDirectoryFromUserPath()
    {
        var cleanupScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "scripts", "uninstall-clean.ps1"));

        Assert.Contains("HKCU:\\Environment", cleanupScript);
        Assert.Contains("function Remove-InstallDirectoryFromUserPath", cleanupScript);
        Assert.Contains("Normalize-UserPathEntry", cleanupScript);
        Assert.Contains("[Environment]::SetEnvironmentVariable('Path', $updatedPath, 'User')", cleanupScript);
        Assert.Contains("Remove-InstallDirectoryFromUserPath -InstallDirectory $installLocation", cleanupScript);
    }

    [Fact]
    public void CleanupScript_UninstallsCloudflaredOnlyWhenExplicitSwitchIsSet()
    {
        var cleanupScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "scripts", "uninstall-clean.ps1"));

        Assert.Contains("[switch]$UninstallCloudflared", cleanupScript);
        Assert.Contains("if ($UninstallCloudflared)", cleanupScript);
        Assert.Contains("uninstall --id Cloudflare.cloudflared -e --disable-interactivity", cleanupScript);
    }

    [Fact]
    public void RepositoryMetadata_DoesNotTrackLocalWorktreesOrGithubWorkflows()
    {
        var repoRoot = AgentPaths.GetRepositoryRoot();
        var gitignore = File.ReadAllText(Path.Combine(repoRoot, ".gitignore"));

        Assert.Contains(".codex-worktrees/", gitignore);
        Assert.Contains(".github/workflows/", gitignore);

        var trackedFiles = RunGit(repoRoot, "ls-files", ".codex-worktrees", ".github/workflows");
        Assert.True(string.IsNullOrWhiteSpace(trackedFiles), $"These local-only paths must not be tracked:{Environment.NewLine}{trackedFiles}");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "Failed to start git.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("git did not exit within 10 seconds.");
        }

        Assert.True(process.ExitCode == 0, $"git exited with code {process.ExitCode}:{Environment.NewLine}{error}");

        return output;
    }
}
