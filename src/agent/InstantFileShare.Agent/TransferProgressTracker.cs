using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal static class TransferProgressTracker
{
    public static async Task TrackReadProgressAsync(
        IShareCoordinator coordinator,
        string transferId,
        MeteredReadStream meteredStream,
        long initialBytesSent,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await coordinator.UpdateTransferProgressAsync(transferId, initialBytesSent + meteredStream.BytesRead, cancellationToken);
        }
    }

    public static async Task TrackWriteProgressAsync(
        IShareCoordinator coordinator,
        string transferId,
        MeteredWriteStream meteredStream,
        Func<long>? progressBytes,
        long progressTotalBytes,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await coordinator.UpdateTransferProgressAsync(
                transferId,
                meteredStream.BytesWritten,
                cancellationToken,
                progressBytes?.Invoke(),
                progressTotalBytes);
        }
    }
}
