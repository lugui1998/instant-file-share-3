using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstantFileShare.Core;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal sealed class ShareCoordinator(
    IShareStore shareStore,
    IRuntimeEventStream runtimeEventStream,
    IBootstrapSettingsSnapshotStore bootstrapSettingsSnapshotStore,
    IAgentLifecycleManager agentLifecycleManager,
    IExplorerLauncher explorerLauncher,
    CloudflaredSupervisor cloudflaredSupervisor,
    ExternalAddressResolver externalAddressResolver,
    IStartupRegistrationService startupRegistrationService,
    IContextMenuRegistrationService contextMenuRegistrationService,
    IHttpClientFactory httpClientFactory) : IShareCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan TransferResumeWindow = TimeSpan.FromMinutes(30);
    private const long TransferResumeToleranceBytes = 1024 * 1024;
    private static readonly TimeSpan ExpiryComparisonTolerance = TimeSpan.FromSeconds(5);
    private readonly object _transferLock = new();
    private readonly SemaphoreSlim _receiveLinkBytesGate = new(1, 1);
    private readonly SemaphoreSlim _transferLoadGate = new(1, 1);
    private readonly Dictionary<string, TransferSnapshot> _activeTransfers = [];
    private readonly List<TransferSnapshot> _completedTransfers = [];
    private bool _transfersLoaded;

    public async Task<(ShareRecord Share, string Url)> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken)
    {
        var shareKind = request.ShareKind;
        var fileInfo = shareKind == ShareKind.File ? new FileInfo(request.FilePath) : null;
        var directoryInfo = shareKind == ShareKind.Folder ? new DirectoryInfo(request.FilePath) : null;

        if (shareKind == ShareKind.File && fileInfo is { Exists: false })
        {
            throw new FileNotFoundException("The selected file does not exist.", request.FilePath);
        }

        if (shareKind == ShareKind.Folder && directoryInfo is { Exists: false })
        {
            throw new DirectoryNotFoundException("The selected folder does not exist.");
        }

        if (shareKind == ShareKind.Folder &&
            directoryInfo is not null &&
            directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Folders backed by junctions, symlinks, or other reparse points cannot be shared.");
        }

        if (shareKind == ShareKind.Folder &&
            directoryInfo is not null &&
            directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Folders that rely on symlinks, junctions, or other reparse points are not supported.");
        }

        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var mode = request.PublishMode ?? settings.DefaultPublishMode;
        var publicBaseUrl = await EnsureTunnelBaseUrlAsync(mode, cancellationToken)
            ?? throw new InvalidOperationException(GetMissingPublishModeMessage(mode));

        var existingShare = await TryGetReusableShareAsync(fileInfo, directoryInfo, request, settings, mode, publicBaseUrl, cancellationToken);
        if (existingShare is not null)
        {
            return (existingShare, ShareUrlBuilder.Build(existingShare));
        }

        var token = await GenerateUniqueShareTokenAsync(NormalizePublicTokenLength(settings.PublicTokenLength), cancellationToken);
        var share = shareKind switch
        {
            ShareKind.Folder when directoryInfo is not null => BuildFolderShare(directoryInfo, request, settings, mode, publicBaseUrl, token),
            ShareKind.File when fileInfo is not null => BuildFileShare(fileInfo, request, settings, mode, publicBaseUrl, token),
            _ => throw new InvalidOperationException("Unsupported share target."),
        };

        await shareStore.AddShareAsync(share, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareCreated, DateTimeOffset.UtcNow, share), cancellationToken);
        return (share, ShareUrlBuilder.Build(share));
    }

    public async Task<(ReceiveLinkRecord ReceiveLink, string Url)> CreateReceiveLinkAsync(CreateReceiveLinkRequest request, CancellationToken cancellationToken)
    {
        var directoryInfo = new DirectoryInfo(request.DirectoryPath);
        if (!directoryInfo.Exists)
        {
            throw new DirectoryNotFoundException("The selected folder does not exist.");
        }

        if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Folders backed by junctions, symlinks, or other reparse points cannot be used as receive targets.");
        }

        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var mode = request.PublishMode ?? settings.DefaultPublishMode;
        var publicBaseUrl = await EnsureTunnelBaseUrlAsync(mode, cancellationToken)
            ?? throw new InvalidOperationException(GetMissingPublishModeMessage(mode));

        var token = await GenerateUniqueReceiveLinkTokenAsync(NormalizePublicTokenLength(settings.PublicTokenLength), cancellationToken);
        var receiveLink = new ReceiveLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = token,
            TargetDirectoryPath = directoryInfo.FullName,
            TargetDisplayName = directoryInfo.Name,
            PublicBaseUrl = publicBaseUrl,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc ?? ResolveDefaultReceiveExpiry(settings),
            MaxTotalBytes = NormalizeReceiveMaxTotalBytes(request.MaxTotalBytes ?? settings.DefaultReceiveMaxTotalBytes),
            BytesReceived = 0,
            PublishMode = mode,
            State = ReceiveLinkState.Active,
        };

        await shareStore.AddReceiveLinkAsync(receiveLink, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareCreated, DateTimeOffset.UtcNow, ShareListItem.FromReceiveLink(receiveLink)), cancellationToken);
        return (receiveLink, ShareUrlBuilder.BuildReceiveLink(receiveLink.PublicBaseUrl, receiveLink.Token));
    }

    private async Task<ShareRecord?> TryGetReusableShareAsync(
        FileInfo? fileInfo,
        DirectoryInfo? directoryInfo,
        CreateShareRequest request,
        AppSettings settings,
        PublishMode mode,
        string publicBaseUrl,
        CancellationToken cancellationToken)
    {
        var normalizedFilePath = (directoryInfo?.FullName ?? fileInfo?.FullName)!;
        var normalizedBaseUrl = NormalizeBaseUrl(publicBaseUrl);
        var shares = await shareStore.ListSharesAsync(cancellationToken);

        foreach (var share in shares)
        {
            if (!string.Equals(share.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase) ||
                share.PublishMode != mode ||
                !string.Equals(NormalizeBaseUrl(share.PublicBaseUrl), normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                !IsShareCompatibleWithRequest(share, request, settings))
            {
                continue;
            }

            var resolvedShare = await ResolveDownloadAsync(share.Token, cancellationToken);
            if (resolvedShare?.State == ShareState.Active)
            {
                return share;
            }
        }

        return null;
    }

    public async Task ReconcilePersistedSharesAsync(CancellationToken cancellationToken)
    {
        var shares = await shareStore.ListSharesAsync(cancellationToken);
        var settings = await shareStore.GetSettingsAsync(cancellationToken);

        foreach (var share in shares)
        {
            await ValidateShareAsync(share, settings, updateLastAccessedAtUtc: false, cancellationToken);
        }
    }

    public Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken) => shareStore.ListSharesAsync(cancellationToken);

    public async Task<IReadOnlyList<ShareListItem>> ListShareItemsAsync(CancellationToken cancellationToken)
    {
        var shares = await shareStore.ListSharesAsync(cancellationToken);
        var receiveLinks = await shareStore.ListReceiveLinksAsync(cancellationToken);

        return shares
            .Select(ShareListItem.FromShare)
            .Concat(receiveLinks.Select(ShareListItem.FromReceiveLink))
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToList();
    }

    public async Task ShowShareInExplorerAsync(string shareId, CancellationToken cancellationToken)
    {
        var share = await shareStore.GetShareByIdAsync(shareId, cancellationToken);
        if (share is not null)
        {
            await explorerLauncher.OpenDirectoryAsync(GetShareDirectoryPath(share), cancellationToken);
            return;
        }

        var receiveLink = await shareStore.GetReceiveLinkByIdAsync(shareId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected share no longer exists.");

        await explorerLauncher.OpenDirectoryAsync(receiveLink.TargetDirectoryPath, cancellationToken);
    }

    public async Task<ShareRecord?> ResolveDownloadAsync(string token, CancellationToken cancellationToken)
    {
        var share = await shareStore.GetShareByTokenAsync(token, cancellationToken);
        if (share is null)
        {
            return null;
        }

        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        return await ValidateShareAsync(share, settings, updateLastAccessedAtUtc: true, cancellationToken);
    }

    private static string GetShareDirectoryPath(ShareRecord share)
    {
        if (share.ShareKind == ShareKind.Folder)
        {
            return share.FilePath;
        }

        var directoryPath = Path.GetDirectoryName(share.FilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new DirectoryNotFoundException("The share location could not be resolved.");
        }

        return directoryPath;
    }

    private async Task<ShareRecord> ValidateShareAsync(
        ShareRecord share,
        AppSettings settings,
        bool updateLastAccessedAtUtc,
        CancellationToken cancellationToken)
    {
        if (share.State is ShareState.Revoked or ShareState.Broken or ShareState.Expired)
        {
            return share;
        }

        if (share.ExpiresAtUtc is not null && share.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            share = share with { State = ShareState.Expired };
            await shareStore.UpdateShareAsync(share, cancellationToken);
            return share;
        }

        if (share.MaxUses is not null && share.UseCount >= share.MaxUses)
        {
            return share with { State = ShareState.Expired, BrokenReason = "Maximum number of uses reached." };
        }

        if (share.ShareKind == ShareKind.Folder)
        {
            var directoryInfo = new DirectoryInfo(share.FilePath);
            if (!directoryInfo.Exists || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return await UpdateBrokenShareAsync(share, "The shared folder is no longer available.", cancellationToken);
            }

            return updateLastAccessedAtUtc
                ? share with { LastAccessedAtUtc = DateTimeOffset.UtcNow }
                : share;
        }

        var fileInfo = new FileInfo(share.FilePath);
        if (!fileInfo.Exists)
        {
            return await UpdateBrokenShareAsync(share, "The file is no longer available.", cancellationToken);
        }

        if (settings.FileChangeBehavior == FileChangeBehavior.Strict &&
            (fileInfo.Length != share.FileSize || fileInfo.LastWriteTimeUtc != share.FileModifiedAtUtc.UtcDateTime))
        {
            return await UpdateBrokenShareAsync(share, "The shared file changed after the link was created.", cancellationToken);
        }

        return updateLastAccessedAtUtc
            ? share with { LastAccessedAtUtc = DateTimeOffset.UtcNow }
            : share;
    }

    private async Task<ShareRecord> UpdateBrokenShareAsync(ShareRecord share, string brokenReason, CancellationToken cancellationToken)
    {
        var updatedShare = share with
        {
            State = ShareState.Broken,
            BrokenReason = brokenReason,
        };

        await shareStore.UpdateShareAsync(updatedShare, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareUpdated, DateTimeOffset.UtcNow, updatedShare), cancellationToken);
        return updatedShare;
    }

    public async Task<ReceiveLinkRecord?> ResolveReceiveLinkAsync(string token, CancellationToken cancellationToken)
    {
        var receiveLink = await shareStore.GetReceiveLinkByTokenAsync(token, cancellationToken);
        if (receiveLink is null)
        {
            return null;
        }

        if (receiveLink.State is ReceiveLinkState.Revoked or ReceiveLinkState.Broken or ReceiveLinkState.Expired or ReceiveLinkState.Exhausted)
        {
            return receiveLink;
        }

        if (receiveLink.ExpiresAtUtc is not null && receiveLink.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            receiveLink = receiveLink with { State = ReceiveLinkState.Expired };
            await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
            return receiveLink;
        }

        var directoryInfo = new DirectoryInfo(receiveLink.TargetDirectoryPath);
        if (!directoryInfo.Exists || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            receiveLink = receiveLink with { State = ReceiveLinkState.Broken, BrokenReason = "The receive folder is no longer available." };
            await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
            return receiveLink;
        }

        if (receiveLink.MaxTotalBytes > 0 && receiveLink.BytesReceived >= receiveLink.MaxTotalBytes)
        {
            receiveLink = receiveLink with { State = ReceiveLinkState.Exhausted };
            await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
            return receiveLink;
        }

        return receiveLink;
    }

    public async Task<ReceiveLinkRecord?> AddReceivedBytesAsync(string receiveLinkId, long bytesReceived, CancellationToken cancellationToken)
    {
        await _receiveLinkBytesGate.WaitAsync(cancellationToken);
        try
        {
            var receiveLink = await shareStore.GetReceiveLinkByIdAsync(receiveLinkId, cancellationToken);
            if (receiveLink is null)
            {
                return null;
            }

            if (receiveLink.State is ReceiveLinkState.Revoked or ReceiveLinkState.Broken or ReceiveLinkState.Expired)
            {
                return receiveLink;
            }

            if (receiveLink.ExpiresAtUtc is not null && receiveLink.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                receiveLink = receiveLink with { State = ReceiveLinkState.Expired };
                await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
                return receiveLink;
            }

            var nextBytesReceived = receiveLink.BytesReceived + bytesReceived;
            if (nextBytesReceived < 0)
            {
                nextBytesReceived = 0;
            }

            if (receiveLink.MaxTotalBytes > 0 && bytesReceived > 0 && nextBytesReceived > receiveLink.MaxTotalBytes)
            {
                return receiveLink with { State = ReceiveLinkState.Exhausted };
            }

            receiveLink = receiveLink with
            {
                BytesReceived = nextBytesReceived,
                State = receiveLink.MaxTotalBytes > 0 && nextBytesReceived >= receiveLink.MaxTotalBytes
                    ? ReceiveLinkState.Exhausted
                    : ReceiveLinkState.Active,
            };

            await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
            return receiveLink;
        }
        finally
        {
            _receiveLinkBytesGate.Release();
        }
    }

    public async Task RevokeShareAsync(string shareId, CancellationToken cancellationToken)
    {
        var share = await shareStore.GetShareByIdAsync(shareId, cancellationToken);
        if (share is not null)
        {
            share = share with { State = ShareState.Revoked };
            await shareStore.UpdateShareAsync(share, cancellationToken);
            await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareRevoked, DateTimeOffset.UtcNow, share), cancellationToken);
            return;
        }

        var receiveLink = await shareStore.GetReceiveLinkByIdAsync(shareId, cancellationToken);
        if (receiveLink is null)
        {
            return;
        }

        receiveLink = receiveLink with { State = ReceiveLinkState.Revoked };
        await shareStore.UpdateReceiveLinkAsync(receiveLink, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareRevoked, DateTimeOffset.UtcNow, ShareListItem.FromReceiveLink(receiveLink)), cancellationToken);
    }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => shareStore.GetSettingsAsync(cancellationToken);

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var currentSettings = await shareStore.GetSettingsAsync(cancellationToken);
        settings = settings with
        {
            PublicTokenLength = NormalizePublicTokenLength(settings.PublicTokenLength),
            FolderZipCompressionLevel = NormalizeFolderZipCompressionLevel(settings.FolderZipCompressionLevel),
            DefaultReceiveExpiryValue = Math.Max(0, settings.DefaultReceiveExpiryValue),
            DefaultReceiveMaxTotalBytes = NormalizeReceiveMaxTotalBytes(settings.DefaultReceiveMaxTotalBytes),
            ReceiveParallelUploadLimit = NormalizeReceiveParallelUploadLimit(settings.ReceiveParallelUploadLimit),
            ReceiveUploadMode = NormalizeReceiveUploadMode(settings.ReceiveUploadMode),
            ReceiveUploadChunkSizingMode = NormalizeReceiveUploadChunkSizingMode(settings.ReceiveUploadChunkSizingMode),
            ReceiveUploadChunkSizeBytes = NormalizeReceiveUploadChunkSizeBytes(settings.ReceiveUploadChunkSizeBytes),
            ReceiveUploadMaxBodySizeBytes = NormalizeReceiveUploadMaxBodySizeBytes(settings.ReceiveUploadMaxBodySizeBytes),
            ReceiveUploadChunkTargetSeconds = NormalizeReceiveUploadChunkTargetSeconds(settings.ReceiveUploadChunkTargetSeconds),
            ReceiveUploadAutoProbeChunkCount = NormalizeReceiveUploadAutoProbeChunkCount(settings.ReceiveUploadAutoProbeChunkCount),
            BrowserManagedDownloadMaxMemoryBytes = NormalizeBrowserManagedDownloadMaxMemoryBytes(settings.BrowserManagedDownloadMaxMemoryBytes),
            BrowserManagedDownloadMaxParallelChunks = NormalizeBrowserManagedDownloadMaxParallelChunks(settings.BrowserManagedDownloadMaxParallelChunks),
            BrowserManagedCompressionMode = NormalizeBrowserManagedCompressionMode(settings.BrowserManagedCompressionMode),
            BrowserDownloadEncryptionPolicy = NormalizeBrowserTransferEncryptionPolicy(settings.BrowserDownloadEncryptionPolicy),
            ReceiveUploadEncryptionPolicy = NormalizeBrowserTransferEncryptionPolicy(settings.ReceiveUploadEncryptionPolicy),
            FolderBrowsePageTitle = NormalizeFolderBrowsePageTitle(settings.FolderBrowsePageTitle),
            ReceivePageTitle = NormalizeReceivePageTitle(settings.ReceivePageTitle),
            HistoryRetentionValue = Math.Max(0, settings.HistoryRetentionValue),
            HistoryItemsPerPage = Math.Max(0, settings.HistoryItemsPerPage),
            SharesItemsPerPage = Math.Max(0, settings.SharesItemsPerPage),
        };
        await shareStore.SaveSettingsAsync(settings, cancellationToken);
        await bootstrapSettingsSnapshotStore.WriteAsync(settings, cancellationToken);
        startupRegistrationService.Apply(settings.StartOnLogin);
        await contextMenuRegistrationService.ApplyAsync(settings, cancellationToken);
        await PruneTransfersAsync(cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.SettingsUpdated, DateTimeOffset.UtcNow, settings), cancellationToken);

        if (RequiresListenerRestart(currentSettings, settings))
        {
            agentLifecycleManager.ScheduleRestart();
        }
    }

    public Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken)
        => shareStore.GetPublishProfilesAsync(cancellationToken);

    public Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken)
        => shareStore.SavePublishProfileAsync(profile, cancellationToken);

    public Task<IReadOnlyList<TransferSnapshot>> GetTransfersAsync(CancellationToken cancellationToken)
    {
        return GetTransfersCoreAsync(cancellationToken);
    }

    public async Task RemoveTransferAsync(string transferId, CancellationToken cancellationToken)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);

        TransferSnapshot? removedTransfer = null;
        var activeTransfer = false;

        lock (_transferLock)
        {
            activeTransfer = _activeTransfers.ContainsKey(transferId);
            if (!activeTransfer)
            {
                var completedTransferIndex = _completedTransfers.FindIndex(entry => entry.Id == transferId);
                if (completedTransferIndex >= 0)
                {
                    removedTransfer = _completedTransfers[completedTransferIndex];
                    _completedTransfers.RemoveAt(completedTransferIndex);
                }
            }
        }

        if (activeTransfer)
        {
            throw new InvalidOperationException("Active transfers cannot be removed from history.");
        }

        if (removedTransfer is null)
        {
            return;
        }

        await shareStore.DeleteTransferAsync(transferId, cancellationToken);
        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(
                RuntimeEventType.TransferRemoved,
                DateTimeOffset.UtcNow,
                new { transferId }),
            cancellationToken);
    }

    public async Task ClearTransferHistoryAsync(CancellationToken cancellationToken)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);

        var clearedAny = false;

        lock (_transferLock)
        {
            if (_completedTransfers.Count > 0)
            {
                _completedTransfers.Clear();
                clearedAny = true;
            }
        }

        if (!clearedAny)
        {
            return;
        }

        await shareStore.ClearCompletedTransfersAsync(cancellationToken);
        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(
                RuntimeEventType.TransferHistoryCleared,
                DateTimeOffset.UtcNow,
                new { }),
            cancellationToken);
    }

    private async Task<IReadOnlyList<TransferSnapshot>> GetTransfersCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);
        lock (_transferLock)
        {
            var active = _activeTransfers.Values.OrderByDescending(entry => entry.StartedAtUtc);
            var completed = _completedTransfers.OrderByDescending(entry => entry.StartedAtUtc);
            return active.Concat(completed).ToList();
        }
    }

    public async Task<TransferSnapshot> StartTransferAsync(string shareId, string token, string fileName, TransferKind transferKind, string? clientSessionId, string? clientFingerprint, string? remoteAddress, long totalBytes, long bytesSent, string? requesterName, CancellationToken cancellationToken)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);
        TransferSnapshot transfer;
        lock (_transferLock)
        {
            var resumedTransferIndex = bytesSent > 0
                ? _completedTransfers.FindLastIndex((entry) => IsMatchingResumableTransfer(
                    entry,
                    shareId,
                    token,
                    fileName,
                    transferKind,
                    clientSessionId,
                    clientFingerprint,
                    totalBytes,
                    bytesSent))
                : -1;

            if (resumedTransferIndex >= 0)
            {
                var resumedTransfer = _completedTransfers[resumedTransferIndex];
                _completedTransfers.RemoveAt(resumedTransferIndex);
                transfer = resumedTransfer with
                {
                    BytesSent = Math.Max(resumedTransfer.BytesSent, bytesSent),
                    ProgressBytes = totalBytes > 0 ? Math.Max(resumedTransfer.ProgressBytes, bytesSent) : resumedTransfer.ProgressBytes,
                    ProgressTotalBytes = totalBytes > 0 ? totalBytes : resumedTransfer.ProgressTotalBytes,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = null,
                    State = TransferState.InProgress,
                    IsActive = true,
                    Succeeded = false,
                    Error = null,
                };
            }
            else
            {
                transfer = new TransferSnapshot
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ShareId = shareId,
                    Token = token,
                    FileName = fileName,
                    TransferKind = transferKind,
                    RequesterName = requesterName,
                    ClientSessionId = clientSessionId,
                    ClientFingerprint = clientFingerprint,
                    RemoteAddress = remoteAddress,
                    BytesSent = bytesSent,
                    TotalBytes = totalBytes,
                    ProgressBytes = totalBytes > 0 ? bytesSent : 0,
                    ProgressTotalBytes = totalBytes,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = null,
                    State = TransferState.InProgress,
                    IsActive = true,
                    Succeeded = false,
                    Error = null,
                };
            }

            _activeTransfers[transfer.Id] = transfer;
        }

        await shareStore.SaveTransferAsync(transfer, cancellationToken);

        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(RuntimeEventType.TransferStarted, DateTimeOffset.UtcNow, transfer),
            cancellationToken);

        return transfer;
    }

    public Task UpdateTransferProgressAsync(string transferId, long bytesSent, CancellationToken cancellationToken, long? progressBytes = null, long? progressTotalBytes = null)
    {
        return UpdateTransferProgressCoreAsync(transferId, bytesSent, cancellationToken, progressBytes, progressTotalBytes);
    }

    private async Task UpdateTransferProgressCoreAsync(string transferId, long bytesSent, CancellationToken cancellationToken, long? progressBytes, long? progressTotalBytes)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);
        TransferSnapshot? updatedTransfer = null;
        lock (_transferLock)
        {
            if (_activeTransfers.TryGetValue(transferId, out var transfer))
            {
                updatedTransfer = transfer with
                {
                    BytesSent = Math.Max(transfer.BytesSent, bytesSent),
                    ProgressBytes = progressBytes.HasValue
                        ? Math.Max(transfer.ProgressBytes, progressBytes.Value)
                        : transfer.ProgressTotalBytes > 0
                            ? Math.Max(transfer.ProgressBytes, bytesSent)
                            : transfer.ProgressBytes,
                    ProgressTotalBytes = progressTotalBytes.HasValue
                        ? Math.Max(transfer.ProgressTotalBytes, progressTotalBytes.Value)
                        : transfer.ProgressTotalBytes,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    State = TransferState.InProgress,
                };
                _activeTransfers[transferId] = updatedTransfer;
            }
        }

        if (updatedTransfer is null)
        {
            return;
        }

        await shareStore.SaveTransferAsync(updatedTransfer, cancellationToken);
        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(RuntimeEventType.TransferProgress, DateTimeOffset.UtcNow, updatedTransfer),
            cancellationToken);
    }

    public async Task MarkTransferCompletedAsync(string transferId, string shareId, string token, string fileName, TransferKind transferKind, string? remoteAddress, long bytesSent, long totalBytes, bool paused, bool succeeded, bool countsTowardUsage, string? usageSessionKey, string? error, string? requesterName, CancellationToken cancellationToken)
    {
        await EnsureTransfersLoadedAsync(cancellationToken);
        TransferSnapshot completedTransfer;
        RuntimeEventType eventType;
        TransferSnapshot? activeTransfer = null;
        lock (_transferLock)
        {
            var startedAt = DateTimeOffset.UtcNow;
            if (_activeTransfers.Remove(transferId, out activeTransfer))
            {
                startedAt = activeTransfer.StartedAtUtc;
                remoteAddress ??= activeTransfer.RemoteAddress;
                requesterName ??= activeTransfer.RequesterName;
                bytesSent = Math.Max(bytesSent, activeTransfer.BytesSent);
            }

            var progressTotalBytes = activeTransfer?.ProgressTotalBytes ?? totalBytes;
            var progressBytes = activeTransfer?.ProgressBytes ?? bytesSent;
            if (succeeded && progressTotalBytes > 0)
            {
                progressBytes = progressTotalBytes;
            }

            completedTransfer = new TransferSnapshot
            {
                Id = transferId,
                ShareId = shareId,
                Token = token,
                FileName = fileName,
                TransferKind = activeTransfer?.TransferKind ?? transferKind,
                RequesterName = requesterName,
                ClientSessionId = activeTransfer?.ClientSessionId,
                ClientFingerprint = activeTransfer?.ClientFingerprint,
                RemoteAddress = remoteAddress,
                BytesSent = bytesSent,
                TotalBytes = totalBytes,
                ProgressBytes = progressBytes,
                ProgressTotalBytes = progressTotalBytes,
                StartedAtUtc = startedAt,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                State = paused ? TransferState.Paused : succeeded ? TransferState.Completed : TransferState.Failed,
                IsActive = false,
                Succeeded = succeeded,
                Error = error,
            };

            _completedTransfers.Add(completedTransfer);
            if (_completedTransfers.Count > 100)
            {
                _completedTransfers.RemoveRange(0, _completedTransfers.Count - 100);
            }
        }

        eventType = paused
            ? RuntimeEventType.TransferPaused
            : succeeded
                ? RuntimeEventType.TransferCompleted
                : RuntimeEventType.TransferFailed;

        await shareStore.SaveTransferAsync(completedTransfer, cancellationToken);

        var share = await shareStore.GetShareByIdAsync(shareId, cancellationToken);
        if (share is not null && countsTowardUsage && succeeded)
        {
            var shouldIncrementUseCount = completedTransfer.TransferKind != TransferKind.FolderFileDownload ||
                string.IsNullOrWhiteSpace(usageSessionKey) ||
                await shareStore.TryAddUsageSessionAsync(shareId, usageSessionKey, cancellationToken);

            if (shouldIncrementUseCount)
            {
                share = share with { UseCount = share.UseCount + 1, LastAccessedAtUtc = DateTimeOffset.UtcNow };
                await shareStore.UpdateShareAsync(share, cancellationToken);
            }
        }

        await PruneTransfersAsync(cancellationToken);

        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(
                eventType,
                DateTimeOffset.UtcNow,
                completedTransfer),
            cancellationToken);
    }

    public Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken)
        => shareStore.GetCloudflaredStateAsync(cancellationToken);

    public async Task<CloudflaredDetectionResult> DetectCloudflaredAsync(CancellationToken cancellationToken)
        => await DetectCloudflaredCoreAsync(publishEvent: true, cancellationToken);

    private async Task<CloudflaredDetectionResult> DetectCloudflaredCoreAsync(bool publishEvent, CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var detection = await cloudflaredSupervisor.DetectAsync(settings.CloudflaredPathOverride, cancellationToken);
        var currentState = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        currentState = currentState with
        {
            ExecutablePath = detection.Path,
            Version = detection.Version,
            Ownership = detection.Ownership,
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
        };

        await shareStore.SaveCloudflaredStateAsync(currentState, cancellationToken);
        if (publishEvent)
        {
            await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.CloudflaredUpdated, DateTimeOffset.UtcNow, currentState), cancellationToken);
        }

        return detection;
    }

    public async Task<string?> EnsureTunnelBaseUrlAsync(PublishMode mode, CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var profiles = await shareStore.GetPublishProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(entry => entry.Mode == mode) ?? new PublishProfile { Mode = mode, Enabled = true };

        return mode switch
        {
            PublishMode.QuickTunnel => await EnsureQuickTunnelUrlAsync(settings, cancellationToken),
            PublishMode.ManagedCloudflare => await EnsureManagedUrlAsync(profile, settings, cancellationToken),
            PublishMode.Manual => await EnsureManualUrlAsync(profile, settings, cancellationToken),
            _ => null,
        };
    }

    public async Task<string?> EnsureInstallerFirstRunTunnelAsync(CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        if (settings.DefaultPublishMode == PublishMode.Manual)
        {
            return null;
        }

        PublishMode[] targetModes = settings.DefaultPublishMode switch
        {
            PublishMode.ManagedCloudflare => [PublishMode.ManagedCloudflare, PublishMode.QuickTunnel],
            PublishMode.QuickTunnel => [PublishMode.QuickTunnel],
            _ => [PublishMode.QuickTunnel],
        };

        foreach (var mode in targetModes)
        {
            var publicBaseUrl = await EnsureTunnelBaseUrlAsync(mode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            {
                return publicBaseUrl;
            }
        }

        return null;
    }

    public async Task<CloudflaredActionResult> InstallCloudflaredAsync(CancellationToken cancellationToken)
    {
        var result = await cloudflaredSupervisor.InstallWithWingetAsync(cancellationToken);
        if (result.Success)
        {
            await DetectCloudflaredAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CloudflaredActionResult> UpdateCloudflaredAsync(CancellationToken cancellationToken)
    {
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(state.ExecutablePath))
        {
            return new CloudflaredActionResult(false, "cloudflared is not available.");
        }

        var result = await cloudflaredSupervisor.UpdateAsync(state.ExecutablePath, state.Ownership, cancellationToken);
        if (result.Success)
        {
            await DetectCloudflaredAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CloudflaredActionResult> StartManagedTunnelLoginAsync(CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return new CloudflaredActionResult(false, "cloudflared is not available.");
        }

        return await cloudflaredSupervisor.LaunchLoginAsync(detection.Path, cancellationToken);
    }

    public async Task<CloudflaredActionResult> LogoutCloudflareAsync(CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        if (File.Exists(certPath))
        {
            File.Delete(certPath);
        }

        await Task.CompletedTask;
        return new CloudflaredActionResult(true, "Logged out locally. Existing managed tunnel settings were kept, but you must log in again to manage them.");
    }

    public async Task<CloudflaredDashboardStatus> GetCloudflaredDashboardStatusAsync(CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredCoreAsync(publishEvent: false, cancellationToken);
        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        var latestVersion = await TryGetLatestCloudflaredVersionAsync(cancellationToken);
        var installedVersion = ExtractCloudflaredVersion(detection.Version);
        var updateAvailable = installedVersion is not null &&
                              latestVersion is not null &&
                              CompareVersions(installedVersion, latestVersion) < 0;

        return new CloudflaredDashboardStatus(
            detection.Found,
            detection.Path,
            installedVersion,
            latestVersion,
            updateAvailable,
            detection.Ownership,
            managedStatus.LoggedIn,
            managedStatus.Message);
    }

    public async Task<CloudflareManagedStatus> GetManagedCloudflareStatusAsync(CancellationToken cancellationToken)
    {
        var profile = (await shareStore.GetPublishProfilesAsync(cancellationToken))
            .FirstOrDefault(entry => entry.Mode == PublishMode.ManagedCloudflare);

        var certToken = await TryReadCloudflareLoginTokenAsync(cancellationToken);
        if (certToken is null)
        {
            return new CloudflareManagedStatus(
                false,
                "Not logged in. Run Start Cloudflare Login to authorize a zone.",
                ConfiguredHostname: profile?.CloudflareHostname,
                ConfiguredTunnelName: profile?.CloudflareTunnelName);
        }

        var zoneDetails = await TryResolveZoneDetailsAsync(certToken, cancellationToken);
        var domains = zoneDetails?.Name is null
            ? Array.Empty<CloudflareDomainOption>()
            : [new CloudflareDomainOption(certToken.ZoneId, zoneDetails.Name)];

        return new CloudflareManagedStatus(
            true,
            zoneDetails?.Name is null ? "Logged in, but the zone name could not be resolved." : $"Logged in for zone {zoneDetails.Name}.",
            certToken.AccountId,
            certToken.ZoneId,
            zoneDetails?.Name,
            zoneDetails?.PlanLegacyId,
            zoneDetails?.PlanName,
            domains,
            profile?.CloudflareHostname,
            profile?.CloudflareTunnelName);
    }

    public async Task<CloudflareManagedAvailability> CheckManagedTunnelAvailabilityAsync(CheckManagedTunnelRequest request, CancellationToken cancellationToken)
    {
        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        var selectedDomain = request.Domain.Trim().Trim('.');
        var subdomain = SanitizeSubdomain(request.Subdomain);
        var hostname = $"{subdomain}.{selectedDomain}";
        var tunnelName = $"ifs-{subdomain}-{selectedDomain.Replace('.', '-')}".ToLowerInvariant();

        if (!managedStatus.LoggedIn)
        {
            return new CloudflareManagedAvailability(
                selectedDomain,
                subdomain,
                hostname,
                tunnelName,
                false,
                false,
                managedStatus.Message);
        }

        var availableDomains = managedStatus.Domains ?? Array.Empty<CloudflareDomainOption>();
        if (availableDomains.Count > 0 && !availableDomains.Any(domain => string.Equals(domain.Name, selectedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return new CloudflareManagedAvailability(
                selectedDomain,
                subdomain,
                hostname,
                tunnelName,
                false,
                false,
                $"The current login is not authorized for {selectedDomain}.");
        }

        var detection = await DetectCloudflaredAsync(cancellationToken);
        var tunnelExists = detection.Found && !string.IsNullOrWhiteSpace(detection.Path)
            ? await cloudflaredSupervisor.TunnelExistsAsync(detection.Path, tunnelName, cancellationToken)
            : false;
        var hostnameExists = managedStatus.ZoneId is not null &&
                             await HostnameRecordExistsAsync(managedStatus.ZoneId, hostname, cancellationToken);

        var message = $"Tunnel {(tunnelExists ? "exists" : "does not exist")}; hostname {(hostnameExists ? "already exists" : "does not exist")} for {hostname}.";

        return new CloudflareManagedAvailability(
            selectedDomain,
            subdomain,
            hostname,
            tunnelName,
            tunnelExists,
            hostnameExists,
            message);
    }

    public async Task<ManagedTunnelProvisionResult> CreateManagedTunnelAsync(CreateManagedTunnelRequest request, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return new ManagedTunnelProvisionResult(false, "cloudflared is not available.");
        }

        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        if (!managedStatus.LoggedIn)
        {
            return new ManagedTunnelProvisionResult(false, managedStatus.Message);
        }

        var selectedDomain = request.Domain.Trim().Trim('.');
        var availableDomains = managedStatus.Domains ?? Array.Empty<CloudflareDomainOption>();
        if (availableDomains.Count > 0 && !availableDomains.Any(domain => string.Equals(domain.Name, selectedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return new ManagedTunnelProvisionResult(false, $"The current login is not authorized for {selectedDomain}.");
        }

        var subdomain = SanitizeSubdomain(request.Subdomain);
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return new ManagedTunnelProvisionResult(false, "A valid subdomain is required.");
        }

        var hostname = $"{subdomain}.{selectedDomain}";
        var tunnelName = $"ifs-{subdomain}-{selectedDomain.Replace('.', '-')}".ToLowerInvariant();
        var existingProfile = (await shareStore.GetPublishProfilesAsync(cancellationToken))
            .FirstOrDefault(entry => entry.Mode == PublishMode.ManagedCloudflare);

        if (existingProfile is not null &&
            !string.IsNullOrWhiteSpace(existingProfile.CloudflareTunnelName) &&
            !string.Equals(existingProfile.CloudflareTunnelName, tunnelName, StringComparison.OrdinalIgnoreCase))
        {
            var deleteResult = await cloudflaredSupervisor.DeleteTunnelAsync(detection.Path, existingProfile.CloudflareTunnelName, cancellationToken);
            if (!deleteResult.Success)
            {
                return new ManagedTunnelProvisionResult(false, $"Failed to replace the previous managed tunnel. {deleteResult.Message}");
            }
        }

        var provisionResult = await cloudflaredSupervisor.ProvisionManagedTunnelAsync(detection.Path, tunnelName, hostname, cancellationToken);
        if (!provisionResult.Success)
        {
            return provisionResult;
        }

        await shareStore.SavePublishProfileAsync(new PublishProfile
        {
            Mode = PublishMode.ManagedCloudflare,
            Enabled = true,
            BaseUrl = $"https://{hostname}",
            PublicPort = Defaults.PublicPort,
            BindAddress = "127.0.0.1",
            CloudflareHostname = hostname,
            CloudflareTunnelName = tunnelName,
            // Managed tunnel tokens remain locally persisted for automatic relaunch.
            // This is an accepted local-exposure tradeoff until credential hardening lands.
            CloudflareToken = await FetchManagedTunnelTokenAsync(detection.Path, tunnelName, cancellationToken),
        }, cancellationToken);

        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.SettingsUpdated, DateTimeOffset.UtcNow, hostname), cancellationToken);
        return provisionResult with
        {
            Message = $"Managed tunnel ready for https://{hostname}",
        };
    }

    public async Task<RuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        return new RuntimeSnapshot
        {
            Shares = await ListShareItemsAsync(cancellationToken),
            Transfers = await GetTransfersCoreAsync(cancellationToken),
            Settings = await shareStore.GetSettingsAsync(cancellationToken),
            Cloudflared = await shareStore.GetCloudflaredStateAsync(cancellationToken),
        };
    }

    private async Task EnsureTransfersLoadedAsync(CancellationToken cancellationToken)
    {
        if (_transfersLoaded)
        {
            return;
        }

        await _transferLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (_transfersLoaded)
            {
                return;
            }

            var transfers = await shareStore.ListTransfersAsync(cancellationToken);
            var staleTransfers = new List<TransferSnapshot>();

            lock (_transferLock)
            {
                _activeTransfers.Clear();
                _completedTransfers.Clear();

                foreach (var transfer in transfers)
                {
                    if (transfer.IsActive || transfer.State == TransferState.InProgress)
                    {
                        var staleTransfer = transfer with
                        {
                            IsActive = false,
                            State = TransferState.Paused,
                            CompletedAtUtc = transfer.CompletedAtUtc ?? transfer.LastUpdatedAtUtc,
                            Error = null,
                            Succeeded = false,
                        };
                        _completedTransfers.Add(staleTransfer);
                        staleTransfers.Add(staleTransfer);
                        continue;
                    }

                    _completedTransfers.Add(transfer);
                }

                _transfersLoaded = true;
            }

            foreach (var staleTransfer in staleTransfers)
            {
                await shareStore.SaveTransferAsync(staleTransfer, cancellationToken);
            }

            await PruneTransfersAsync(cancellationToken);
        }
        finally
        {
            _transferLoadGate.Release();
        }
    }

    private async Task PruneTransfersAsync(CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var cutoff = ResolveHistoryRetentionCutoff(settings);
        if (cutoff is null)
        {
            return;
        }

        lock (_transferLock)
        {
            _completedTransfers.RemoveAll(transfer => transfer.CompletedAtUtc is not null && transfer.CompletedAtUtc < cutoff.Value);
        }

        await shareStore.PruneCompletedTransfersAsync(cutoff.Value, cancellationToken);
    }

    private static bool IsMatchingResumableTransfer(
        TransferSnapshot entry,
        string shareId,
        string token,
        string fileName,
        TransferKind transferKind,
        string? clientSessionId,
        string? clientFingerprint,
        long totalBytes,
        long requestedFrom)
    {
        if (entry.State is not TransferState.Paused and not TransferState.Failed)
        {
            return false;
        }

        if (entry.ShareId != shareId ||
            entry.Token != token ||
            entry.TotalBytes != totalBytes ||
            entry.TransferKind != transferKind ||
            !string.Equals(entry.FileName, fileName, StringComparison.Ordinal))
        {
            return false;
        }

        if (entry.LastUpdatedAtUtc < DateTimeOffset.UtcNow.Subtract(TransferResumeWindow))
        {
            return false;
        }

        if (requestedFrom <= 0 || requestedFrom > entry.BytesSent)
        {
            return false;
        }

        if (entry.BytesSent - requestedFrom > TransferResumeToleranceBytes)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(clientSessionId))
        {
            return string.Equals(entry.ClientSessionId, clientSessionId, StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(entry.ClientSessionId) &&
               !string.IsNullOrWhiteSpace(clientFingerprint) &&
               string.Equals(entry.ClientFingerprint, clientFingerprint, StringComparison.Ordinal);
    }

    private async Task<string?> EnsureQuickTunnelUrlAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return null;
        }

        var url = await cloudflaredSupervisor.EnsureQuickTunnelAsync(detection.Path, settings.ManualPublicPort, cancellationToken);
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        await shareStore.SaveCloudflaredStateAsync(state with
        {
            QuickTunnelUrl = url,
            ActiveMode = PublishMode.QuickTunnel,
        }, cancellationToken);
        return url;
    }

    private async Task<string?> EnsureManagedUrlAsync(PublishProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return null;
        }

        await cloudflaredSupervisor.StartManagedTunnelAsync(detection.Path, profile, settings.ManualPublicPort, cancellationToken);
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        await shareStore.SaveCloudflaredStateAsync(state with
        {
            ManagedTunnelRunning = cloudflaredSupervisor.ManagedTunnelRunning,
            ActiveMode = PublishMode.ManagedCloudflare,
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return profile.BaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(profile.CloudflareHostname))
        {
            return $"https://{profile.CloudflareHostname.TrimStart('/')}";
        }

        return null;
    }

    private async Task<string> EnsureManualUrlAsync(PublishProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.ManualBaseUrl))
        {
            return settings.ManualBaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return profile.BaseUrl.TrimEnd('/');
        }

        var publicIp = await externalAddressResolver.TryGetPublicIpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(publicIp))
        {
            return $"http://{publicIp}:{settings.ManualPublicPort}";
        }

        var localIp = await externalAddressResolver.TryGetLocalIpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(localIp))
        {
            return $"http://{localIp}:{settings.ManualPublicPort}";
        }

        return $"http://127.0.0.1:{settings.ManualPublicPort}";
    }

    private static string GetMissingPublishModeMessage(PublishMode mode)
    {
        return mode switch
        {
            PublishMode.QuickTunnel => "Quick Tunnel mode requires cloudflared. Install it from Diagnostics or set an explicit cloudflared path in Settings.",
            PublishMode.ManagedCloudflare => "Managed Cloudflare mode requires cloudflared plus a configured managed tunnel profile.",
            PublishMode.Manual => "Manual mode could not resolve a usable base URL.",
            _ => "No public URL is available for the selected publish mode.",
        };
    }

    private static string NormalizeBaseUrl(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    private static bool IsShareCompatibleWithRequest(ShareRecord share, CreateShareRequest request, AppSettings settings)
    {
        if (share.ShareKind != request.ShareKind)
        {
            return false;
        }

        if (share.ShareKind == ShareKind.Folder)
        {
            var primaryEntryPoint = request.PrimaryFolderEntryPoint ?? FolderShareEntryPoint.Browse;
            var (canBrowse, canDownloadZip) = ResolveFolderCapabilities(request, settings);
            if (share.CanBrowseFolderContents != canBrowse ||
                share.CanDownloadFolderAsZip != canDownloadZip ||
                share.PrimaryFolderEntryPoint != primaryEntryPoint)
            {
                return false;
            }
        }

        if (share.MaxUses != (request.MaxUses ?? settings.DefaultMaxUses))
        {
            return false;
        }

        if (request.ExpiresAtUtc is not null)
        {
            return share.ExpiresAtUtc == request.ExpiresAtUtc;
        }

        if (settings.DefaultExpiryValue <= 0)
        {
            return share.ExpiresAtUtc is null;
        }

        if (share.ExpiresAtUtc is null)
        {
            return false;
        }

        var expectedLifetime = settings.DefaultExpiryUnit switch
        {
            ExpiryUnit.Minutes => TimeSpan.FromMinutes(settings.DefaultExpiryValue),
            ExpiryUnit.Days => TimeSpan.FromDays(settings.DefaultExpiryValue),
            _ => TimeSpan.FromHours(settings.DefaultExpiryValue),
        };

        var actualLifetime = share.ExpiresAtUtc.Value - share.CreatedAtUtc;
        return (actualLifetime - expectedLifetime).Duration() <= ExpiryComparisonTolerance;
    }

    private static DateTimeOffset? ResolveDefaultExpiry(AppSettings settings)
    {
        if (settings.DefaultExpiryValue <= 0)
        {
            return null;
        }

        return settings.DefaultExpiryUnit switch
        {
            ExpiryUnit.Minutes => DateTimeOffset.UtcNow.AddMinutes(settings.DefaultExpiryValue),
            ExpiryUnit.Days => DateTimeOffset.UtcNow.AddDays(settings.DefaultExpiryValue),
            _ => DateTimeOffset.UtcNow.AddHours(settings.DefaultExpiryValue),
        };
    }

    private static DateTimeOffset? ResolveDefaultReceiveExpiry(AppSettings settings)
    {
        if (settings.DefaultReceiveExpiryValue <= 0)
        {
            return null;
        }

        return settings.DefaultReceiveExpiryUnit switch
        {
            ExpiryUnit.Minutes => DateTimeOffset.UtcNow.AddMinutes(settings.DefaultReceiveExpiryValue),
            ExpiryUnit.Days => DateTimeOffset.UtcNow.AddDays(settings.DefaultReceiveExpiryValue),
            _ => DateTimeOffset.UtcNow.AddHours(settings.DefaultReceiveExpiryValue),
        };
    }

    private async Task<string> GenerateUniqueShareTokenAsync(int tokenLength, CancellationToken cancellationToken)
    {
        const int maxAttempts = 32;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var token = ShareTokenGenerator.Generate(tokenLength);
            if (await shareStore.GetShareByTokenAsync(token, cancellationToken) is null)
            {
                return token;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique public share token.");
    }

    private async Task<string> GenerateUniqueReceiveLinkTokenAsync(int tokenLength, CancellationToken cancellationToken)
    {
        const int maxAttempts = 32;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var token = ShareTokenGenerator.Generate(tokenLength);
            if (await shareStore.GetReceiveLinkByTokenAsync(token, cancellationToken) is null)
            {
                return token;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique receive link token.");
    }

    private static int NormalizePublicTokenLength(int tokenLength)
    {
        return Math.Clamp(tokenLength, ShareTokenGenerator.MinLength, ShareTokenGenerator.MaxLength);
    }

    private static long NormalizeReceiveMaxTotalBytes(long maxTotalBytes)
    {
        return Math.Max(0, maxTotalBytes);
    }

    private static int NormalizeReceiveParallelUploadLimit(int uploadLimit)
    {
        return Math.Max(0, uploadLimit);
    }

    private static ReceiveUploadMode NormalizeReceiveUploadMode(ReceiveUploadMode uploadMode)
    {
        if (uploadMode == ReceiveUploadMode.CompressedStream)
        {
            return ReceiveUploadMode.Auto;
        }

        return Enum.IsDefined(uploadMode)
            ? uploadMode
            : Defaults.DefaultReceiveUploadMode;
    }

    private static ReceiveUploadChunkSizingMode NormalizeReceiveUploadChunkSizingMode(ReceiveUploadChunkSizingMode uploadChunkSizingMode)
    {
        return Enum.IsDefined(uploadChunkSizingMode)
            ? uploadChunkSizingMode
            : Defaults.DefaultReceiveUploadChunkSizingMode;
    }

    private static long NormalizeReceiveUploadChunkSizeBytes(long chunkSizeBytes)
    {
        return Math.Max(Defaults.MinimumReceiveUploadChunkSizeBytes, chunkSizeBytes);
    }

    private static long NormalizeReceiveUploadMaxBodySizeBytes(long maxBodySizeBytes)
    {
        return Math.Max(Defaults.MinimumReceiveUploadChunkSizeBytes, maxBodySizeBytes);
    }

    private static int NormalizeReceiveUploadChunkTargetSeconds(int chunkTargetSeconds)
    {
        return Math.Max(
            Defaults.MinimumReceiveUploadChunkTargetSeconds,
            chunkTargetSeconds <= 0 ? Defaults.DefaultReceiveUploadChunkTargetSeconds : chunkTargetSeconds);
    }

    private static int NormalizeReceiveUploadAutoProbeChunkCount(int chunkCount)
    {
        return Math.Max(
            Defaults.MinimumReceiveUploadAutoProbeChunkCount,
            chunkCount <= 0 ? Defaults.DefaultReceiveUploadAutoProbeChunkCount : chunkCount);
    }

    private static long NormalizeBrowserManagedDownloadMaxMemoryBytes(long maxMemoryBytes)
    {
        return Math.Max(Defaults.MinimumReceiveUploadChunkSizeBytes, maxMemoryBytes <= 0 ? Defaults.DefaultBrowserManagedDownloadMaxMemoryBytes : maxMemoryBytes);
    }

    private static int NormalizeBrowserManagedDownloadMaxParallelChunks(int maxParallelChunks)
    {
        return Math.Max(
            Defaults.MinimumBrowserManagedDownloadMaxParallelChunks,
            maxParallelChunks <= 0 ? Defaults.DefaultBrowserManagedDownloadMaxParallelChunks : maxParallelChunks);
    }

    private static BrowserManagedCompressionMode NormalizeBrowserManagedCompressionMode(BrowserManagedCompressionMode compressionMode)
    {
        return Enum.IsDefined(compressionMode) ? compressionMode : BrowserManagedCompressionMode.Auto;
    }

    private static BrowserTransferEncryptionPolicy NormalizeBrowserTransferEncryptionPolicy(BrowserTransferEncryptionPolicy encryptionPolicy)
    {
        return Enum.IsDefined(encryptionPolicy) ? encryptionPolicy : BrowserTransferEncryptionPolicy.HttpOnly;
    }

    private static string NormalizeReceivePageTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title)
            ? Defaults.CreateDefaultReceivePageTitle()
            : title.Trim();
    }

    private static string NormalizeFolderBrowsePageTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title)
            ? Defaults.CreateDefaultFolderBrowsePageTitle()
            : title.Trim();
    }

    private static bool RequiresListenerRestart(AppSettings currentSettings, AppSettings nextSettings)
    {
        return currentSettings.LocalApiPort != nextSettings.LocalApiPort ||
               currentSettings.ManualPublicPort != nextSettings.ManualPublicPort ||
               !string.Equals(currentSettings.ManualBindAddress, nextSettings.ManualBindAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static FolderZipCompressionLevel NormalizeFolderZipCompressionLevel(FolderZipCompressionLevel level)
    {
        return level is FolderZipCompressionLevel.Fastest or FolderZipCompressionLevel.NoCompression or FolderZipCompressionLevel.SmallestSize
            ? level
            : FolderZipCompressionLevel.Optimal;
    }

    private static ShareRecord BuildFileShare(FileInfo fileInfo, CreateShareRequest request, AppSettings settings, PublishMode mode, string publicBaseUrl, string token)
    {
        return new ShareRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = token,
            FilePath = fileInfo.FullName,
            FileName = fileInfo.Name,
            Slug = settings.FriendlyUrlsEnabled ? FileNameSlug.Create(fileInfo.Name) : null,
            PublicBaseUrl = publicBaseUrl,
            FileSize = fileInfo.Length,
            FileModifiedAtUtc = fileInfo.LastWriteTimeUtc,
            ShareKind = ShareKind.File,
            CanBrowseFolderContents = false,
            CanDownloadFolderAsZip = false,
            PrimaryFolderEntryPoint = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc ?? ResolveDefaultExpiry(settings),
            MaxUses = request.MaxUses ?? settings.DefaultMaxUses,
            UseCount = 0,
            PublishMode = mode,
            State = ShareState.Active,
        };
    }

    private static ShareRecord BuildFolderShare(DirectoryInfo directoryInfo, CreateShareRequest request, AppSettings settings, PublishMode mode, string publicBaseUrl, string token)
    {
        var primaryEntryPoint = request.PrimaryFolderEntryPoint ?? FolderShareEntryPoint.Browse;
        var (canBrowse, canDownloadZip) = ResolveFolderCapabilities(request, settings);

        return new ShareRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = token,
            FilePath = directoryInfo.FullName,
            FileName = directoryInfo.Name,
            Slug = FileNameSlug.Create(directoryInfo.Name, stripExtension: false) ?? "folder",
            PublicBaseUrl = publicBaseUrl,
            FileSize = 0,
            FileModifiedAtUtc = directoryInfo.LastWriteTimeUtc,
            ShareKind = ShareKind.Folder,
            CanBrowseFolderContents = canBrowse,
            CanDownloadFolderAsZip = canDownloadZip,
            PrimaryFolderEntryPoint = primaryEntryPoint,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc ?? ResolveDefaultExpiry(settings),
            MaxUses = request.MaxUses ?? settings.DefaultMaxUses,
            UseCount = 0,
            PublishMode = mode,
            State = ShareState.Active,
        };
    }

    private static (bool CanBrowse, bool CanDownloadZip) ResolveFolderCapabilities(CreateShareRequest request, AppSettings settings)
    {
        if (request.ShareKind != ShareKind.Folder)
        {
            return (false, false);
        }

        var primaryEntryPoint = request.PrimaryFolderEntryPoint ?? FolderShareEntryPoint.Browse;
        if (settings.FolderShareCapabilityPolicy == FolderShareCapabilityPolicy.AllowBoth)
        {
            return (true, true);
        }

        return primaryEntryPoint == FolderShareEntryPoint.Browse
            ? (true, false)
            : (false, true);
    }

    private static DateTimeOffset? ResolveHistoryRetentionCutoff(AppSettings settings)
    {
        if (settings.HistoryRetentionValue <= 0)
        {
            return null;
        }

        return settings.HistoryRetentionUnit switch
        {
            HistoryRetentionUnit.Minutes => DateTimeOffset.UtcNow.AddMinutes(-settings.HistoryRetentionValue),
            HistoryRetentionUnit.Hours => DateTimeOffset.UtcNow.AddHours(-settings.HistoryRetentionValue),
            HistoryRetentionUnit.Months => DateTimeOffset.UtcNow.AddMonths(-settings.HistoryRetentionValue),
            HistoryRetentionUnit.Years => DateTimeOffset.UtcNow.AddYears(-settings.HistoryRetentionValue),
            _ => DateTimeOffset.UtcNow.AddDays(-settings.HistoryRetentionValue),
        };
    }

    private async Task<CloudflareLoginToken?> TryReadCloudflareLoginTokenAsync(CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        if (!File.Exists(certPath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(certPath, cancellationToken);
        var payload = string.Concat(lines.Where(line => !line.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) && !line.Contains("END", StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonSerializer.Deserialize<CloudflareLoginToken>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private async Task<CloudflareZoneDetails?> TryResolveZoneDetailsAsync(CloudflareLoginToken token, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{token.ZoneId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.ApiToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var fallbackName = await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
                return fallbackName is null ? null : new CloudflareZoneDetails(fallbackName, null, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CloudflareZoneResponse>(stream, JsonOptions, cancellationToken);
            if (payload?.Result?.Name is { Length: > 0 } zoneName)
            {
                return new CloudflareZoneDetails(
                    zoneName,
                    payload.Result.Plan?.LegacyId,
                    payload.Result.Plan?.Name);
            }

            var fallbackZoneName = await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
            return fallbackZoneName is null ? null : new CloudflareZoneDetails(fallbackZoneName, null, null);
        }
        catch
        {
            var fallbackZoneName = await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
            return fallbackZoneName is null ? null : new CloudflareZoneDetails(fallbackZoneName, null, null);
        }
    }

    private async Task<string?> FetchManagedTunnelTokenAsync(string executablePath, string tunnelName, CancellationToken cancellationToken)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"tunnel token {tunnelName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private async Task<string?> TryGetLatestCloudflaredVersionAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/cloudflare/cloudflared/releases/latest");
        request.Headers.UserAgent.ParseAdd("InstantFileShare/1.0");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, cancellationToken);
            return ExtractCloudflaredVersion(payload?.TagName);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeSubdomain(string value)
    {
        var candidate = value.Trim().Trim('.').ToLowerInvariant();
        var normalized = new string(candidate.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "share" : normalized;
    }

    private static string? ExtractCloudflaredVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\d+\.\d+\.\d+");
        return match.Success ? match.Value : null;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(value => int.TryParse(value, out var parsed) ? parsed : 0).ToArray();
        var rightParts = right.Split('.').Select(value => int.TryParse(value, out var parsed) ? parsed : 0).ToArray();
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Length ? leftParts[index] : 0;
            var rightValue = index < rightParts.Length ? rightParts[index] : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private static async Task<string?> TryResolveZoneNameWithPowerShellAsync(CloudflareLoginToken token, CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        var escapedCertPath = certPath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$certPath = '" + escapedCertPath + "'; " +
            "$content = Get-Content $certPath | Where-Object { $_ -notmatch 'BEGIN|END' }; " +
            "$json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($content -join ''))); " +
            "$obj = $json | ConvertFrom-Json; " +
            "$headers = @{ Authorization = \"Bearer $($obj.apiToken)\" }; " +
            "$zone = Invoke-RestMethod -Headers $headers -Uri \"https://api.cloudflare.com/client/v4/zones/$($obj.zoneID)\" -Method Get; " +
            "$zone.result.name";

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return null;
        }

        var zoneName = output.Trim();
        return string.IsNullOrWhiteSpace(zoneName) ? null : zoneName;
    }

    private async Task<bool> HostnameRecordExistsAsync(string zoneId, string hostname, CancellationToken cancellationToken)
    {
        var certToken = await TryReadCloudflareLoginTokenAsync(cancellationToken);
        if (certToken is null)
        {
            return false;
        }

        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?name={Uri.EscapeDataString(hostname)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", certToken.ApiToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CloudflareDnsListResponse>(stream, JsonOptions, cancellationToken);
            return payload?.Result?.Any(record => string.Equals(record.Name, hostname, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CloudflareLoginToken(string ZoneId, string AccountId, string ApiToken);

    private sealed record CloudflareZoneDetails(string Name, string? PlanLegacyId, string? PlanName);

    private sealed record CloudflareZoneResponse(CloudflareZoneResult? Result);

    private sealed record CloudflareZoneResult(string Id, string Name, CloudflareZonePlan? Plan);

    private sealed record CloudflareZonePlan([property: JsonPropertyName("legacy_id")] string? LegacyId, string? Name);

    private sealed record CloudflareDnsListResponse(IReadOnlyList<CloudflareDnsRecord>? Result);

    private sealed record CloudflareDnsRecord(string Id, string Name, string Type, string Content);

    private sealed record GitHubReleaseResponse(string TagName);
}
