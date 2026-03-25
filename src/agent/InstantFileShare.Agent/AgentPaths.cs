namespace InstantFileShare.Agent;

internal static class AgentPaths
{
    public static string GetAppDataDirectory()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "InstantFileShare");
    }

    public static string GetDatabasePath() => Path.Combine(GetAppDataDirectory(), "instant-file-share.db");

    public static string GetLogsDirectory() => Path.Combine(GetAppDataDirectory(), "logs");

    public static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "InstantFileShare.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    }
}
