using System.Text.Json;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal interface IBootstrapSettingsSnapshotStore
{
    Task WriteAsync(AppSettings settings, CancellationToken cancellationToken);
}

internal sealed class BootstrapSettingsSnapshotStore(string snapshotPath) : IBootstrapSettingsSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _snapshotPath = snapshotPath;

    public async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);

        var snapshot = new BootstrapSettingsSnapshot(settings.LocalApiPort);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(_snapshotPath, json, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed record BootstrapSettingsSnapshot(int LocalApiPort);
}
