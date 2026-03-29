using System.Net;
using InstantFileShare.Core;
using InstantFileShare.Data;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal static class AgentApplication
{
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        AgentApplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AgentApplicationOptions();

        var startupOptions = AgentStartupOptions.Parse(args);
        var store = new SqliteShareStore(options.DatabasePath);
        await store.InitializeAsync(cancellationToken);

        if (options.InitialSettingsOverride is not null)
        {
            await store.SaveSettingsAsync(options.InitialSettingsOverride, cancellationToken);
        }

        var initialSettings = options.InitialSettingsOverride ?? await store.GetSettingsAsync(cancellationToken);
        var fileLogStore = new FileLogStore(options.LogsDirectory);

        var builder = WebApplication.CreateBuilder(args);
        options.ConfigureBuilder?.Invoke(builder);

        var isDevelopment = builder.Environment.IsDevelopment();
        builder.Logging.AddProvider(new AgentFileLoggerProvider(fileLogStore));

        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.ListenLocalhost(initialSettings.LocalApiPort);

            var bindAddress = IPAddress.TryParse(initialSettings.ManualBindAddress, out var parsedAddress)
                ? parsedAddress
                : IPAddress.Parse(Defaults.PublicBindAddress);
            kestrelOptions.Listen(bindAddress, initialSettings.ManualPublicPort);
        });

        builder.Services.AddAgentRuntimeServices(
            store,
            fileLogStore,
            new InitialAppSettingsSnapshot(initialSettings),
            options,
            startupOptions);

        var app = builder.Build();
        app.UseWebSockets();
        app.UseCors(isDevelopment ? "DevelopmentCors" : "ReleaseCors");
        app.UseControlPortRestriction(initialSettings.LocalApiPort);

        app.MapGet("/", () => Results.Ok(new { name = "Instant File Share Agent", status = "ok" }));
        app.MapControlApiEndpoints();
        app.MapRuntimeEndpoints();
        app.MapPublicShareAssetEndpoints();
        app.MapPublicShareEndpoints();

        return app;
    }
}
