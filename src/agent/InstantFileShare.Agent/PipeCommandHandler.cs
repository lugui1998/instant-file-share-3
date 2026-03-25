using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class PipeCommandHandler(
    IShareCoordinator shareCoordinator,
    ClipboardService clipboardService,
    NotificationService notificationService,
    DashboardLauncher dashboardLauncher,
    ILogger<PipeCommandHandler> logger) : IPipeCommandHandler
{
    public async Task<PipeCommandResult> HandleAsync(PipeCommand command, CancellationToken cancellationToken)
    {
        var operation = command.Command?.Trim().ToLowerInvariant();
        return operation switch
        {
            "share" when !string.IsNullOrWhiteSpace(command.FilePath) => await ShareAsync(command.FilePath, cancellationToken),
            "open-dashboard" => OpenDashboard(),
            _ => new PipeCommandResult(false, "Unsupported command."),
        };
    }

    private async Task<PipeCommandResult> ShareAsync(string filePath, CancellationToken cancellationToken)
    {
        var (share, url) = await shareCoordinator.CreateShareAsync(new CreateShareRequest(filePath), cancellationToken);
        try
        {
            await clipboardService.SetTextAsync(url);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Share {ShareId} was created but copying the URL to the clipboard failed.", share.Id);
        }

        try
        {
            notificationService.ShowInfo("Share link copied", share.FileName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Share {ShareId} was created but the success notification failed.", share.Id);
        }

        return new PipeCommandResult(true, "Share link copied to clipboard.", url);
    }

    private PipeCommandResult OpenDashboard()
    {
        dashboardLauncher.Launch(AgentPaths.GetRepositoryRoot());
        return new PipeCommandResult(true, "Dashboard launch requested.");
    }
}
