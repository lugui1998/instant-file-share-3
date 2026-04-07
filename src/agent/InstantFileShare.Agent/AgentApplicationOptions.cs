using InstantFileShare.Core;

namespace InstantFileShare.Agent;

public sealed record AgentApplicationOptions
{
    public string DatabasePath { get; init; } = AgentPaths.GetDatabasePath();
    public string LogsDirectory { get; init; } = AgentPaths.GetLogsDirectory();
    public string BootstrapSettingsPath { get; init; } = AgentPaths.GetBootstrapSettingsPath();
    public string RepositoryRoot { get; init; } = AgentPaths.GetRepositoryRoot();
    public string? PublicShareAssetsDirectory { get; init; }
    public bool RunStartupTasks { get; init; } = true;
    public bool EnableTrayIcon { get; init; } = true;
    public bool EnablePipeCommandServer { get; init; } = true;
    public AppSettings? InitialSettingsOverride { get; init; }
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }
}
