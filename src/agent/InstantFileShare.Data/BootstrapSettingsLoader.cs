using InstantFileShare.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace InstantFileShare.Data;

public static class BootstrapSettingsLoader
{
    public static AppSettings LoadOrDefault(SqlitePaths paths)
    {
        if (!File.Exists(paths.DatabasePath))
        {
            return new AppSettings();
        }

        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM settings WHERE key = 'settings' LIMIT 1;";

        var json = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(json)
            ? new AppSettings()
            : JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new AppSettings();
    }
}
