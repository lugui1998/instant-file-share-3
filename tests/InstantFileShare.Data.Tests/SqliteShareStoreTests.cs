using InstantFileShare.Core;
using InstantFileShare.Data;

namespace InstantFileShare.Data.Tests;

public sealed class SqliteShareStoreTests
{
    [Fact]
    public async Task InitializeAsync_SeedsDefaultSettingsStateAndProfiles()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var settings = await fixture.Store.GetSettingsAsync(CancellationToken.None);
        var cloudflaredState = await fixture.Store.GetCloudflaredStateAsync(CancellationToken.None);
        var profiles = await fixture.Store.GetPublishProfilesAsync(CancellationToken.None);

        Assert.Equal(PublishMode.QuickTunnel, settings.DefaultPublishMode);
        Assert.Equal(Defaults.DefaultReceiveParallelUploadLimit, settings.ReceiveParallelUploadLimit);
        Assert.Equal(Defaults.DefaultReceiveUploadMode, settings.ReceiveUploadMode);
        Assert.Equal(Defaults.DefaultReceiveUploadChunkSizingMode, settings.ReceiveUploadChunkSizingMode);
        Assert.Equal(Defaults.DefaultReceiveUploadChunkSizeBytes, settings.ReceiveUploadChunkSizeBytes);
        Assert.Equal(Defaults.DefaultReceiveUploadMaxBodySizeBytes, settings.ReceiveUploadMaxBodySizeBytes);
        Assert.Equal(Defaults.DefaultReceiveUploadChunkTargetSeconds, settings.ReceiveUploadChunkTargetSeconds);
        Assert.False(cloudflaredState.ManagedTunnelRunning);
        Assert.Equal(3, profiles.Count);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.QuickTunnel && profile.Enabled);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.ManagedCloudflare && !profile.Enabled);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.Manual && profile.Enabled && profile.PublicPort == Defaults.PublicPort);
    }

    [Fact]
    public async Task ShareRecords_CanBeAddedRetrievedUpdatedAndListed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var share = CreateShareRecord("share-1", "token-1", Path.Combine(fixture.RootPath, "report.pdf"));

        await fixture.Store.AddShareAsync(share, CancellationToken.None);

        var byId = await fixture.Store.GetShareByIdAsync(share.Id, CancellationToken.None);
        var byToken = await fixture.Store.GetShareByTokenAsync(share.Token, CancellationToken.None);
        Assert.NotNull(byId);
        Assert.NotNull(byToken);
        Assert.Equal(share.FileName, byId!.FileName);
        Assert.Equal(share.FilePath, byToken!.FilePath);

        var updated = share with { State = ShareState.Revoked, UseCount = 3 };
        await fixture.Store.UpdateShareAsync(updated, CancellationToken.None);

        var listed = await fixture.Store.ListSharesAsync(CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(ShareState.Revoked, listed[0].State);
        Assert.Equal(3, listed[0].UseCount);
    }

    [Fact]
    public async Task PublishProfiles_And_CloudflaredState_RoundTrip()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var profile = new PublishProfile
        {
            Mode = PublishMode.ManagedCloudflare,
            Enabled = true,
            CloudflareTunnelName = "ifs-tunnel",
            CloudflareHostname = "share.example.com",
            CloudflareToken = "token",
        };
        var state = new CloudflaredState
        {
            ExecutablePath = @"C:\tools\cloudflared.exe",
            Version = "2026.3.0",
            Ownership = CloudflaredOwnership.Winget,
            ManagedTunnelRunning = true,
            ActiveMode = PublishMode.ManagedCloudflare,
        };

        await fixture.Store.SavePublishProfileAsync(profile, CancellationToken.None);
        await fixture.Store.SaveCloudflaredStateAsync(state, CancellationToken.None);

        var profiles = await fixture.Store.GetPublishProfilesAsync(CancellationToken.None);
        var storedState = await fixture.Store.GetCloudflaredStateAsync(CancellationToken.None);

        Assert.Contains(profiles, candidate =>
            candidate.Mode == PublishMode.ManagedCloudflare &&
            candidate.Enabled &&
            candidate.CloudflareTunnelName == "ifs-tunnel" &&
            candidate.CloudflareHostname == "share.example.com");
        Assert.Equal(state.ExecutablePath, storedState.ExecutablePath);
        Assert.Equal(state.Version, storedState.Version);
        Assert.True(storedState.ManagedTunnelRunning);
        Assert.Equal(PublishMode.ManagedCloudflare, storedState.ActiveMode);
    }

    [Fact]
    public async Task Transfers_CanBeSavedListedAndPruned()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var oldTransfer = CreateTransfer("transfer-old", completedAtUtc: DateTimeOffset.UtcNow.AddDays(-10));
        var activeTransfer = CreateTransfer("transfer-active", completedAtUtc: null) with
        {
            IsActive = true,
            State = TransferState.InProgress,
            CompletedAtUtc = null,
        };

        await fixture.Store.SaveTransferAsync(oldTransfer, CancellationToken.None);
        await fixture.Store.SaveTransferAsync(activeTransfer, CancellationToken.None);

        var listedBeforePrune = await fixture.Store.ListTransfersAsync(CancellationToken.None);
        Assert.Equal(2, listedBeforePrune.Count);
        Assert.Contains(listedBeforePrune, transfer =>
            transfer.Id == "transfer-old" &&
            transfer.ProgressBytes == 512 &&
            transfer.ProgressTotalBytes == 1024);

        await fixture.Store.PruneCompletedTransfersAsync(DateTimeOffset.UtcNow.AddDays(-5), CancellationToken.None);

        var listedAfterPrune = await fixture.Store.ListTransfersAsync(CancellationToken.None);
        Assert.Single(listedAfterPrune);
        Assert.Equal("transfer-active", listedAfterPrune[0].Id);
    }

    [Fact]
    public async Task Transfers_CanBeDeletedIndividually_AndClearedByHistory()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var completedTransfer = CreateTransfer("transfer-completed", completedAtUtc: DateTimeOffset.UtcNow);
        var otherCompletedTransfer = CreateTransfer("transfer-other", completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        var activeTransfer = CreateTransfer("transfer-active", completedAtUtc: null) with
        {
            IsActive = true,
            State = TransferState.InProgress,
            CompletedAtUtc = null,
        };

        await fixture.Store.SaveTransferAsync(completedTransfer, CancellationToken.None);
        await fixture.Store.SaveTransferAsync(otherCompletedTransfer, CancellationToken.None);
        await fixture.Store.SaveTransferAsync(activeTransfer, CancellationToken.None);

        await fixture.Store.DeleteTransferAsync("transfer-completed", CancellationToken.None);
        var listedAfterDelete = await fixture.Store.ListTransfersAsync(CancellationToken.None);
        Assert.DoesNotContain(listedAfterDelete, transfer => transfer.Id == "transfer-completed");

        await fixture.Store.ClearCompletedTransfersAsync(CancellationToken.None);
        var listedAfterClear = await fixture.Store.ListTransfersAsync(CancellationToken.None);
        Assert.Single(listedAfterClear);
        Assert.Equal("transfer-active", listedAfterClear[0].Id);
    }

    [Fact]
    public async Task UsageSessions_AreDeduplicatedPerShareAndSessionKey()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var share = CreateShareRecord("share-usage", "token-usage", Path.Combine(fixture.RootPath, "usage.txt"));
        await fixture.Store.AddShareAsync(share, CancellationToken.None);

        var firstInsert = await fixture.Store.TryAddUsageSessionAsync(share.Id, "session-1", CancellationToken.None);
        var duplicateInsert = await fixture.Store.TryAddUsageSessionAsync(share.Id, "session-1", CancellationToken.None);
        var differentSession = await fixture.Store.TryAddUsageSessionAsync(share.Id, "session-2", CancellationToken.None);

        Assert.True(firstInsert);
        Assert.False(duplicateInsert);
        Assert.True(differentSession);
    }

    private static ShareRecord CreateShareRecord(string id, string token, string filePath)
    {
        return new ShareRecord
        {
            Id = id,
            Token = token,
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Slug = Path.GetFileNameWithoutExtension(filePath),
            PublicBaseUrl = "https://example.com",
            FileSize = 128,
            FileModifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
        };
    }

    private static TransferSnapshot CreateTransfer(string id, DateTimeOffset? completedAtUtc)
    {
        return new TransferSnapshot
        {
            Id = id,
            ShareId = "share-1",
            Token = "token-1",
            FileName = "report.pdf",
            TransferKind = TransferKind.FileDownload,
            ClientSessionId = "session-1",
            ClientFingerprint = "fingerprint",
            RemoteAddress = "203.0.113.10",
            BytesSent = 512,
            TotalBytes = 1024,
            ProgressBytes = 512,
            ProgressTotalBytes = 1024,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            LastUpdatedAtUtc = completedAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAtUtc = completedAtUtc,
            State = completedAtUtc is null ? TransferState.InProgress : TransferState.Completed,
            IsActive = completedAtUtc is null,
            Succeeded = completedAtUtc is not null,
            Error = null,
        };
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(string rootPath, SqliteShareStore store)
        {
            RootPath = rootPath;
            Store = store;
        }

        public string RootPath { get; }

        public SqliteShareStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ifs-data-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var databasePath = Path.Combine(rootPath, "instant-file-share.db");
            var store = new SqliteShareStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);
            return new StoreFixture(rootPath, store);
        }

        public ValueTask DisposeAsync()
        {
            DeleteDirectoryEventually(RootPath);
            return ValueTask.CompletedTask;
        }

        private static void DeleteDirectoryEventually(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
