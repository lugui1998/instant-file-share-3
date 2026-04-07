using InstantFileShare.Agent;

var startupOptions = AgentStartupOptions.Parse(args);
if (startupOptions.RestartParentProcessId is int restartParentProcessId)
{
    WaitForParentProcessExit(restartParentProcessId);
}

var app = await Program.CreateAppAsync(args, cancellationToken: CancellationToken.None);
await app.RunAsync();

public partial class Program
{
    public static Task<WebApplication> CreateAppAsync(
        string[] args,
        AgentApplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return AgentApplication.BuildAsync(args, options, cancellationToken);
    }

    private static void WaitForParentProcessExit(int parentProcessId)
    {
        try
        {
            using var parentProcess = System.Diagnostics.Process.GetProcessById(parentProcessId);
            parentProcess.WaitForExit();
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
