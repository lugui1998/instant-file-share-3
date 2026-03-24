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
        return string.IsNullOrWhiteSpace(json)
            ? new AppSettings()
            : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
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
        return string.IsNullOrWhiteSpace(json)
            ? new CloudflaredState()
            : JsonSerializer.Deserialize<CloudflaredState>(json, JsonOptions) ?? new CloudflaredState();
    }

    public Task SaveCloudflaredStateAsync(CloudflaredState state, CancellationToken cancellationToken)
    {
        return SaveSingletonJsonAsync("cloudflared_state", "cloudflared", JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
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
                expires_at_utc, max_uses, use_count, publish_mode, state, broken_reason, last_accessed_at_utc
            ) VALUES (
                $id, $token, $filePath, $fileName, $slug, $publicBaseUrl, $fileSize, $fileModifiedAtUtc, $createdAtUtc,
                $expiresAtUtc, $maxUses, $useCount, $publishMode, $state, $brokenReason, $lastAccessedAtUtc
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
                last_accessed_at_utc = excluded.last_accessed_at_utc;
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

    private SqliteConnection OpenConnection() => new($"Data Source={_databasePath}");

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
    };
}
