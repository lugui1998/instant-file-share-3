using InstantFileShare.Agent;

namespace InstantFileShare.Agent.Tests;

public sealed class ContextMenuRegistrationServiceTests
{
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
