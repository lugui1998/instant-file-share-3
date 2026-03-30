using System.Text.Json;
using InstantFileShare.Core;
using Microsoft.Data.Sqlite;

namespace InstantFileShare.Data;

public sealed class SqliteShareStore(string databasePath) : IShareStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var sql in Schema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureSharesColumnsAsync(connection, cancellationToken);
        await EnsureTransfersColumnsAsync(connection, cancellationToken);

        if (await ReadSingletonJsonAsync(connection, "settings", "settings", cancellationToken) is null)
        {
            await SaveSettingsAsync(new AppSettings(), cancellationToken);
        }

        if (await ReadSingletonJsonAsync(connection, "cloudflared_state", "cloudflared", cancellationToken) is null)
        {
            await SaveCloudflaredStateAsync(new CloudflaredState(), cancellationToken);
        }

        if ((await GetPublishProfilesAsync(cancellationToken)).Count == 0)
        {
            await SavePublishProfileAsync(new PublishProfile
            {
                Mode = PublishMode.QuickTunnel,
                Enabled = true,
            }, cancellationToken);
            await SavePublishProfileAsync(new PublishProfile
            {
                Mode = PublishMode.ManagedCloudflare,
                Enabled = false,
            }, cancellationToken);
            await SavePublishProfileAsync(new PublishProfile
            {
                Mode = PublishMode.Manual,
                BindAddress = Defaults.PublicBindAddress,
                PublicPort = Defaults.PublicPort,
                Enabled = true,
            }, cancellationToken);
        }
    }

    public async Task<ShareRecord> AddShareAsync(ShareRecord share, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await UpsertShareAsync(connection, share, cancellationToken);
        return share;
    }

    public async Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM shares ORDER BY created_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var shares = new List<ShareRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shares.Add(MapShare(reader));
        }

        return shares;
    }

    public Task<ShareRecord?> GetShareByIdAsync(string shareId, CancellationToken cancellationToken)
    {
        return GetShareByAsync("id", shareId, cancellationToken);
    }

    public Task<ShareRecord?> GetShareByTokenAsync(string token, CancellationToken cancellationToken)
    {
        return GetShareByAsync("token", token, cancellationToken);
    }

    public async Task UpdateShareAsync(ShareRecord share, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await UpsertShareAsync(connection, share, cancellationToken);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        var json = await ReadSingletonJsonAsync(connection, "settings", "settings", cancellationToken);
        return DeserializeOrDefault(json, static () => new AppSettings());
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        return SaveSingletonJsonAsync("settings", "settings", JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }

    public async Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM publish_profiles ORDER BY mode;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var profiles = new List<PublishProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(0);
            var profile = JsonSerializer.Deserialize<PublishProfile>(json, JsonOptions);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    public async Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO publish_profiles (mode, json) VALUES ($mode, $json)
            ON CONFLICT(mode) DO UPDATE SET json = excluded.json;
            """;
        command.Parameters.AddWithValue("$mode", (int)profile.Mode);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        var json = await ReadSingletonJsonAsync(connection, "cloudflared_state", "cloudflared", cancellationToken);
        return DeserializeOrDefault(json, static () => new CloudflaredState());
    }

    public Task SaveCloudflaredStateAsync(CloudflaredState state, CancellationToken cancellationToken)
    {
        return SaveSingletonJsonAsync("cloudflared_state", "cloudflared", JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
    }

    public async Task<IReadOnlyList<TransferSnapshot>> ListTransfersAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM transfers ORDER BY started_at_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var transfers = new List<TransferSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            transfers.Add(MapTransfer(reader));
        }

        return transfers;
    }

    public async Task SaveTransferAsync(TransferSnapshot transfer, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await UpsertTransferAsync(connection, transfer, cancellationToken);
    }

    public async Task DeleteTransferAsync(string transferId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transfers WHERE id = $transferId;";
        command.Parameters.AddWithValue("$transferId", transferId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearCompletedTransfersAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM transfers
            WHERE completed_at_utc IS NOT NULL
               OR is_active = 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PruneCompletedTransfersAsync(DateTimeOffset completedBeforeUtc, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM transfers
            WHERE completed_at_utc IS NOT NULL
              AND completed_at_utc < $completedBeforeUtc;
            """;
        command.Parameters.AddWithValue("$completedBeforeUtc", completedBeforeUtc.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryAddUsageSessionAsync(string shareId, string sessionKey, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO share_usage_sessions (share_id, session_key) VALUES ($shareId, $sessionKey)
            ON CONFLICT(share_id, session_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$shareId", shareId);
        command.Parameters.AddWithValue("$sessionKey", sessionKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<ShareRecord?> GetShareByAsync(string fieldName, string fieldValue, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM shares WHERE {fieldName} = $value LIMIT 1;";
        command.Parameters.AddWithValue("$value", fieldValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? MapShare(reader) : null;
    }

    private async Task UpsertShareAsync(SqliteConnection connection, ShareRecord share, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO shares (
                id, token, file_path, file_name, slug, public_base_url, file_size, file_modified_at_utc, created_at_utc,
                expires_at_utc, max_uses, use_count, publish_mode, state, broken_reason, last_accessed_at_utc,
                share_kind, can_browse_folder_contents, can_download_folder_as_zip, primary_folder_entry_point
            ) VALUES (
                $id, $token, $filePath, $fileName, $slug, $publicBaseUrl, $fileSize, $fileModifiedAtUtc, $createdAtUtc,
                $expiresAtUtc, $maxUses, $useCount, $publishMode, $state, $brokenReason, $lastAccessedAtUtc,
                $shareKind, $canBrowseFolderContents, $canDownloadFolderAsZip, $primaryFolderEntryPoint
            )
            ON CONFLICT(id) DO UPDATE SET
                token = excluded.token,
                file_path = excluded.file_path,
                file_name = excluded.file_name,
                slug = excluded.slug,
                public_base_url = excluded.public_base_url,
                file_size = excluded.file_size,
                file_modified_at_utc = excluded.file_modified_at_utc,
                created_at_utc = excluded.created_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                max_uses = excluded.max_uses,
                use_count = excluded.use_count,
                publish_mode = excluded.publish_mode,
                state = excluded.state,
                broken_reason = excluded.broken_reason,
                last_accessed_at_utc = excluded.last_accessed_at_utc,
                share_kind = excluded.share_kind,
                can_browse_folder_contents = excluded.can_browse_folder_contents,
                can_download_folder_as_zip = excluded.can_download_folder_as_zip,
                primary_folder_entry_point = excluded.primary_folder_entry_point;
            """;
        command.Parameters.AddWithValue("$id", share.Id);
        command.Parameters.AddWithValue("$token", share.Token);
        command.Parameters.AddWithValue("$filePath", share.FilePath);
        command.Parameters.AddWithValue("$fileName", share.FileName);
        command.Parameters.AddWithValue("$slug", (object?)share.Slug ?? DBNull.Value);
        command.Parameters.AddWithValue("$publicBaseUrl", share.PublicBaseUrl);
        command.Parameters.AddWithValue("$fileSize", share.FileSize);
        command.Parameters.AddWithValue("$fileModifiedAtUtc", share.FileModifiedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$createdAtUtc", share.CreatedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$expiresAtUtc", share.ExpiresAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$maxUses", share.MaxUses ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$useCount", share.UseCount);
        command.Parameters.AddWithValue("$publishMode", (int)share.PublishMode);
        command.Parameters.AddWithValue("$state", (int)share.State);
        command.Parameters.AddWithValue("$brokenReason", (object?)share.BrokenReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastAccessedAtUtc", share.LastAccessedAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$shareKind", (int)share.ShareKind);
        command.Parameters.AddWithValue("$canBrowseFolderContents", share.CanBrowseFolderContents ? 1 : 0);
        command.Parameters.AddWithValue("$canDownloadFolderAsZip", share.CanDownloadFolderAsZip ? 1 : 0);
        command.Parameters.AddWithValue("$primaryFolderEntryPoint", share.PrimaryFolderEntryPoint is null ? DBNull.Value : (int)share.PrimaryFolderEntryPoint.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertTransferAsync(SqliteConnection connection, TransferSnapshot transfer, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO transfers (
                id, share_id, token, file_name, transfer_kind, requester_name, client_session_id, client_fingerprint, remote_address, bytes_sent, total_bytes,
                started_at_utc, last_updated_at_utc, completed_at_utc, state, is_active, succeeded, error
            ) VALUES (
                $id, $shareId, $token, $fileName, $transferKind, $requesterName, $clientSessionId, $clientFingerprint, $remoteAddress, $bytesSent, $totalBytes,
                $startedAtUtc, $lastUpdatedAtUtc, $completedAtUtc, $state, $isActive, $succeeded, $error
            )
            ON CONFLICT(id) DO UPDATE SET
                share_id = excluded.share_id,
                token = excluded.token,
                file_name = excluded.file_name,
                transfer_kind = excluded.transfer_kind,
                requester_name = excluded.requester_name,
                client_session_id = excluded.client_session_id,
                client_fingerprint = excluded.client_fingerprint,
                remote_address = excluded.remote_address,
                bytes_sent = excluded.bytes_sent,
                total_bytes = excluded.total_bytes,
                started_at_utc = excluded.started_at_utc,
                last_updated_at_utc = excluded.last_updated_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                state = excluded.state,
                is_active = excluded.is_active,
                succeeded = excluded.succeeded,
                error = excluded.error;
            """;
        command.Parameters.AddWithValue("$id", transfer.Id);
        command.Parameters.AddWithValue("$shareId", transfer.ShareId);
        command.Parameters.AddWithValue("$token", transfer.Token);
        command.Parameters.AddWithValue("$fileName", transfer.FileName);
        command.Parameters.AddWithValue("$transferKind", (int)transfer.TransferKind);
        command.Parameters.AddWithValue("$requesterName", (object?)transfer.RequesterName ?? DBNull.Value);
        command.Parameters.AddWithValue("$clientSessionId", (object?)transfer.ClientSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$clientFingerprint", (object?)transfer.ClientFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$remoteAddress", (object?)transfer.RemoteAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytesSent", transfer.BytesSent);
        command.Parameters.AddWithValue("$totalBytes", transfer.TotalBytes);
        command.Parameters.AddWithValue("$startedAtUtc", transfer.StartedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$lastUpdatedAtUtc", transfer.LastUpdatedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$completedAtUtc", transfer.CompletedAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)transfer.State);
        command.Parameters.AddWithValue("$isActive", transfer.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$succeeded", transfer.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)transfer.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveSingletonJsonAsync(string tableName, string key, string json, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO {tableName} (key, json) VALUES ($key, $json)
            ON CONFLICT(key) DO UPDATE SET json = excluded.json;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadSingletonJsonAsync(SqliteConnection connection, string tableName, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT json FROM {tableName} WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static T DeserializeOrDefault<T>(string? json, Func<T> fallbackFactory)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallbackFactory();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallbackFactory();
        }
        catch (JsonException)
        {
            return fallbackFactory();
        }
    }

    private static ShareRecord MapShare(SqliteDataReader reader)
    {
        return new ShareRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Token = reader.GetString(reader.GetOrdinal("token")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            Slug = reader.IsDBNull(reader.GetOrdinal("slug")) ? null : reader.GetString(reader.GetOrdinal("slug")),
            PublicBaseUrl = reader.GetString(reader.GetOrdinal("public_base_url")),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            FileModifiedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("file_modified_at_utc"))),
            ShareKind = (ShareKind)reader.GetInt32(reader.GetOrdinal("share_kind")),
            CanBrowseFolderContents = reader.GetInt64(reader.GetOrdinal("can_browse_folder_contents")) != 0,
            CanDownloadFolderAsZip = reader.GetInt64(reader.GetOrdinal("can_download_folder_as_zip")) != 0,
            PrimaryFolderEntryPoint = reader.IsDBNull(reader.GetOrdinal("primary_folder_entry_point")) ? null : (FolderShareEntryPoint)reader.GetInt32(reader.GetOrdinal("primary_folder_entry_point")),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            ExpiresAtUtc = reader.IsDBNull(reader.GetOrdinal("expires_at_utc")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_at_utc"))),
            MaxUses = reader.IsDBNull(reader.GetOrdinal("max_uses")) ? null : reader.GetInt32(reader.GetOrdinal("max_uses")),
            UseCount = reader.GetInt32(reader.GetOrdinal("use_count")),
            PublishMode = (PublishMode)reader.GetInt32(reader.GetOrdinal("publish_mode")),
            State = (ShareState)reader.GetInt32(reader.GetOrdinal("state")),
            BrokenReason = reader.IsDBNull(reader.GetOrdinal("broken_reason")) ? null : reader.GetString(reader.GetOrdinal("broken_reason")),
            LastAccessedAtUtc = reader.IsDBNull(reader.GetOrdinal("last_accessed_at_utc")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_accessed_at_utc"))),
        };
    }

    private static TransferSnapshot MapTransfer(SqliteDataReader reader)
    {
        return new TransferSnapshot
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            ShareId = reader.GetString(reader.GetOrdinal("share_id")),
            Token = reader.GetString(reader.GetOrdinal("token")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            TransferKind = reader.IsDBNull(reader.GetOrdinal("transfer_kind")) ? TransferKind.FileDownload : (TransferKind)reader.GetInt32(reader.GetOrdinal("transfer_kind")),
            RequesterName = reader.IsDBNull(reader.GetOrdinal("requester_name")) ? null : reader.GetString(reader.GetOrdinal("requester_name")),
            ClientSessionId = reader.IsDBNull(reader.GetOrdinal("client_session_id")) ? null : reader.GetString(reader.GetOrdinal("client_session_id")),
            ClientFingerprint = reader.IsDBNull(reader.GetOrdinal("client_fingerprint")) ? null : reader.GetString(reader.GetOrdinal("client_fingerprint")),
            RemoteAddress = reader.IsDBNull(reader.GetOrdinal("remote_address")) ? null : reader.GetString(reader.GetOrdinal("remote_address")),
            BytesSent = reader.GetInt64(reader.GetOrdinal("bytes_sent")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            StartedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at_utc"))),
            LastUpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_updated_at_utc"))),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("completed_at_utc")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at_utc"))),
            State = (TransferState)reader.GetInt32(reader.GetOrdinal("state")),
            IsActive = reader.GetInt64(reader.GetOrdinal("is_active")) != 0,
            Succeeded = reader.GetInt64(reader.GetOrdinal("succeeded")) != 0,
            Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
        };
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_databasePath}");

    private static async Task EnsureTransfersColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(transfers);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columnNames.Add(reader.GetString(1));
            }
        }

        if (!columnNames.Contains("requester_name"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE transfers ADD COLUMN requester_name TEXT NULL;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columnNames.Contains("transfer_kind"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE transfers ADD COLUMN transfer_kind INTEGER NOT NULL DEFAULT 0;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureSharesColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(shares);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columnNames.Add(reader.GetString(1));
            }
        }

        if (!columnNames.Contains("share_kind"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE shares ADD COLUMN share_kind INTEGER NOT NULL DEFAULT 0;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columnNames.Contains("can_browse_folder_contents"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE shares ADD COLUMN can_browse_folder_contents INTEGER NOT NULL DEFAULT 0;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columnNames.Contains("can_download_folder_as_zip"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE shares ADD COLUMN can_download_folder_as_zip INTEGER NOT NULL DEFAULT 0;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columnNames.Contains("primary_folder_entry_point"))
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE shares ADD COLUMN primary_folder_entry_point INTEGER NULL;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static readonly string[] Schema =
    {
        """
        CREATE TABLE IF NOT EXISTS shares (
            id TEXT PRIMARY KEY,
            token TEXT NOT NULL UNIQUE,
            file_path TEXT NOT NULL,
            file_name TEXT NOT NULL,
            slug TEXT NULL,
            public_base_url TEXT NOT NULL,
            file_size INTEGER NOT NULL,
            file_modified_at_utc TEXT NOT NULL,
            share_kind INTEGER NOT NULL DEFAULT 0,
            can_browse_folder_contents INTEGER NOT NULL DEFAULT 0,
            can_download_folder_as_zip INTEGER NOT NULL DEFAULT 0,
            primary_folder_entry_point INTEGER NULL,
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
        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            json TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS publish_profiles (
            mode INTEGER PRIMARY KEY,
            json TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS cloudflared_state (
            key TEXT PRIMARY KEY,
            json TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS transfers (
            id TEXT PRIMARY KEY,
            share_id TEXT NOT NULL,
            token TEXT NOT NULL,
            file_name TEXT NOT NULL,
            transfer_kind INTEGER NOT NULL DEFAULT 0,
            requester_name TEXT NULL,
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
        CREATE TABLE IF NOT EXISTS share_usage_sessions (
            share_id TEXT NOT NULL,
            session_key TEXT NOT NULL,
            PRIMARY KEY (share_id, session_key)
        );
        """,
    };
}
