using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantFileShare.Core;
using InstantFileShare.Data;
using InstantFileShare.Infrastructure;

var databasePath = global::InstantFileShare.Agent.AgentPaths.GetDatabasePath();
var store = new SqliteShareStore(databasePath);
await store.InitializeAsync(CancellationToken.None);
var initialSettings = await store.GetSettingsAsync(CancellationToken.None);
var fileLogStore = new global::InstantFileShare.Infrastructure.FileLogStore(global::InstantFileShare.Agent.AgentPaths.GetLogsDirectory());

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddProvider(new global::InstantFileShare.Agent.AgentFileLoggerProvider(fileLogStore));

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(initialSettings.LocalApiPort);

    var bindAddress = IPAddress.TryParse(initialSettings.ManualBindAddress, out var parsed)
        ? parsed
        : IPAddress.Parse(Defaults.PublicBindAddress);
    options.Listen(bindAddress, initialSettings.ManualPublicPort);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton(fileLogStore);
builder.Services.AddSingleton<IShareStore>(store);
builder.Services.AddSingleton<IRuntimeEventStream, ChannelRuntimeEventStream>();
builder.Services.AddSingleton<CloudflaredSupervisor>();
builder.Services.AddHttpClient<ExternalAddressResolver>();
builder.Services.AddSingleton<global::InstantFileShare.Agent.DashboardLauncher>();
builder.Services.AddSingleton<global::InstantFileShare.Agent.ClipboardService>();
builder.Services.AddSingleton<global::InstantFileShare.Agent.NotificationService>();
builder.Services.AddSingleton<global::InstantFileShare.Agent.PowerManagementService>();
builder.Services.AddSingleton<IStartupRegistrationService, global::InstantFileShare.Agent.StartupRegistrationService>();
builder.Services.AddSingleton<IContextMenuRegistrationService, global::InstantFileShare.Agent.ContextMenuRegistrationService>();
builder.Services.AddSingleton<IShareCoordinator, global::InstantFileShare.Agent.ShareCoordinator>();
builder.Services.AddSingleton<IPipeCommandHandler, global::InstantFileShare.Agent.PipeCommandHandler>();
builder.Services.AddSingleton<global::InstantFileShare.Agent.PipeCommandServer>();
builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<global::InstantFileShare.Agent.NotificationService>());
builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<global::InstantFileShare.Agent.PipeCommandServer>());

var app = builder.Build();
app.UseWebSockets();
app.UseCors();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
jsonOptions.Converters.Add(new JsonStringEnumConverter());

app.Services.GetRequiredService<IStartupRegistrationService>().Apply(initialSettings.StartOnLogin);
await app.Services.GetRequiredService<IContextMenuRegistrationService>().ApplyAsync(initialSettings.AddFileContextMenuButton, CancellationToken.None);

var notifications = app.Services.GetRequiredService<global::InstantFileShare.Agent.NotificationService>();
notifications.OpenDashboardRequested += () => app.Services.GetRequiredService<global::InstantFileShare.Agent.DashboardLauncher>().Launch(global::InstantFileShare.Agent.AgentPaths.GetRepositoryRoot());

try
{
    await app.Services.GetRequiredService<IShareCoordinator>().DetectCloudflaredAsync(CancellationToken.None);
}
catch
{
    // Detection is best-effort during startup. Share creation will retry on demand.
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isControlPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");
    if (isControlPath && context.Connection.LocalPort != initialSettings.LocalApiPort)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Ok(new { name = "Instant File Share Agent", status = "ok" }));

app.MapGet("/api/runtime", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetRuntimeSnapshotAsync(cancellationToken)));

app.MapGet("/api/shares", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.ListSharesAsync(cancellationToken)));

app.MapPost("/api/shares", async (CreateShareRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var (share, url) = await coordinator.CreateShareAsync(request, cancellationToken);
    return Results.Ok(new { share, url });
});

