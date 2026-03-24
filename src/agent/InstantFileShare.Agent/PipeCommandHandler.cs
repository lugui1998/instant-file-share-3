using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class PipeCommandHandler(
    IShareCoordinator shareCoordinator,
    ClipboardService clipboardService,
    NotificationService notificationService,
    DashboardLauncher dashboardLauncher) : IPipeCommandHandler
{
    public async Task<PipeCommandResult> HandleAsync(PipeCommand command, CancellationToken cancellationToken)
    {
        return command.Command.ToLowerInvariant() switch
        {
            "share" when !string.IsNullOrWhiteSpace(command.FilePath) => await ShareAsync(command.FilePath, cancellationToken),
            "open-dashboard" => OpenDashboard(),
            _ => new PipeCommandResult(false, "Unsupported command."),
        };
    }

    private async Task<PipeCommandResult> ShareAsync(string filePath, CancellationToken cancellationToken)
    {
        var (share, url) = await shareCoordinator.CreateShareAsync(new CreateShareRequest(filePath), cancellationToken);
        await clipboardService.SetTextAsync(url);
        notificationService.ShowInfo("Share link copied", share.FileName);
        return new PipeCommandResult(true, "Share link copied to clipboard.", url);
    }

    private PipeCommandResult OpenDashboard()
    {
        dashboardLauncher.Launch(AgentPaths.GetRepositoryRoot());
        return new PipeCommandResult(true, "Dashboard launch requested.");
    }
}
