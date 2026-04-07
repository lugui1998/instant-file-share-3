using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class AgentStartupHostedService(
    AgentApplicationOptions applicationOptions,
    AgentStartupOptions startupOptions,
    InitialAppSettingsSnapshot initialSettings,
    IBootstrapSettingsSnapshotStore bootstrapSettingsSnapshotStore,
    IStartupRegistrationService startupRegistrationService,
    IContextMenuRegistrationService contextMenuRegistrationService,
    NotificationService notificationService,
    DashboardLauncher dashboardLauncher,
    IShareCoordinator shareCoordinator,
    IHostApplicationLifetime lifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bootstrapSettingsSnapshotStore.WriteAsync(initialSettings.Settings, cancellationToken);

        if (!applicationOptions.RunStartupTasks)
        {
            return;
        }

        startupRegistrationService.Apply(initialSettings.Settings.StartOnLogin);
        await contextMenuRegistrationService.ApplyAsync(initialSettings.Settings, cancellationToken);

        notificationService.OpenDashboardRequested += HandleOpenDashboardRequested;

        try
        {
            await shareCoordinator.DetectCloudflaredAsync(cancellationToken);
        }
        catch
        {
            // Detection is best-effort during startup. Share creation retries on demand.
        }

        if (startupOptions.InstallerFirstRunRequested)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await shareCoordinator.EnsureInstallerFirstRunTunnelAsync(lifetime.ApplicationStopping);
                    }
                    catch
                    {
                        // First-run tunnel setup is best-effort so the app is usable immediately after install.
                    }
                });
            });
        }

        if (initialSettings.Settings.OpenDashboardOnStart || startupOptions.LaunchDashboardRequested)
        {
            await dashboardLauncher.Launch(applicationOptions.RepositoryRoot);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notificationService.OpenDashboardRequested -= HandleOpenDashboardRequested;
        return Task.CompletedTask;
    }

    private void HandleOpenDashboardRequested()
    {
        _ = dashboardLauncher.Launch(applicationOptions.RepositoryRoot);
    }
}
