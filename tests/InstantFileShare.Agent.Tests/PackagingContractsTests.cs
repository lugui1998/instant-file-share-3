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
    public void CleanupScript_RemovesAllContextMenuKeys_AppDataPaths_AndStopsAgentTree()
    {
        var cleanupScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "scripts", "uninstall-clean.ps1"));

        Assert.Contains("Software\\Classes\\*\\shell\\InstantFileShare", cleanupScript);
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
    public void CleanupScript_UninstallsCloudflaredOnlyWhenExplicitSwitchIsSet()
    {
        var cleanupScript = File.ReadAllText(Path.Combine(AgentPaths.GetRepositoryRoot(), "scripts", "uninstall-clean.ps1"));

        Assert.Contains("[switch]$UninstallCloudflared", cleanupScript);
        Assert.Contains("if ($UninstallCloudflared)", cleanupScript);
        Assert.Contains("uninstall --id Cloudflare.cloudflared -e --disable-interactivity", cleanupScript);
    }

    [Fact]
    public void GitHubActions_RunWindowsCi_AndPublishTaggedReleases()
    {
        var repoRoot = AgentPaths.GetRepositoryRoot();
        var ciWorkflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("runs-on: windows-latest", ciWorkflow);
        Assert.Contains("dotnet build InstantFileShare.slnx", ciWorkflow);
        Assert.Contains("dotnet test InstantFileShare.slnx", ciWorkflow);
        Assert.Contains("npm test", ciWorkflow);
        Assert.Contains("npm run build", ciWorkflow);
        Assert.Contains("cmake --build build/shell-extension --config Debug", ciWorkflow);

        Assert.Contains("contents: write", releaseWorkflow);
        Assert.Contains("- v*.*.*", releaseWorkflow);
        Assert.Contains("choco install innosetup --yes --no-progress", releaseWorkflow);
        Assert.Contains("dotnet test InstantFileShare.slnx", releaseWorkflow);
        Assert.Contains("npm test", releaseWorkflow);
        Assert.Contains(".\\scripts\\build-installer.ps1", releaseWorkflow);
        Assert.Contains("gh release create", releaseWorkflow);
        Assert.Contains("gh release upload", releaseWorkflow);
    }
}