app.MapDelete("/api/shares/{shareId}", async (string shareId, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
{
    await coordinator.RevokeShareAsync(shareId, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/settings", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetSettingsAsync(cancellationToken)));

app.MapPut("/api/settings", async (UpdateSettingsRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
{
    await coordinator.SaveSettingsAsync(request.Settings, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/publish-profiles", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetPublishProfilesAsync(cancellationToken)));

app.MapPut("/api/publish-profiles/{mode}", async (PublishMode mode, PublishProfile profile, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
{
    await coordinator.SavePublishProfileAsync(profile with { Mode = mode }, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/transfers", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetTransfersAsync(cancellationToken)));

app.MapGet("/api/logs/agent", async (global::InstantFileShare.Infrastructure.FileLogStore logStore, CancellationToken cancellationToken) =>
    Results.Text(await logStore.ReadAgentAsync(cancellationToken), "text/plain"));

app.MapGet("/api/logs/cloudflare", async (global::InstantFileShare.Infrastructure.FileLogStore logStore, CancellationToken cancellationToken) =>
    Results.Text(await logStore.ReadCloudflareAsync(cancellationToken), "text/plain"));

app.MapPost("/api/cloudflared/detect", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.DetectCloudflaredAsync(cancellationToken)));

app.MapPost("/api/cloudflared/install", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.InstallCloudflaredAsync(cancellationToken)));

app.MapPost("/api/cloudflared/update", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.UpdateCloudflaredAsync(cancellationToken)));

app.MapPost("/api/cloudflared/login", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.StartManagedTunnelLoginAsync(cancellationToken)));

app.MapPost("/api/cloudflared/logout", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.LogoutCloudflareAsync(cancellationToken)));

app.MapGet("/api/cloudflared/status", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetCloudflaredDashboardStatusAsync(cancellationToken)));

app.MapGet("/api/cloudflared/managed-status", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetManagedCloudflareStatusAsync(cancellationToken)));

app.MapPost("/api/cloudflared/managed-check", async (CheckManagedTunnelRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.CheckManagedTunnelAvailabilityAsync(request, cancellationToken)));

app.MapPost("/api/cloudflared/managed-tunnel", async (CreateManagedTunnelRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.CreateManagedTunnelAsync(request, cancellationToken)));

app.MapGet("/ws/runtime", async (HttpContext context, IRuntimeEventStream stream, CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await foreach (var runtimeEvent in stream.ListenAsync(cancellationToken))
    {
        if (socket.State != WebSocketState.Open)
        {
            break;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(runtimeEvent, jsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }
});

app.MapMethods("/s/{token}", ["GET", "HEAD"], (string token, HttpContext context, IShareCoordinator coordinator, global::InstantFileShare.Agent.PowerManagementService power, CancellationToken cancellationToken) =>
    HandleDownloadAsync(token, context, coordinator, power, cancellationToken));
app.MapMethods("/s/{token}/{slug}", ["GET", "HEAD"], (string token, string slug, HttpContext context, IShareCoordinator coordinator, global::InstantFileShare.Agent.PowerManagementService power, CancellationToken cancellationToken) =>
    HandleDownloadAsync(token, context, coordinator, power, cancellationToken));

await app.RunAsync();

static async Task<IResult> HandleDownloadAsync(
    string token,
    HttpContext context,
    IShareCoordinator coordinator,
    global::InstantFileShare.Agent.PowerManagementService powerManagementService,
    CancellationToken cancellationToken)
{
    var share = await coordinator.ResolveDownloadAsync(token, cancellationToken);
    if (share is null)
    {
        return Results.NotFound();
    }

    if (share.State == ShareState.Revoked)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    if (share.State == ShareState.Expired)
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    if (share.State == ShareState.Broken)
    {
        return Results.Problem(share.BrokenReason ?? "The shared file is unavailable.", statusCode: StatusCodes.Status410Gone);
    }

    var file = new FileInfo(share.FilePath);
    if (!file.Exists)
    {
        return Results.NotFound();
    }

    var settings = await coordinator.GetSettingsAsync(cancellationToken);

    var isHead = HttpMethods.IsHead(context.Request.Method);

    if (isHead)
    {
        context.Response.ContentLength = file.Length;
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{share.FileName}\"";
        return Results.Empty;
    }

    var remoteAddress = context.Connection.RemoteIpAddress?.ToString();
    var countsTowardUsage = !context.Request.Headers.ContainsKey("Range");
    if (settings.KeepAwakeWhileTransferring)
    {
        powerManagementService.NotifyTransferStarted();
    }

    var rawStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var meteredStream = new global::InstantFileShare.Agent.MeteredReadStream(rawStream, settings.BandwidthLimitBytesPerSecond);
    context.Response.OnCompleted(async () =>
    {
        try
        {
            await coordinator.MarkTransferCompletedAsync(
                share.Id,
                share.Token,
                share.FileName,
                remoteAddress,
                meteredStream.BytesRead,
                file.Length,
                countsTowardUsage,
                countsTowardUsage ? null : "Range response does not increment share usage.",
                CancellationToken.None);
        }
        finally
        {
            meteredStream.Dispose();
            if (settings.KeepAwakeWhileTransferring)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }
    });

    return Results.File(meteredStream, fileDownloadName: share.FileName, enableRangeProcessing: true);
}
