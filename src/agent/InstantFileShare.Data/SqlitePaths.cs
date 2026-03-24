namespace InstantFileShare.Data;

public sealed class SqlitePaths
{
    public string DataDirectory { get; }
    public string DatabasePath { get; }

    public SqlitePaths(string? baseDirectory = null)
    {
        baseDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InstantFileShare");

        DataDirectory = baseDirectory;
        DatabasePath = Path.Combine(DataDirectory, "instant-file-share.db");
    }
}
