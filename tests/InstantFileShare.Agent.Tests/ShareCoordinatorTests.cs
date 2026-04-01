using System.Net;
using System.Net.Http;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using InstantFileShare.Data;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent.Tests;

public sealed class ShareCoordinatorTests
{
    [Fact]
    public async Task SaveSettingsAsync_NormalizesValuesAndAppliesRegistrations()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var settings = new AppSettings
        {
            DefaultPublishMode = PublishMode.Manual,
            ManualBaseUrl = "http://127.0.0.1:46431",
            PublicTokenLength = 1,
            FolderZipCompressionLevel = (FolderZipCompressionLevel)999,
            DefaultReceiveExpiryValue = -1,
            DefaultReceiveMaxTotalBytes = 0,
            HistoryRetentionValue = -5,
            HistoryItemsPerPage = -2,
            SharesItemsPerPage = -7,
            StartOnLogin = false,
        };

        await context.Coordinator.SaveSettingsAsync(settings, CancellationToken.None);
        var savedSettings = await context.Store.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(ShareTokenGenerator.MinLength, savedSettings.PublicTokenLength);
        Assert.Equal(FolderZipCompressionLevel.Optimal, savedSettings.FolderZipCompressionLevel);
        Assert.Equal(0, savedSettings.DefaultReceiveExpiryValue);
        Assert.Equal(0, savedSettings.DefaultReceiveMaxTotalBytes);
        Assert.Equal(0, savedSettings.HistoryRetentionValue);
        Assert.Equal(0, savedSettings.HistoryItemsPerPage);
        Assert.Equal(0, savedSettings.SharesItemsPerPage);
        Assert.False(context.StartupRegistration.AppliedValue);
        Assert.NotNull(context.ContextMenuRegistration.AppliedSettings);
        Assert.Contains(context.RuntimeEvents, entry => entry.Type == RuntimeEventType.SettingsUpdated);
    }

    [Fact]
    public async Task CreateShareAsync_ReusesCompatibleManualShare()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var filePath = Path.Combine(context.FilesDirectory, "hello.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var request = new CreateShareRequest(filePath, PublishMode.Manual);
        var firstResult = await context.Coordinator.CreateShareAsync(request, CancellationToken.None);
        var secondResult = await context.Coordinator.CreateShareAsync(request, CancellationToken.None);

        Assert.Equal(firstResult.Share.Id, secondResult.Share.Id);
        Assert.Equal(firstResult.Url, secondResult.Url);
        Assert.Single(context.RuntimeEvents.Where(entry => entry.Type == RuntimeEventType.ShareCreated));
    }

    [Fact]
    public async Task CreateReceiveLinkAsync_UsesReceiveDefaults()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync(new AppSettings
        {
            DefaultPublishMode = PublishMode.Manual,
            ManualBaseUrl = "http://127.0.0.1:46431",
            DefaultReceiveExpiryValue = 2,
            DefaultReceiveExpiryUnit = ExpiryUnit.Days,
            DefaultReceiveMaxTotalBytes = 1024,
            StartOnLogin = false,
        });
        var folderPath = Path.Combine(context.FilesDirectory, "drop");
        Directory.CreateDirectory(folderPath);

        var result = await context.Coordinator.CreateReceiveLinkAsync(new CreateReceiveLinkRequest(folderPath), CancellationToken.None);

        Assert.Equal(folderPath, result.ReceiveLink.TargetDirectoryPath);
        Assert.Equal("drop", result.ReceiveLink.TargetDisplayName);
        Assert.Equal(1024, result.ReceiveLink.MaxTotalBytes);
        Assert.NotNull(result.ReceiveLink.ExpiresAtUtc);
        Assert.Equal("http://127.0.0.1:46431/r/" + result.ReceiveLink.Token, result.Url);
    }

    [Fact]
    public async Task AddReceivedBytesAsync_UpdatesQuotaAndSupportsRollback()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var folderPath = Path.Combine(context.FilesDirectory, "drop");
        Directory.CreateDirectory(folderPath);
        var receiveLink = context.CreateReceiveLink("receive-token", folderPath) with
        {
            MaxTotalBytes = 10,
        };
        await context.Store.AddReceiveLinkAsync(receiveLink, CancellationToken.None);

        var reserved = await context.Coordinator.AddReceivedBytesAsync(receiveLink.Id, 10, CancellationToken.None);
        var rolledBack = await context.Coordinator.AddReceivedBytesAsync(receiveLink.Id, -4, CancellationToken.None);

        Assert.NotNull(reserved);
        Assert.Equal(ReceiveLinkState.Exhausted, reserved!.State);
        Assert.NotNull(rolledBack);
        Assert.Equal(6, rolledBack!.BytesReceived);
        Assert.Equal(ReceiveLinkState.Active, rolledBack.State);
    }

    [Fact]
    public async Task ResolveDownloadAsync_MarksMissingFileAsBrokenAndPublishesEvent()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var filePath = Path.Combine(context.FilesDirectory, "gone.txt");
        await File.WriteAllTextAsync(filePath, "gone");
        var created = await context.Coordinator.CreateShareAsync(new CreateShareRequest(filePath, PublishMode.Manual), CancellationToken.None);
        File.Delete(filePath);

        var resolved = await context.Coordinator.ResolveDownloadAsync(created.Share.Token, CancellationToken.None);
        var persisted = await context.Store.GetShareByIdAsync(created.Share.Id, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(ShareState.Broken, resolved!.State);
        Assert.NotNull(persisted);
        Assert.Equal(ShareState.Broken, persisted!.State);
        Assert.Contains(context.RuntimeEvents, entry => entry.Type == RuntimeEventType.ShareUpdated);
    }

    [Fact]
    public async Task ResolveDownloadAsync_PersistsExpiredShare()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var filePath = Path.Combine(context.FilesDirectory, "expired.txt");
        await File.WriteAllTextAsync(filePath, "expired");
        var share = context.CreateFileShare("expired-token", filePath) with
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await context.Store.AddShareAsync(share, CancellationToken.None);

        var resolved = await context.Coordinator.ResolveDownloadAsync(share.Token, CancellationToken.None);
        var persisted = await context.Store.GetShareByIdAsync(share.Id, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(ShareState.Expired, resolved!.State);
        Assert.NotNull(persisted);
        Assert.Equal(ShareState.Expired, persisted!.State);
    }

    [Fact]
    public async Task ResolveDownloadAsync_LenientMode_KeepsChangedFileActive()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync(new AppSettings
        {
            DefaultPublishMode = PublishMode.Manual,
            ManualBaseUrl = "http://127.0.0.1:46431",
            FileChangeBehavior = FileChangeBehavior.Lenient,
            DefaultExpiryValue = 0,
            StartOnLogin = false,
        });
        var filePath = Path.Combine(context.FilesDirectory, "lenient.txt");
        await File.WriteAllTextAsync(filePath, "before");
        var created = await context.Coordinator.CreateShareAsync(new CreateShareRequest(filePath, PublishMode.Manual), CancellationToken.None);
        await File.WriteAllTextAsync(filePath, "after");

        var resolved = await context.Coordinator.ResolveDownloadAsync(created.Share.Token, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(ShareState.Active, resolved!.State);
    }

    [Fact]
    public async Task MarkTransferCompletedAsync_ForFolderFileDownloads_OnlyCountsUniqueUsageSession()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var folderPath = Path.Combine(context.FilesDirectory, "team-files");
        Directory.CreateDirectory(folderPath);
        var share = context.CreateFolderShare("folder-share", folderPath, "team-files");
        await context.Store.AddShareAsync(share, CancellationToken.None);

        var firstTransfer = await context.Coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            "guide.txt",
            TransferKind.FolderFileDownload,
            clientSessionId: "session-1",
            clientFingerprint: "fp",
            remoteAddress: "127.0.0.1",
            totalBytes: 100,
            bytesSent: 0,
            requesterName: null,
            CancellationToken.None);

        await context.Coordinator.MarkTransferCompletedAsync(
            firstTransfer.Id,
            share.Id,
            share.Token,
            "guide.txt",
            TransferKind.FolderFileDownload,
            remoteAddress: "127.0.0.1",
            bytesSent: 100,
            totalBytes: 100,
            paused: false,
            succeeded: true,
            countsTowardUsage: true,
            usageSessionKey: "session-1",
            error: null,
            requesterName: null,
            CancellationToken.None);

        var secondTransfer = await context.Coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            "guide.txt",
            TransferKind.FolderFileDownload,
            clientSessionId: "session-1",
            clientFingerprint: "fp",
            remoteAddress: "127.0.0.1",
            totalBytes: 100,
            bytesSent: 0,
            requesterName: null,
            CancellationToken.None);

        await context.Coordinator.MarkTransferCompletedAsync(
            secondTransfer.Id,
            share.Id,
            share.Token,
            "guide.txt",
            TransferKind.FolderFileDownload,
            remoteAddress: "127.0.0.1",
            bytesSent: 100,
            totalBytes: 100,
            paused: false,
            succeeded: true,
            countsTowardUsage: true,
            usageSessionKey: "session-1",
            error: null,
            requesterName: null,
            CancellationToken.None);

        var persistedShare = await context.Store.GetShareByIdAsync(share.Id, CancellationToken.None);

        Assert.NotNull(persistedShare);
        Assert.Equal(1, persistedShare!.UseCount);
    }

    [Fact]
    public async Task MarkTransferCompletedAsync_DoesNotIncrementUseCount_WhenCountsTowardUsageIsFalse()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();
        var filePath = Path.Combine(context.FilesDirectory, "nocount.txt");
        await File.WriteAllTextAsync(filePath, "hello");
        var created = await context.Coordinator.CreateShareAsync(new CreateShareRequest(filePath, PublishMode.Manual), CancellationToken.None);

        var transfer = await context.Coordinator.StartTransferAsync(
            created.Share.Id,
            created.Share.Token,
            created.Share.FileName,
            TransferKind.FileDownload,
            clientSessionId: "session-1",
            clientFingerprint: "fp",
            remoteAddress: "127.0.0.1",
            totalBytes: 5,
            bytesSent: 0,
            requesterName: null,
            CancellationToken.None);

        await context.Coordinator.MarkTransferCompletedAsync(
            transfer.Id,
            created.Share.Id,
            created.Share.Token,
            created.Share.FileName,
            TransferKind.FileDownload,
            remoteAddress: "127.0.0.1",
            bytesSent: 5,
            totalBytes: 5,
            paused: false,
            succeeded: true,
            countsTowardUsage: false,
            usageSessionKey: null,
            error: null,
            requesterName: null,
            CancellationToken.None);

        var persistedShare = await context.Store.GetShareByIdAsync(created.Share.Id, CancellationToken.None);

        Assert.NotNull(persistedShare);
        Assert.Equal(0, persistedShare!.UseCount);
    }

    [Fact]
    public async Task RemoveTransferAsync_RemovesCompletedTransfer_AndPublishesEvent()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();

        var transfer = new TransferSnapshot
        {
            Id = "completed-transfer",
            ShareId = "share-1",
            Token = "token-1",
            FileName = "report.pdf",
            TransferKind = TransferKind.FileDownload,
            BytesSent = 128,
            TotalBytes = 128,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            State = TransferState.Completed,
            IsActive = false,
            Succeeded = true,
        };

        await context.Store.SaveTransferAsync(transfer, CancellationToken.None);

        await context.Coordinator.RemoveTransferAsync(transfer.Id, CancellationToken.None);

        var transfers = await context.Coordinator.GetTransfersAsync(CancellationToken.None);

        Assert.Empty(transfers);
        Assert.Contains(context.RuntimeEvents, entry => entry.Type == RuntimeEventType.TransferRemoved);
    }

    [Fact]
    public async Task RemoveTransferAsync_RejectsActiveTransfer()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();

        var activeTransfer = await context.Coordinator.StartTransferAsync(
            "share-1",
            "token-1",
            "report.pdf",
            TransferKind.FileDownload,
            clientSessionId: "session-1",
            clientFingerprint: "fingerprint-1",
            remoteAddress: "127.0.0.1",
            totalBytes: 128,
            bytesSent: 64,
            requesterName: null,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Coordinator.RemoveTransferAsync(activeTransfer.Id, CancellationToken.None));

        Assert.Equal("Active transfers cannot be removed from history.", exception.Message);
    }

    [Fact]
    public async Task ClearTransferHistoryAsync_RemovesOnlyInactiveTransfers_AndPublishesEvent()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();

        var completedTransfer = new TransferSnapshot
        {
            Id = "completed-transfer",
            ShareId = "share-1",
            Token = "token-1",
            FileName = "report.pdf",
            TransferKind = TransferKind.FileDownload,
            BytesSent = 128,
            TotalBytes = 128,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            State = TransferState.Completed,
            IsActive = false,
            Succeeded = true,
        };

        await context.Store.SaveTransferAsync(completedTransfer, CancellationToken.None);
        var activeTransfer = await context.Coordinator.StartTransferAsync(
            "share-2",
            "token-2",
            "active.pdf",
            TransferKind.FileDownload,
            clientSessionId: "session-2",
            clientFingerprint: "fingerprint-2",
            remoteAddress: "127.0.0.1",
            totalBytes: 256,
            bytesSent: 64,
            requesterName: null,
            CancellationToken.None);

        await context.Coordinator.ClearTransferHistoryAsync(CancellationToken.None);

        var transfers = await context.Coordinator.GetTransfersAsync(CancellationToken.None);

        Assert.Single(transfers);
        Assert.Equal(activeTransfer.Id, transfers[0].Id);
        Assert.Contains(context.RuntimeEvents, entry => entry.Type == RuntimeEventType.TransferHistoryCleared);
    }

    [Fact]
    public async Task EnsureTunnelBaseUrlAsync_ManualModePrefersConfiguredManualBaseUrl()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync();

        var resolvedBaseUrl = await context.Coordinator.EnsureTunnelBaseUrlAsync(PublishMode.Manual, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:46431", resolvedBaseUrl);
    }

    [Fact]
    public async Task EnsureTunnelBaseUrlAsync_ManualModeFallsBackToProfileBaseUrl()
    {
        await using var context = await ShareCoordinatorTestContext.CreateAsync(new AppSettings
        {
            DefaultPublishMode = PublishMode.Manual,
            ManualBaseUrl = null,
            ManualBindAddress = "127.0.0.1",
            ManualPublicPort = 46431,
            LocalApiPort = 46430,
            DefaultExpiryValue = 0,
            StartOnLogin = false,
        });
        await context.Store.SavePublishProfileAsync(new PublishProfile
        {
            Mode = PublishMode.Manual,
            BaseUrl = "https://manual-profile.example.com/",
            BindAddress = "127.0.0.1",
            PublicPort = 46431,
            Enabled = true,
        }, CancellationToken.None);

        var resolvedBaseUrl = await context.Coordinator.EnsureTunnelBaseUrlAsync(PublishMode.Manual, CancellationToken.None);

        Assert.Equal("https://manual-profile.example.com", resolvedBaseUrl);
    }

    private sealed class ShareCoordinatorTestContext : IAsyncDisposable
    {
        private ShareCoordinatorTestContext(
            string rootPath,
            string filesDirectory,
            SqliteShareStore store,
            ShareCoordinator coordinator,
            TestRuntimeEventStream runtimeEventStream,
            TestStartupRegistrationService startupRegistration,
            TestContextMenuRegistrationService contextMenuRegistration)
        {
            RootPath = rootPath;
            FilesDirectory = filesDirectory;
            Store = store;
            Coordinator = coordinator;
            RuntimeEventStream = runtimeEventStream;
            StartupRegistration = startupRegistration;
            ContextMenuRegistration = contextMenuRegistration;
        }

        public string RootPath { get; }
        public string FilesDirectory { get; }
        public SqliteShareStore Store { get; }
        public ShareCoordinator Coordinator { get; }
        public TestRuntimeEventStream RuntimeEventStream { get; }
        public TestStartupRegistrationService StartupRegistration { get; }
        public TestContextMenuRegistrationService ContextMenuRegistration { get; }
        public IReadOnlyList<RuntimeEvent> RuntimeEvents => RuntimeEventStream.Events;

        public static async Task<ShareCoordinatorTestContext> CreateAsync(AppSettings? settings = null)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ifs-share-coordinator-tests", Guid.NewGuid().ToString("N"));
            var filesDirectory = Path.Combine(rootPath, "files");
            var logsDirectory = Path.Combine(rootPath, "logs");
            Directory.CreateDirectory(filesDirectory);
            Directory.CreateDirectory(logsDirectory);

            var store = new SqliteShareStore(Path.Combine(rootPath, "store.db"));
            await store.InitializeAsync(CancellationToken.None);

            settings ??= new AppSettings
            {
                DefaultPublishMode = PublishMode.Manual,
                ManualBaseUrl = "http://127.0.0.1:46431",
                ManualBindAddress = "127.0.0.1",
                ManualPublicPort = 46431,
                LocalApiPort = 46430,
                DefaultExpiryValue = 0,
                DefaultMaxUses = null,
                StartOnLogin = true,
            };
            await store.SaveSettingsAsync(settings, CancellationToken.None);
            await store.SavePublishProfileAsync(new PublishProfile
            {
                Mode = PublishMode.Manual,
                BaseUrl = settings.ManualBaseUrl ?? "http://127.0.0.1:46431",
                BindAddress = settings.ManualBindAddress,
                PublicPort = settings.ManualPublicPort,
                Enabled = true,
            }, CancellationToken.None);

            var runtimeEventStream = new TestRuntimeEventStream();
            var startupRegistration = new TestStartupRegistrationService();
            var contextMenuRegistration = new TestContextMenuRegistrationService();
            var coordinator = new ShareCoordinator(
                store,
                runtimeEventStream,
                new CloudflaredSupervisor(new FileLogStore(logsDirectory)),
                new ExternalAddressResolver(new HttpClient(new StubHttpMessageHandler())),
                startupRegistration,
                contextMenuRegistration,
                new TestHttpClientFactory(new HttpClient(new StubHttpMessageHandler())));

            return new ShareCoordinatorTestContext(
                rootPath,
                filesDirectory,
                store,
                coordinator,
                runtimeEventStream,
                startupRegistration,
                contextMenuRegistration);
        }

        public ShareRecord CreateFolderShare(string token, string folderPath, string slug)
        {
            var directoryInfo = new DirectoryInfo(folderPath);
            return new ShareRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                FilePath = directoryInfo.FullName,
                FileName = directoryInfo.Name,
                Slug = slug,
                PublicBaseUrl = "http://127.0.0.1:46431",
                FileSize = 0,
                FileModifiedAtUtc = new DateTimeOffset(directoryInfo.LastWriteTimeUtc),
                ShareKind = ShareKind.Folder,
                CanBrowseFolderContents = true,
                CanDownloadFolderAsZip = true,
                PrimaryFolderEntryPoint = FolderShareEntryPoint.Browse,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PublishMode = PublishMode.Manual,
                State = ShareState.Active,
            };
        }

        public ShareRecord CreateFileShare(string token, string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            return new ShareRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                FilePath = fileInfo.FullName,
                FileName = fileInfo.Name,
                Slug = fileInfo.Name,
                PublicBaseUrl = "http://127.0.0.1:46431",
                FileSize = fileInfo.Length,
                FileModifiedAtUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                ShareKind = ShareKind.File,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PublishMode = PublishMode.Manual,
                State = ShareState.Active,
            };
        }

        public ReceiveLinkRecord CreateReceiveLink(string token, string folderPath)
        {
            var directoryInfo = new DirectoryInfo(folderPath);
            return new ReceiveLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Token = token,
                TargetDirectoryPath = directoryInfo.FullName,
                TargetDisplayName = directoryInfo.Name,
                PublicBaseUrl = "http://127.0.0.1:46431",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24),
                MaxTotalBytes = Defaults.DefaultReceiveMaxTotalBytes,
                BytesReceived = 0,
                PublishMode = PublishMode.Manual,
                State = ReceiveLinkState.Active,
            };
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                }
                catch
                {
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestRuntimeEventStream : IRuntimeEventStream
    {
        private readonly List<RuntimeEvent> _events = [];

        public IReadOnlyList<RuntimeEvent> Events => _events;

        public Task PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
        {
            _events.Add(runtimeEvent);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RuntimeEvent> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestStartupRegistrationService : IStartupRegistrationService
    {
        public bool AppliedValue { get; private set; }

        public void Apply(bool enabled)
        {
            AppliedValue = enabled;
        }
    }

    private sealed class TestContextMenuRegistrationService : IContextMenuRegistrationService
    {
        public AppSettings? AppliedSettings { get; private set; }

        public Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            AppliedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
