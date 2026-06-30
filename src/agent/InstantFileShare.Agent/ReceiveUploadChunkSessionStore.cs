using System.Collections.Concurrent;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class ReceiveUploadChunkSessionStore
{
    private readonly ConcurrentDictionary<string, ReceiveUploadChunkSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _canceledUploadIds = new(StringComparer.Ordinal);

    public bool TryAdd(ReceiveUploadChunkSession session) => _sessions.TryAdd(session.UploadId, session);

    public bool TryGet(string uploadId, out ReceiveUploadChunkSession? session) => _sessions.TryGetValue(uploadId, out session);

    public bool IsCanceled(string uploadId) => _canceledUploadIds.ContainsKey(uploadId);

    public void MarkCanceled(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return;
        }

        _canceledUploadIds.TryAdd(uploadId, 0);
        if (_sessions.TryGetValue(uploadId, out var session))
        {
            session.Cancel();
        }
    }

    public void Remove(string uploadId) => _sessions.TryRemove(uploadId, out _);
}

internal sealed class ReceiveUploadChunkSession(
    string uploadId,
    string receiveLinkId,
    string token,
    string fileName,
    string clientRelativePath,
    string storedRelativePath,
    string destinationPath,
    string partialDestinationPath,
    long expectedTotalBytes,
    TransferSnapshot transfer)
{
    public string UploadId { get; } = uploadId;
    public string ReceiveLinkId { get; } = receiveLinkId;
    public string Token { get; } = token;
    public string FileName { get; } = fileName;
    public string ClientRelativePath { get; } = clientRelativePath;
    public string StoredRelativePath { get; } = storedRelativePath;
    public string DestinationPath { get; } = destinationPath;
    public string PartialDestinationPath { get; } = partialDestinationPath;
    public long ExpectedTotalBytes { get; } = expectedTotalBytes;
    public TransferSnapshot Transfer { get; } = transfer;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public long BytesWritten { get; set; }
    public CancellationToken CancellationToken => _cancellation.Token;

    private readonly CancellationTokenSource _cancellation = new();

    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch
        {
        }
    }
}
