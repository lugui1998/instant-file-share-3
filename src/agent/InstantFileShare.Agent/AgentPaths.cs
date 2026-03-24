namespace InstantFileShare.Agent;

internal static class AgentPaths
{
    public static string GetAppDataDirectory()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "InstantFileShare");
    }

    public static string GetDatabasePath() => Path.Combine(GetAppDataDirectory(), "instant-file-share.db");

    public static string GetRepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
