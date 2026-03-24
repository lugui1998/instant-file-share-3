using System.Text;

namespace InstantFileShare.Infrastructure;

public sealed class FileLogStore(string logsDirectory)
{
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private readonly SemaphoreSlim _cloudflareLock = new(1, 1);
    private readonly string _logsDirectory = logsDirectory;

    public string AgentLogPath => Path.Combine(_logsDirectory, "agent.log");
    public string CloudflareLogPath => Path.Combine(_logsDirectory, "cloudflare.log");

    public FileLogStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InstantFileShare",
        "logs"))
    {
    }

    public Task AppendAgentAsync(string line, CancellationToken cancellationToken)
        => AppendAsync(AgentLogPath, line, _agentLock, cancellationToken);

    public Task AppendCloudflareAsync(string line, CancellationToken cancellationToken)
        => AppendAsync(CloudflareLogPath, line, _cloudflareLock, cancellationToken);

    public Task<string> ReadAgentAsync(CancellationToken cancellationToken)
        => ReadAsync(AgentLogPath, cancellationToken);

    public Task<string> ReadCloudflareAsync(CancellationToken cancellationToken)
        => ReadAsync(CloudflareLogPath, cancellationToken);

    private static async Task AppendAsync(string path, string line, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}", Encoding.UTF8, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
