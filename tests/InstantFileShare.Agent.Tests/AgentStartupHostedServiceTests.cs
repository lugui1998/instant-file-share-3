using InstantFileShare.Agent;
using InstantFileShare.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstantFileShare.Agent.Tests;

public sealed class AgentStartupHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ReconcilesPersistedShares_WhenStartupTasksRun()
    {
        var coordinator = new RecordingShareCoordinator();
        var service = new AgentStartupHostedService(
            new AgentApplicationOptions
            {
                RunStartupTasks = true,
                RepositoryRoot = Environment.CurrentDirectory,
            },
            new AgentStartupOptions(false, false, null),
            new InitialAppSettingsSnapshot(new AppSettings
            {
                StartOnLogin = false,
                OpenDashboardOnStart = false,
            }),
            new RecordingBootstrapSettingsSnapshotStore(),
            new RecordingStartupRegistrationService(),
            new RecordingContextMenuRegistrationService(),
            new NotificationService(),
            new DashboardLauncher(NullLogger<DashboardLauncher>.Instance),
            coordinator,
            new RecordingHostApplicationLifetime());

        await service.StartAsync(CancellationToken.None);

        Assert.True(coordinator.ReconcilePersistedSharesCalled);
    }

    private sealed class RecordingShareCoordinator : IShareCoordinator
    {
        public bool ReconcilePersistedSharesCalled { get; private set; }

        public Task ReconcilePersistedSharesAsync(CancellationToken cancellationToken)
        {
            ReconcilePersistedSharesCalled = true;
            return Task.CompletedTask;
        }

        public Task<(ShareRecord Share, string Url)> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(ReceiveLinkRecord ReceiveLink, string Url)> CreateReceiveLinkAsync(CreateReceiveLinkRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShareListItem>> ListShareItemsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ShareRecord?> ResolveDownloadAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReceiveLinkRecord?> ResolveReceiveLinkAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReceiveLinkRecord?> AddReceivedBytesAsync(string receiveLinkId, long bytesReceived, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeShareAsync(string shareId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ShowShareInExplorerAsync(string shareId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TransferSnapshot>> GetTransfersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveTransferAsync(string transferId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearTransferHistoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransferSnapshot> StartTransferAsync(string shareId, string token, string fileName, TransferKind transferKind, string? clientSessionId, string? clientFingerprint, string? remoteAddress, long totalBytes, long bytesSent, string? requesterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateTransferProgressAsync(string transferId, long bytesSent, CancellationToken cancellationToken, long? progressBytes = null, long? progressTotalBytes = null) => throw new NotSupportedException();
        public Task MarkTransferCompletedAsync(string transferId, string shareId, string token, string fileName, TransferKind transferKind, string? remoteAddress, long bytesSent, long totalBytes, bool paused, bool succeeded, bool countsTowardUsage, string? usageSessionKey, string? error, string? requesterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredDetectionResult> DetectCloudflaredAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> EnsureTunnelBaseUrlAsync(PublishMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> EnsureInstallerFirstRunTunnelAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredActionResult> InstallCloudflaredAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredActionResult> UpdateCloudflaredAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredActionResult> StartManagedTunnelLoginAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredActionResult> LogoutCloudflareAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflaredDashboardStatus> GetCloudflaredDashboardStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflareManagedStatus> GetManagedCloudflareStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudflareManagedAvailability> CheckManagedTunnelAvailabilityAsync(CheckManagedTunnelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedTunnelProvisionResult> CreateManagedTunnelAsync(CreateManagedTunnelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingBootstrapSettingsSnapshotStore : IBootstrapSettingsSnapshotStore
    {
        public Task WriteAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingStartupRegistrationService : IStartupRegistrationService
    {
        public void Apply(bool enabled)
        {
        }
    }

    private sealed class RecordingContextMenuRegistrationService : IContextMenuRegistrationService
    {
        public Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

}
