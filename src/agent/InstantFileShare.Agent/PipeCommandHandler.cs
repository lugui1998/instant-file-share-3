using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class PipeCommandHandler(
    IShareCoordinator shareCoordinator,
    IClipboardService clipboardService,
    INotificationService notificationService,
    IUiLauncher dashboardLauncher,
    ILogger<PipeCommandHandler> logger) : IPipeCommandHandler
{
    public async Task<PipeCommandResult> HandleAsync(PipeCommand command, CancellationToken cancellationToken)
    {
        var operation = command.Command?.Trim().ToLowerInvariant();
        return operation switch
        {
            "share" when !string.IsNullOrWhiteSpace(command.FilePath) => await ShareAsync(command.FilePath, ShareKind.File, null, cancellationToken),
            "share-folder-zip" when !string.IsNullOrWhiteSpace(command.FilePath) => await ShareAsync(command.FilePath, ShareKind.Folder, FolderShareEntryPoint.Zip, cancellationToken),
            "share-folder-browse" when !string.IsNullOrWhiteSpace(command.FilePath) => await ShareAsync(command.FilePath, ShareKind.Folder, FolderShareEntryPoint.Browse, cancellationToken),
            "open-dashboard" => await OpenDashboardAsync(cancellationToken),
            _ => new PipeCommandResult(false, "Unsupported command."),
        };
    }

    private async Task<PipeCommandResult> ShareAsync(string filePath, ShareKind shareKind, FolderShareEntryPoint? folderEntryPoint, CancellationToken cancellationToken)
    {
        var (share, url) = await shareCoordinator.CreateShareAsync(
            new CreateShareRequest(filePath, ShareKind: shareKind, PrimaryFolderEntryPoint: folderEntryPoint),
            cancellationToken);
        try
        {
            await clipboardService.SetTextAsync(url, cancellationToken);
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

    private async Task<PipeCommandResult> OpenDashboardAsync(CancellationToken cancellationToken)
    {
        await dashboardLauncher.OpenDashboardAsync(cancellationToken);
        return new PipeCommandResult(true, "Dashboard launch requested.");
    }
}
