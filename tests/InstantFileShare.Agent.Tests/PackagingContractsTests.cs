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
        Assert.DoesNotContain("--unregister-context-menu", installerScript);
    }

    [Fact]
    public void CleanupScript_RemovesAllContextMenuKeys_AndStopsAgentTree()
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
    }
}
