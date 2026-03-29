using InstantFileShare.Agent;

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
}
