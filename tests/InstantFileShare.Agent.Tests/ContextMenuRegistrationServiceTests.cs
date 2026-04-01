using InstantFileShare.Agent;

namespace InstantFileShare.Agent.Tests;

public sealed class ContextMenuRegistrationServiceTests
{
    [Fact]
    public void BuildDesktopFolderExclusionAppliesTo_ReturnsExpectedQuery()
    {
        var query = ContextMenuRegistrationService.BuildDesktopFolderExclusionAppliesTo(@"C:\Users\TestUser\Desktop");

        Assert.Equal("System.ItemPathDisplay:<>=\"C:\\Users\\TestUser\\Desktop\"", query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDesktopFolderExclusionAppliesTo_ReturnsNullWhenDesktopPathMissing(string? desktopPath)
    {
        var query = ContextMenuRegistrationService.BuildDesktopFolderExclusionAppliesTo(desktopPath);

        Assert.Null(query);
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
