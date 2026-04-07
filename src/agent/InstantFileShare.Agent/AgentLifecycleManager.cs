using System.Diagnostics;

namespace InstantFileShare.Agent;

internal interface IAgentLifecycleManager
{
    void ScheduleRestart();
}

internal sealed class AgentLifecycleManager(
    IHostApplicationLifetime lifetime,
    ILogger<AgentLifecycleManager> logger) : IAgentLifecycleManager
{
    private const int RestartShutdownDelayMilliseconds = 250;
    private int _restartScheduled;

    public void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) == 1)
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            logger.LogWarning("Skipping agent restart because the current executable path could not be resolved.");
            Interlocked.Exchange(ref _restartScheduled, 0);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--restart-parent-pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            });

            if (process is null)
            {
                logger.LogWarning("Skipping agent restart because the replacement process did not start.");
                Interlocked.Exchange(ref _restartScheduled, 0);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Skipping agent restart because the replacement process could not be started.");
            Interlocked.Exchange(ref _restartScheduled, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RestartShutdownDelayMilliseconds);
            }
            catch
            {
            }

            lifetime.StopApplication();
        });
    }
}
