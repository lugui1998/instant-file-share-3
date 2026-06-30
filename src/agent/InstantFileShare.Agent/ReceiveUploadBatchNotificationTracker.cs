using System.Collections.Concurrent;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class ReceiveUploadBatchNotificationTracker(INotificationService notificationService)
{
    private readonly ConcurrentDictionary<string, ReceiveUploadBatchState> _batches = new(StringComparer.Ordinal);

    public void RecordSuccessfulUploads(string receiveLinkId, string? batchId, string targetDisplayName, int uploadedCount)
    {
        if (uploadedCount <= 0 || string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        var key = BuildKey(receiveLinkId, batchId);
        _batches.AddOrUpdate(
            key,
            _ => new ReceiveUploadBatchState(targetDisplayName, uploadedCount),
            (_, current) => current with
            {
                TargetDisplayName = targetDisplayName,
                UploadedCount = current.UploadedCount + uploadedCount,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            });
    }

    public bool TryNotifyCompletedBatch(string receiveLinkId, string? batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return false;
        }

        var key = BuildKey(receiveLinkId, batchId);
        if (!_batches.TryRemove(key, out var batch) || batch.UploadedCount <= 0)
        {
            return false;
        }

        var summary = batch.UploadedCount == 1
            ? $"1 file received in {batch.TargetDisplayName}"
            : $"{batch.UploadedCount} files received in {batch.TargetDisplayName}";
        notificationService.ShowInfo("Files received", summary);
        return true;
    }

    private static string BuildKey(string receiveLinkId, string batchId)
    {
        return $"{receiveLinkId}:{batchId.Trim()}";
    }

    private sealed record ReceiveUploadBatchState(
        string TargetDisplayName,
        int UploadedCount)
    {
        public DateTimeOffset LastUpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
