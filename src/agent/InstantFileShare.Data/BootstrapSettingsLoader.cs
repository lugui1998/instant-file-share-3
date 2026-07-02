using InstantFileShare.Core;
using Microsoft.Data.Sqlite;

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
        return AppSettingsJson.DeserializeOrDefault(json);
    }
}
