namespace InstantFileShare.Core;

public interface IShareStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ShareRecord> AddShareAsync(ShareRecord share, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken);
    Task<ShareRecord?> GetShareByIdAsync(string shareId, CancellationToken cancellationToken);
    Task<ShareRecord?> GetShareByTokenAsync(string token, CancellationToken cancellationToken);
    Task UpdateShareAsync(ShareRecord share, CancellationToken cancellationToken);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken);
    Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken);
    Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken);
    Task SaveCloudflaredStateAsync(CloudflaredState state, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferSnapshot>> ListTransfersAsync(CancellationToken cancellationToken);
    Task SaveTransferAsync(TransferSnapshot transfer, CancellationToken cancellationToken);
    Task DeleteTransferAsync(string transferId, CancellationToken cancellationToken);
    Task ClearCompletedTransfersAsync(CancellationToken cancellationToken);
    Task PruneCompletedTransfersAsync(DateTimeOffset completedBeforeUtc, CancellationToken cancellationToken);
    Task<bool> TryAddUsageSessionAsync(string shareId, string sessionKey, CancellationToken cancellationToken);
}

public interface IRuntimeEventStream
{
    Task PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeEvent> ListenAsync(CancellationToken cancellationToken);
}

public interface IShareCoordinator
{
    Task<(ShareRecord Share, string Url)> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken);
    Task<ShareRecord?> ResolveDownloadAsync(string token, CancellationToken cancellationToken);
    Task RevokeShareAsync(string shareId, CancellationToken cancellationToken);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken);
    Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferSnapshot>> GetTransfersAsync(CancellationToken cancellationToken);
    Task RemoveTransferAsync(string transferId, CancellationToken cancellationToken);
    Task ClearTransferHistoryAsync(CancellationToken cancellationToken);
    Task<TransferSnapshot> StartTransferAsync(string shareId, string token, string fileName, TransferKind transferKind, string? clientSessionId, string? clientFingerprint, string? remoteAddress, long totalBytes, long bytesSent, string? requesterName, CancellationToken cancellationToken);
    Task UpdateTransferProgressAsync(string transferId, long bytesSent, CancellationToken cancellationToken);
    Task MarkTransferCompletedAsync(string transferId, string shareId, string token, string fileName, TransferKind transferKind, string? remoteAddress, long bytesSent, long totalBytes, bool paused, bool succeeded, bool countsTowardUsage, string? usageSessionKey, string? error, string? requesterName, CancellationToken cancellationToken);
    Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken);
    Task<CloudflaredDetectionResult> DetectCloudflaredAsync(CancellationToken cancellationToken);
    Task<string?> EnsureTunnelBaseUrlAsync(PublishMode mode, CancellationToken cancellationToken);
    Task<string?> EnsureInstallerFirstRunTunnelAsync(CancellationToken cancellationToken);
    Task<CloudflaredActionResult> InstallCloudflaredAsync(CancellationToken cancellationToken);
    Task<CloudflaredActionResult> UpdateCloudflaredAsync(CancellationToken cancellationToken);
    Task<CloudflaredActionResult> StartManagedTunnelLoginAsync(CancellationToken cancellationToken);
    Task<CloudflaredActionResult> LogoutCloudflareAsync(CancellationToken cancellationToken);
    Task<CloudflaredDashboardStatus> GetCloudflaredDashboardStatusAsync(CancellationToken cancellationToken);
    Task<CloudflareManagedStatus> GetManagedCloudflareStatusAsync(CancellationToken cancellationToken);
    Task<CloudflareManagedAvailability> CheckManagedTunnelAvailabilityAsync(CheckManagedTunnelRequest request, CancellationToken cancellationToken);
    Task<ManagedTunnelProvisionResult> CreateManagedTunnelAsync(CreateManagedTunnelRequest request, CancellationToken cancellationToken);
    Task<RuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken);
}

public interface IPipeCommandHandler
{
    Task<PipeCommandResult> HandleAsync(PipeCommand command, CancellationToken cancellationToken);
}

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public interface INotificationService
{
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
}

public interface IUiLauncher
{
    Task OpenDashboardAsync(CancellationToken cancellationToken);
}

public interface IStartupRegistrationService
{
    void Apply(bool enabled);
}

public interface IContextMenuRegistrationService
{
    Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IKeepAwakeService
{
    void SetActiveTransfers(bool active);
}
