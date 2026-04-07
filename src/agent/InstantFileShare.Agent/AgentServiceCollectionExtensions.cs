using System.Text.Json.Serialization;
using InstantFileShare.Core;
using InstantFileShare.Data;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntimeServices(
        this IServiceCollection services,
        SqliteShareStore store,
        FileLogStore fileLogStore,
        InitialAppSettingsSnapshot initialSettings,
        AgentApplicationOptions applicationOptions,
        AgentStartupOptions startupOptions)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("DevelopmentCors", policy =>
                policy.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod());

            options.AddPolicy("ReleaseCors", policy =>
                policy.SetIsOriginAllowed(ControlOriginPolicy.IsAllowed)
                    .AllowAnyHeader()
                    .AllowAnyMethod());
        });

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddSingleton(applicationOptions);
        services.AddSingleton(startupOptions);
        services.AddSingleton(initialSettings);
        services.AddSingleton(fileLogStore);
        services.AddSingleton<IBootstrapSettingsSnapshotStore>(_ => new BootstrapSettingsSnapshotStore(applicationOptions.BootstrapSettingsPath));
        services.AddSingleton<IShareStore>(store);
        services.AddSingleton<IRuntimeEventStream, ChannelRuntimeEventStream>();
        services.AddSingleton<IAgentLifecycleManager, AgentLifecycleManager>();
        services.AddSingleton<CloudflaredSupervisor>();
        services.AddHttpClient<ExternalAddressResolver>();
        services.AddSingleton<DashboardLauncher>();
        services.AddSingleton<IUiLauncher>(provider => provider.GetRequiredService<DashboardLauncher>());
        services.AddSingleton<IExplorerLauncher, ExplorerLauncher>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<IClipboardService>(provider => provider.GetRequiredService<ClipboardService>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationService>());
        services.AddSingleton<PowerManagementService>();
        services.AddSingleton<IStartupRegistrationService, StartupRegistrationService>();
        services.AddSingleton<IContextMenuRegistrationService, ContextMenuRegistrationService>();
        services.AddSingleton<IShareCoordinator, ShareCoordinator>();
        services.AddSingleton<IPipeCommandHandler, PipeCommandHandler>();
        services.AddSingleton<PipeCommandServer>();
        services.AddSingleton<PublicShareAssetLocator>();
        services.AddSingleton<PublicSharePageModelFactory>();
        services.AddSingleton<PublicShareHtmlRenderer>();
        services.AddSingleton<IHostedService, AgentStartupHostedService>();

        if (applicationOptions.EnableTrayIcon)
        {
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<NotificationService>());
        }

        if (applicationOptions.EnablePipeCommandServer)
        {
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PipeCommandServer>());
        }

        return services;
    }
}
