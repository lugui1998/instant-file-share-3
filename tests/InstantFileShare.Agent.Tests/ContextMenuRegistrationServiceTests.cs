using InstantFileShare.Agent;

namespace InstantFileShare.Agent.Tests;

public sealed class ContextMenuRegistrationServiceTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public void ShouldUseCascadingContextMenu_ReturnsTrueOnlyWhenMultipleItemsAreEnabled(
        bool zipEnabled,
        bool browseEnabled,
        bool receiveEnabled,
        bool expected)
    {
        var shouldUseCascadingMenu = ContextMenuRegistrationService.ShouldUseCascadingContextMenu(
            zipEnabled,
            browseEnabled,
            receiveEnabled);

        Assert.Equal(expected, shouldUseCascadingMenu);
    }

    [Fact]
    public void SelectedFolderRegistrations_DoNotExcludeDesktopItems()
    {
        var repositoryRoot = AgentPaths.GetRepositoryRoot();
        var agentSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "agent",
            "InstantFileShare.Agent",
            "ContextMenuRegistrationService.cs"));
        var shellSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "shell-extension",
            "src",
            "main.cpp"));

        Assert.DoesNotContain("System.ItemPathDisplay:<>", agentSource);
        Assert.DoesNotContain("System.ItemPathDisplay:<>", shellSource);
    }

    [Fact]
    public void FolderRegistrations_UseInstantFileShareCascadeForMultipleActions()
    {
        var repositoryRoot = AgentPaths.GetRepositoryRoot();
        var agentSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "agent",
            "InstantFileShare.Agent",
            "ContextMenuRegistrationService.cs"));
        var shellSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "shell-extension",
            "src",
            "main.cpp"));

        Assert.Contains(@"Software\Classes\Directory\shell\InstantFileShare", agentSource);
        Assert.Contains(@"Software\Classes\Directory\ContextMenus\InstantFileShare", agentSource);
        Assert.Contains(@"Software\Classes\Directory\Background\shell\InstantFileShare", agentSource);
        Assert.Contains(@"Software\Classes\Directory\Background\ContextMenus\InstantFileShare", agentSource);
        Assert.Contains(@"Software\Classes\DesktopBackground\Shell\InstantFileShare", agentSource);
        Assert.Contains(@"Software\Classes\DesktopBackground\ContextMenus\InstantFileShare", agentSource);
        Assert.Contains("ExtendedSubCommandsKey", agentSource);
        Assert.DoesNotContain("\"SubCommands\"", agentSource);

        Assert.Contains(@"Software\Classes\Directory\shell\InstantFileShare", shellSource);
        Assert.Contains(@"Software\Classes\Directory\ContextMenus\InstantFileShare", shellSource);
        Assert.Contains(@"Software\Classes\Directory\Background\shell\InstantFileShare", shellSource);
        Assert.Contains(@"Software\Classes\Directory\Background\ContextMenus\InstantFileShare", shellSource);
        Assert.Contains(@"Software\Classes\DesktopBackground\Shell\InstantFileShare", shellSource);
        Assert.Contains(@"Software\Classes\DesktopBackground\ContextMenus\InstantFileShare", shellSource);
        Assert.Contains("ExtendedSubCommandsKey", shellSource);
        Assert.DoesNotContain("L\"SubCommands\"", shellSource);
    }

    [Fact]
    public void ResolveDesktopBackgroundTargetPath_ReturnsNormalizedDesktopPath()
    {
        var targetPath = ContextMenuRegistrationService.ResolveDesktopBackgroundTargetPath(@"C:\Users\TestUser\Desktop\");

        Assert.Equal(@"C:\Users\TestUser\Desktop", targetPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDesktopBackgroundTargetPath_FallsBackToShellTokenWhenDesktopPathMissing(string? desktopPath)
    {
        var targetPath = ContextMenuRegistrationService.ResolveDesktopBackgroundTargetPath(desktopPath);

        Assert.Equal("%V", targetPath);
    }

    [Fact]
    public void ResolveFolderBackgroundTargetPath_ReturnsShellToken()
    {
        var targetPath = ContextMenuRegistrationService.ResolveFolderBackgroundTargetPath();

        Assert.Equal("%V", targetPath);
    }
}
