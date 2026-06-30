using InstantFileShare.Agent;

namespace InstantFileShare.Agent.Tests;

public sealed class DashboardLauncherTests
{
    [Fact]
    public void CreateStartInfo_WithRepositoryRoot_UsesDevelopmentDashboardCommand()
    {
        var repositoryRoot = FindRepositoryRoot();

        var startInfo = DashboardLauncher.CreateStartInfo(repositoryRoot);

        Assert.NotNull(startInfo);
        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal("/c npm run electron:dev", startInfo.Arguments);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, "src", "ui")),
            Path.GetFullPath(startInfo.WorkingDirectory));
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateStartInfo_WithMissingUiPackage_ReturnsNull()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ifs-dashboard-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            Assert.Null(DashboardLauncher.CreateStartInfo(tempDirectory));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void AgentPaths_GetRepositoryRoot_FindsSolutionRootFromTestOutput()
    {
        var repositoryRoot = FindRepositoryRoot();

        Assert.Equal(repositoryRoot, AgentPaths.GetRepositoryRoot());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "InstantFileShare.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
