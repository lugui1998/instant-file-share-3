using InstantFileShare.Core;
using InstantFileShare.Data;
using Microsoft.Data.Sqlite;

namespace InstantFileShare.Agent.Tests;

public sealed class SqliteShareStoreTests
{
    [Fact]
    public async Task InitializeAsync_SeedsDefaults()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));

        await store.InitializeAsync(CancellationToken.None);

        var settings = await store.GetSettingsAsync(CancellationToken.None);
        var cloudflaredState = await store.GetCloudflaredStateAsync(CancellationToken.None);
        var profiles = await store.GetPublishProfilesAsync(CancellationToken.None);

        Assert.Equal(PublishMode.QuickTunnel, settings.DefaultPublishMode);
        Assert.Null(cloudflaredState.ExecutablePath);
        Assert.Equal(3, profiles.Count);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.QuickTunnel && profile.Enabled);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.ManagedCloudflare && !profile.Enabled);
        Assert.Contains(profiles, profile => profile.Mode == PublishMode.Manual && profile.Enabled);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndDoesNotResetSettings()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);
        await store.SaveSettingsAsync(new AppSettings
        {
            DefaultPublishMode = PublishMode.ManagedCloudflare,
            PublicTokenLength = 22,
            ShowLogs = true,
        }, CancellationToken.None);

        await store.InitializeAsync(CancellationToken.None);

        var settings = await store.GetSettingsAsync(CancellationToken.None);
        var profiles = await store.GetPublishProfilesAsync(CancellationToken.None);

        Assert.Equal(PublishMode.ManagedCloudflare, settings.DefaultPublishMode);
        Assert.Equal(22, settings.PublicTokenLength);
        Assert.True(settings.ShowLogs);
        Assert.Equal(3, profiles.Count);
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacySchema()
    {
        using var tempDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(tempDirectory.RootPath, "legacy.db");

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
                     {
                         """
                         CREATE TABLE shares (
                             id TEXT PRIMARY KEY,
                             token TEXT NOT NULL UNIQUE,
                             file_path TEXT NOT NULL,
                             file_name TEXT NOT NULL,
                             slug TEXT NULL,
                             public_base_url TEXT NOT NULL,
                             file_size INTEGER NOT NULL,
                             file_modified_at_utc TEXT NOT NULL,
                             created_at_utc TEXT NOT NULL,
                             expires_at_utc TEXT NULL,
                             max_uses INTEGER NULL,
                             use_count INTEGER NOT NULL,
                             publish_mode INTEGER NOT NULL,
                             state INTEGER NOT NULL,
                             broken_reason TEXT NULL,
                             last_accessed_at_utc TEXT NULL
                         );
                         """,
                         """
                         CREATE TABLE settings (
                             key TEXT PRIMARY KEY,
                             json TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE publish_profiles (
                             mode INTEGER PRIMARY KEY,
                             json TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE cloudflared_state (
                             key TEXT PRIMARY KEY,
                             json TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE transfers (
                             id TEXT PRIMARY KEY,
                             share_id TEXT NOT NULL,
                             token TEXT NOT NULL,
                             file_name TEXT NOT NULL,
                             client_session_id TEXT NULL,
                             client_fingerprint TEXT NULL,
                             remote_address TEXT NULL,
                             bytes_sent INTEGER NOT NULL,
                             total_bytes INTEGER NOT NULL,
                             started_at_utc TEXT NOT NULL,
                             last_updated_at_utc TEXT NOT NULL,
                             completed_at_utc TEXT NULL,
                             state INTEGER NOT NULL,
                             is_active INTEGER NOT NULL,
                             succeeded INTEGER NOT NULL,
                             error TEXT NULL
                         );
                         """,
                         """
                         CREATE TABLE share_usage_sessions (
                             share_id TEXT NOT NULL,
                             session_key TEXT NOT NULL,
                             PRIMARY KEY (share_id, session_key)
                         );
                         """,
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }

        var store = new SqliteShareStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);

        var share = CreateFolderShare(tempDirectory.RootPath);
        await store.AddShareAsync(share, CancellationToken.None);

        var transfer = CreateTransfer(share.Id, share.Token);
        await store.SaveTransferAsync(transfer, CancellationToken.None);

        var resolvedShare = await store.GetShareByTokenAsync(share.Token, CancellationToken.None);
        var transfers = await store.ListTransfersAsync(CancellationToken.None);

        Assert.NotNull(resolvedShare);
        Assert.Equal(ShareKind.Folder, resolvedShare!.ShareKind);
        Assert.True(resolvedShare.CanBrowseFolderContents);
        Assert.Single(transfers);
        Assert.Equal(TransferKind.MetadataPreview, transfers[0].TransferKind);
        Assert.Equal("Discordbot", transfers[0].RequesterName);
    }

    [Fact]
    public async Task ShareRoundTrip_PreservesFolderFields()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var share = CreateFolderShare(tempDirectory.RootPath);
        await store.AddShareAsync(share, CancellationToken.None);

        var byId = await store.GetShareByIdAsync(share.Id, CancellationToken.None);
        var byToken = await store.GetShareByTokenAsync(share.Token, CancellationToken.None);
        var shares = await store.ListSharesAsync(CancellationToken.None);

        Assert.NotNull(byId);
        Assert.NotNull(byToken);
        Assert.Single(shares);
        Assert.Equal(share.Slug, byToken!.Slug);
        Assert.True(byToken.CanBrowseFolderContents);
        Assert.True(byToken.CanDownloadFolderAsZip);
        Assert.Equal(FolderShareEntryPoint.Browse, byToken.PrimaryFolderEntryPoint);
    }

    [Fact]
    public async Task ListSharesAsync_ReturnsNewestFirst_AndPreservesNullOptionals()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var firstPath = Path.Combine(tempDirectory.RootPath, "first.txt");
        var secondPath = Path.Combine(tempDirectory.RootPath, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");

        var older = new ShareRecord
        {
            Id = "older-share",
            Token = "older-token",
            FilePath = firstPath,
            FileName = "first.txt",
            Slug = null,
            PublicBaseUrl = "http://127.0.0.1:46431",
            FileSize = 5,
            FileModifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            ShareKind = ShareKind.File,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAtUtc = null,
            MaxUses = null,
            UseCount = 0,
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
            BrokenReason = null,
            LastAccessedAtUtc = null,
        };
        var newer = older with
        {
            Id = "newer-share",
            Token = "newer-token",
            FilePath = secondPath,
            FileName = "second.txt",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            FileModifiedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.AddShareAsync(older, CancellationToken.None);
        await store.AddShareAsync(newer, CancellationToken.None);

        var shares = await store.ListSharesAsync(CancellationToken.None);
        var loadedOlder = await store.GetShareByTokenAsync("older-token", CancellationToken.None);

        Assert.Equal("newer-share", shares[0].Id);
        Assert.Equal("older-share", shares[1].Id);
        Assert.NotNull(loadedOlder);
        Assert.Null(loadedOlder!.Slug);
        Assert.Null(loadedOlder.ExpiresAtUtc);
        Assert.Null(loadedOlder.MaxUses);
        Assert.Null(loadedOlder.BrokenReason);
    }

    [Fact]
    public async Task TransferRoundTrip_AndPruneCompletedTransfers_Works()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var oldTransfer = CreateTransfer("share-old", "token-old") with
        {
            Id = "old-transfer",
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            State = TransferState.Completed,
            IsActive = false,
            Succeeded = true,
        };
        var recentTransfer = CreateTransfer("share-new", "token-new") with
        {
            Id = "recent-transfer",
            CompletedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            State = TransferState.Completed,
            IsActive = false,
            Succeeded = true,
        };

        await store.SaveTransferAsync(oldTransfer, CancellationToken.None);
        await store.SaveTransferAsync(recentTransfer, CancellationToken.None);
        await store.PruneCompletedTransfersAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        var transfers = await store.ListTransfersAsync(CancellationToken.None);

        Assert.Single(transfers);
        Assert.Equal("recent-transfer", transfers[0].Id);
    }

    [Fact]
    public async Task ListTransfersAsync_ReturnsNewestFirst_AndPreservesNullOptionals()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var older = new TransferSnapshot
        {
            Id = "older-transfer",
            ShareId = "share-1",
            Token = "token-1",
            FileName = "first.txt",
            TransferKind = TransferKind.FileDownload,
            RequesterName = null,
            ClientSessionId = null,
            ClientFingerprint = null,
            RemoteAddress = null,
            BytesSent = 0,
            TotalBytes = 10,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAtUtc = null,
            State = TransferState.InProgress,
            IsActive = true,
            Succeeded = false,
            Error = null,
        };
        var newer = older with
        {
            Id = "newer-transfer",
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.SaveTransferAsync(older, CancellationToken.None);
        await store.SaveTransferAsync(newer, CancellationToken.None);

        var transfers = await store.ListTransfersAsync(CancellationToken.None);

        Assert.Equal("newer-transfer", transfers[0].Id);
        Assert.Equal("older-transfer", transfers[1].Id);
        Assert.Null(transfers[1].RequesterName);
        Assert.Null(transfers[1].ClientSessionId);
        Assert.Null(transfers[1].ClientFingerprint);
        Assert.Null(transfers[1].RemoteAddress);
        Assert.Null(transfers[1].CompletedAtUtc);
        Assert.Null(transfers[1].Error);
    }

    [Fact]
    public async Task TryAddUsageSessionAsync_DeduplicatesSessionsPerShare()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var firstInsert = await store.TryAddUsageSessionAsync("share-1", "session-1", CancellationToken.None);
        var duplicateInsert = await store.TryAddUsageSessionAsync("share-1", "session-1", CancellationToken.None);
        var differentShareInsert = await store.TryAddUsageSessionAsync("share-2", "session-1", CancellationToken.None);

        Assert.True(firstInsert);
        Assert.False(duplicateInsert);
        Assert.True(differentShareInsert);
    }

    [Fact]
    public async Task SettingsAndCloudflaredState_RoundTrip()
    {
        using var tempDirectory = new TemporaryDirectory();
        var store = new SqliteShareStore(Path.Combine(tempDirectory.RootPath, "store.db"));
        await store.InitializeAsync(CancellationToken.None);

        var settings = new AppSettings
        {
            DefaultPublishMode = PublishMode.ManagedCloudflare,
            PublicTokenLength = 22,
            ManualBaseUrl = "https://example.com",
            ShowLogs = true,
        };
        var cloudflaredState = new CloudflaredState
        {
            ExecutablePath = @"C:\Tools\cloudflared.exe",
            Version = "2026.3.1",
            Ownership = CloudflaredOwnership.Winget,
            QuickTunnelUrl = "https://demo.trycloudflare.com",
            ActiveMode = PublishMode.QuickTunnel,
        };

        await store.SaveSettingsAsync(settings, CancellationToken.None);
        await store.SaveCloudflaredStateAsync(cloudflaredState, CancellationToken.None);

        var storedSettings = await store.GetSettingsAsync(CancellationToken.None);
        var storedState = await store.GetCloudflaredStateAsync(CancellationToken.None);

        Assert.Equal(settings.DefaultPublishMode, storedSettings.DefaultPublishMode);
        Assert.Equal(settings.PublicTokenLength, storedSettings.PublicTokenLength);
        Assert.Equal(settings.ManualBaseUrl, storedSettings.ManualBaseUrl);
        Assert.True(storedSettings.ShowLogs);
        Assert.Equal(cloudflaredState.ExecutablePath, storedState.ExecutablePath);
        Assert.Equal(cloudflaredState.QuickTunnelUrl, storedState.QuickTunnelUrl);
        Assert.Equal(cloudflaredState.ActiveMode, storedState.ActiveMode);
    }

    [Fact]
    public async Task InvalidSingletonJson_FallsBackToDefaults()
    {
        using var tempDirectory = new TemporaryDirectory();
        var databasePath = Path.Combine(tempDirectory.RootPath, "store.db");
        var store = new SqliteShareStore(databasePath);
        await store.InitializeAsync(CancellationToken.None);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
                     {
                         "UPDATE settings SET json = '{invalid' WHERE key = 'settings';",
                         "UPDATE cloudflared_state SET json = '{invalid' WHERE key = 'cloudflared';",
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }

        var settings = await store.GetSettingsAsync(CancellationToken.None);
        var state = await store.GetCloudflaredStateAsync(CancellationToken.None);

        Assert.Equal(PublishMode.QuickTunnel, settings.DefaultPublishMode);
        Assert.Null(state.ExecutablePath);
    }

    private static ShareRecord CreateFolderShare(string rootPath)
    {
        var folderPath = Path.Combine(rootPath, "shared-folder");
        Directory.CreateDirectory(folderPath);
        return new ShareRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = "folder-token",
            FilePath = folderPath,
            FileName = "shared-folder",
            Slug = "shared-folder",
            PublicBaseUrl = "http://127.0.0.1:46431",
            FileSize = 0,
            FileModifiedAtUtc = new DateTimeOffset(Directory.GetLastWriteTimeUtc(folderPath)),
            ShareKind = ShareKind.Folder,
            CanBrowseFolderContents = true,
            CanDownloadFolderAsZip = true,
            PrimaryFolderEntryPoint = FolderShareEntryPoint.Browse,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PublishMode = PublishMode.Manual,
            State = ShareState.Active,
        };
    }

    private static TransferSnapshot CreateTransfer(string shareId, string token)
    {
        return new TransferSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            ShareId = shareId,
            Token = token,
            FileName = "shared-folder",
            TransferKind = TransferKind.MetadataPreview,
            RequesterName = "Discordbot",
            ClientSessionId = "session-1",
            ClientFingerprint = "fingerprint-1",
            RemoteAddress = "203.0.113.10",
            BytesSent = 0,
            TotalBytes = 0,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = null,
            State = TransferState.InProgress,
            IsActive = true,
            Succeeded = false,
            Error = null,
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "ifs-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
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
        }
    }
}
