using InstantFileShare.Agent;
using InstantFileShare.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstantFileShare.Agent.Tests;

public sealed class PipeCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShareCommand_CreatesFileShareAndCopiesUrl()
    {
        var coordinator = new RecordingShareCoordinator();
        var clipboard = new RecordingClipboardService();
        var notifications = new RecordingNotificationService();
        var launcher = new RecordingUiLauncher();
        var handler = new PipeCommandHandler(coordinator, clipboard, notifications, launcher, NullLogger<PipeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PipeCommand("share", @"C:\temp\demo.txt"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://example.test/s/token123", result.ShareUrl);
        Assert.Single(coordinator.CreateRequests);
        Assert.Equal(ShareKind.File, coordinator.CreateRequests[0].ShareKind);
        Assert.Equal(@"C:\temp\demo.txt", coordinator.CreateRequests[0].FilePath);
        Assert.Single(clipboard.CopiedTexts);
        Assert.Equal("https://example.test/s/token123", clipboard.CopiedTexts[0]);
        Assert.Single(notifications.Messages);
        Assert.Equal("demo.txt", notifications.Messages[0].Message);
        Assert.Empty(launcher.OpenRequests);
    }

    [Fact]
    public async Task HandleAsync_ShareFolderCommands_UseExpectedEntryPoints()
    {
        var coordinator = new RecordingShareCoordinator();
        var handler = new PipeCommandHandler(
            coordinator,
            new RecordingClipboardService(),
            new RecordingNotificationService(),
            new RecordingUiLauncher(),
            NullLogger<PipeCommandHandler>.Instance);

        await handler.HandleAsync(new PipeCommand("share-folder-zip", @"C:\temp\folder"), CancellationToken.None);
        await handler.HandleAsync(new PipeCommand("share-folder-browse", @"C:\temp\folder"), CancellationToken.None);

        Assert.Equal(2, coordinator.CreateRequests.Count);
        Assert.Equal(ShareKind.Folder, coordinator.CreateRequests[0].ShareKind);
        Assert.Equal(FolderShareEntryPoint.Zip, coordinator.CreateRequests[0].PrimaryFolderEntryPoint);
        Assert.Equal(FolderShareEntryPoint.Browse, coordinator.CreateRequests[1].PrimaryFolderEntryPoint);
    }

    [Fact]
    public async Task HandleAsync_ReceiveHere_CreatesReceiveLinkAndCopiesUrl()
    {
        var coordinator = new RecordingShareCoordinator();
        var clipboard = new RecordingClipboardService();
        var notifications = new RecordingNotificationService();
        var handler = new PipeCommandHandler(coordinator, clipboard, notifications, new RecordingUiLauncher(), NullLogger<PipeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PipeCommand("receive-here", @"C:\temp\drop"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://example.test/r/receive123", result.ShareUrl);
        Assert.Single(coordinator.ReceiveRequests);
        Assert.Equal(@"C:\temp\drop", coordinator.ReceiveRequests[0].DirectoryPath);
        Assert.Contains(clipboard.CopiedTexts, entry => entry == "https://example.test/r/receive123");
    }

    [Fact]
    public async Task HandleAsync_OpenDashboard_DelegatesToLauncher()
    {
        var launcher = new RecordingUiLauncher();
        var handler = new PipeCommandHandler(
            new RecordingShareCoordinator(),
            new RecordingClipboardService(),
            new RecordingNotificationService(),
            launcher,
            NullLogger<PipeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PipeCommand("open-dashboard"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(launcher.OpenRequests);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedCommand_ReturnsFailure()
    {
        var handler = new PipeCommandHandler(
            new RecordingShareCoordinator(),
            new RecordingClipboardService(),
            new RecordingNotificationService(),
            new RecordingUiLauncher(),
            NullLogger<PipeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PipeCommand("unknown"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Unsupported command.", result.Message);
    }

    [Fact]
    public async Task HandleAsync_ClipboardAndNotificationFailures_StillReturnSuccess()
    {
        var coordinator = new RecordingShareCoordinator();
        var handler = new PipeCommandHandler(
            coordinator,
            new ThrowingClipboardService(),
            new ThrowingNotificationService(),
            new RecordingUiLauncher(),
            NullLogger<PipeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PipeCommand("share", @"C:\temp\demo.txt"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://example.test/s/token123", result.ShareUrl);
        Assert.Single(coordinator.CreateRequests);
    }

    private sealed class RecordingShareCoordinator : IShareCoordinator
    {
        public List<CreateShareRequest> CreateRequests { get; } = [];
        public List<CreateReceiveLinkRequest> ReceiveRequests { get; } = [];

        public Task<(ShareRecord Share, string Url)> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken)
        {
            CreateRequests.Add(request);
            var fileName = Path.GetFileName(request.FilePath);
            return Task.FromResult<(ShareRecord, string)>((
                new ShareRecord
                {
                    Id = "share-1",
                    Token = "token123",
                    FilePath = request.FilePath,
                    FileName = fileName,
                    Slug = fileName,
                    PublicBaseUrl = "https://example.test",
                    FileSize = 0,
                    FileModifiedAtUtc = DateTimeOffset.UtcNow,
                    ShareKind = request.ShareKind,
                    CanBrowseFolderContents = request.ShareKind == ShareKind.Folder,
                    CanDownloadFolderAsZip = request.ShareKind == ShareKind.Folder,
                    PrimaryFolderEntryPoint = request.PrimaryFolderEntryPoint,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    PublishMode = PublishMode.Manual,
                    State = ShareState.Active,
                },
                "https://example.test/s/token123"));
        }

        public Task<(ReceiveLinkRecord ReceiveLink, string Url)> CreateReceiveLinkAsync(CreateReceiveLinkRequest request, CancellationToken cancellationToken)
        {
            ReceiveRequests.Add(request);
            return Task.FromResult((
                new ReceiveLinkRecord
                {
                    Id = "receive-1",
                    Token = "receive123",
                    TargetDirectoryPath = request.DirectoryPath,
                    TargetDisplayName = Path.GetFileName(request.DirectoryPath),
                    PublicBaseUrl = "https://example.test",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24),
                    MaxTotalBytes = Defaults.DefaultReceiveMaxTotalBytes,
                    BytesReceived = 0,
                    PublishMode = PublishMode.Manual,
                },
                "https://example.test/r/receive123"));
        }

        public Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ShareRecord?> ResolveDownloadAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReceiveLinkRecord?> ResolveReceiveLinkAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReceiveLinkRecord?> AddReceivedBytesAsync(string receiveLinkId, long bytesReceived, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeShareAsync(string shareId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TransferSnapshot>> GetTransfersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveTransferAsync(string transferId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearTransferHistoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransferSnapshot> StartTransferAsync(string shareId, string token, string fileName, TransferKind transferKind, string? clientSessionId, string? clientFingerprint, string? remoteAddress, long totalBytes, long bytesSent, string? requesterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateTransferProgressAsync(string transferId, long bytesSent, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    private sealed class RecordingClipboardService : IClipboardService
    {
        public List<string> CopiedTexts { get; } = [];

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            CopiedTexts.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingClipboardService : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Clipboard unavailable.");
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(string Title, string Message)> Messages { get; } = [];

        public void ShowInfo(string title, string message) => Messages.Add((title, message));
        public void ShowError(string title, string message) => Messages.Add((title, message));
    }

    private sealed class ThrowingNotificationService : INotificationService
    {
        public void ShowInfo(string title, string message) => throw new InvalidOperationException("Notifications unavailable.");
        public void ShowError(string title, string message) => throw new InvalidOperationException("Notifications unavailable.");
    }

    private sealed class RecordingUiLauncher : IUiLauncher
    {
        public List<bool> OpenRequests { get; } = [];

        public Task OpenDashboardAsync(CancellationToken cancellationToken)
        {
            OpenRequests.Add(true);
            return Task.CompletedTask;
        }
    }
}
