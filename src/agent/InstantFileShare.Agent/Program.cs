using System.Net;
using System.Net.WebSockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantFileShare.Agent;
using InstantFileShare.Core;
using InstantFileShare.Data;
using InstantFileShare.Infrastructure;

var databasePath = global::InstantFileShare.Agent.AgentPaths.GetDatabasePath();
var store = new SqliteShareStore(databasePath);
await store.InitializeAsync(CancellationToken.None);
var initialSettings = await store.GetSettingsAsync(CancellationToken.None);
var fileLogStore = new global::InstantFileShare.Infrastructure.FileLogStore(global::InstantFileShare.Agent.AgentPaths.GetLogsDirectory());
var launchDashboardRequested = args.Any(argument => string.Equals(argument, "--open-dashboard", StringComparison.OrdinalIgnoreCase));
var installerFirstRunRequested = args.Any(argument => string.Equals(argument, "--installer-first-run", StringComparison.OrdinalIgnoreCase));

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
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
    options.AddPolicy("DevelopmentCors", policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());

    options.AddPolicy("ReleaseCors", policy =>
        policy.SetIsOriginAllowed(origin => IsAllowedControlOrigin(origin))
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
app.UseCors(isDevelopment ? "DevelopmentCors" : "ReleaseCors");

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
jsonOptions.Converters.Add(new JsonStringEnumConverter());

app.Services.GetRequiredService<IStartupRegistrationService>().Apply(initialSettings.StartOnLogin);
await app.Services.GetRequiredService<IContextMenuRegistrationService>().ApplyAsync(initialSettings, CancellationToken.None);

var notifications = app.Services.GetRequiredService<global::InstantFileShare.Agent.NotificationService>();
notifications.OpenDashboardRequested += () => app.Services.GetRequiredService<global::InstantFileShare.Agent.DashboardLauncher>().Launch(global::InstantFileShare.Agent.AgentPaths.GetRepositoryRoot());
var shareCoordinator = app.Services.GetRequiredService<IShareCoordinator>();

try
{
    await shareCoordinator.DetectCloudflaredAsync(CancellationToken.None);
}
catch
{
    // Detection is best-effort during startup. Share creation will retry on demand.
}

if (installerFirstRunRequested)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await shareCoordinator.EnsureInstallerFirstRunTunnelAsync(app.Lifetime.ApplicationStopping);
            }
            catch
            {
                // First-run tunnel setup is best-effort so the app is usable immediately after install.
            }
        });
    });
}

if (initialSettings.OpenDashboardOnStart || launchDashboardRequested)
{
    await app.Services.GetRequiredService<global::InstantFileShare.Agent.DashboardLauncher>()
        .Launch(global::InstantFileShare.Agent.AgentPaths.GetRepositoryRoot());
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

app.MapMethods("/s/{token}/{slug}/{**path}", ["GET", "HEAD"], (string token, string slug, string path, HttpContext context, IShareCoordinator coordinator, global::InstantFileShare.Agent.PowerManagementService power, CancellationToken cancellationToken) =>
    HandlePublicShareAsync(token, slug, path, context, coordinator, power, cancellationToken));
app.MapMethods("/s/{token}/{slug}", ["GET", "HEAD"], (string token, string slug, HttpContext context, IShareCoordinator coordinator, global::InstantFileShare.Agent.PowerManagementService power, CancellationToken cancellationToken) =>
    HandlePublicShareAsync(token, slug, null, context, coordinator, power, cancellationToken));
app.MapMethods("/s/{token}", ["GET", "HEAD"], (string token, HttpContext context, IShareCoordinator coordinator, global::InstantFileShare.Agent.PowerManagementService power, CancellationToken cancellationToken) =>
    HandlePublicShareAsync(token, null, null, context, coordinator, power, cancellationToken));

await app.RunAsync();

async Task<IResult> HandlePublicShareAsync(
    string token,
    string? slugOrArchive,
    string? relativePath,
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

    var unavailableResult = ResolveUnavailableShareResult(share);
    if (unavailableResult is not null)
    {
        return unavailableResult;
    }

    var settings = await coordinator.GetSettingsAsync(cancellationToken);

    if (share.ShareKind == ShareKind.File)
    {
        if (!string.IsNullOrEmpty(relativePath))
        {
            return Results.NotFound();
        }

        return await HandlePhysicalFileDownloadAsync(
            context,
            coordinator,
            powerManagementService,
            settings,
            share,
            share.FilePath,
            transferFileName: share.FileName,
            responseFileName: share.FileName,
            transferKind: TransferKind.FileDownload,
            allowRangeRequests: true,
            usageSessionKey: null,
            cancellationToken);
    }

    var slug = share.Slug ?? FileNameSlug.Create(share.FileName, stripExtension: false);
    if (string.IsNullOrWhiteSpace(slug))
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(slugOrArchive))
    {
        return Results.Redirect(ShareUrlBuilder.Build(share), permanent: false);
    }

    var zipSegment = $"{slug}.zip";
    if (string.IsNullOrEmpty(relativePath) && string.Equals(slugOrArchive, zipSegment, StringComparison.OrdinalIgnoreCase))
    {
        if (!share.CanDownloadFolderAsZip)
        {
            return Results.NotFound();
        }

        return await HandleFolderZipDownloadAsync(context, coordinator, powerManagementService, settings, share, cancellationToken);
    }

    if (!string.Equals(slugOrArchive, slug, StringComparison.OrdinalIgnoreCase) || !share.CanBrowseFolderContents)
    {
        return Results.NotFound();
    }

    if (!FolderSharePathResolver.TryResolveEntry(share.FilePath, relativePath, out var resolvedEntry) || resolvedEntry is null)
    {
        return Results.NotFound();
    }

    return resolvedEntry.IsDirectory
        ? await HandleFolderBrowseDirectoryAsync(context, coordinator, settings, share, resolvedEntry, cancellationToken)
        : await HandlePhysicalFileDownloadAsync(
            context,
            coordinator,
            powerManagementService,
            settings,
            share,
            resolvedEntry.FullPath,
            transferFileName: string.IsNullOrEmpty(resolvedEntry.RelativePath) ? resolvedEntry.Name : resolvedEntry.RelativePath.Replace('/', '\\'),
            responseFileName: resolvedEntry.Name,
            transferKind: TransferKind.FolderFileDownload,
            allowRangeRequests: true,
            usageSessionKey: null,
            cancellationToken);
}

IResult? ResolveUnavailableShareResult(ShareRecord share)
{
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

    return null;
}

async Task<IResult> HandlePhysicalFileDownloadAsync(
    HttpContext context,
    IShareCoordinator coordinator,
    global::InstantFileShare.Agent.PowerManagementService powerManagementService,
    AppSettings settings,
    ShareRecord share,
    string physicalPath,
    string transferFileName,
    string responseFileName,
    TransferKind transferKind,
    bool allowRangeRequests,
    string? usageSessionKey,
    CancellationToken cancellationToken)
{
    var file = new FileInfo(physicalPath);
    if (!file.Exists)
    {
        return Results.NotFound();
    }

    var isHead = HttpMethods.IsHead(context.Request.Method);
    var crawlerName = ResolveMetadataCrawlerName(context.Request);
    var isMetadataPreview = crawlerName is not null;
    var fileResponseMetadata = ShareFileResponsePolicy.Resolve(responseFileName, settings);
    var remoteAddress = ResolveClientIpAddress(context);
    var userAgent = context.Request.Headers.UserAgent.ToString();

    if (isMetadataPreview && settings.SendMetadataToCrawlers)
    {
        var previewTransfer = await coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            transferFileName,
            TransferKind.MetadataPreview,
            null,
            BuildClientFingerprint(remoteAddress, userAgent),
            remoteAddress,
            0,
            0,
            crawlerName,
            cancellationToken);
        await coordinator.MarkTransferCompletedAsync(
            previewTransfer.Id,
            share.Id,
            share.Token,
            transferFileName,
            TransferKind.MetadataPreview,
            remoteAddress,
            0,
            0,
            false,
            true,
            false,
            null,
            null,
            crawlerName,
            cancellationToken);

        var metadataHtml = BuildFileShareMetadataHtml(context, share, responseFileName, file, fileResponseMetadata);

        if (isHead)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(metadataHtml);
            return Results.Empty;
        }

        return Results.Content(metadataHtml, "text/html; charset=utf-8");
    }

    if (isHead)
    {
        context.Response.ContentLength = file.Length;
        ApplyFileResponseHeaders(context.Response, responseFileName, fileResponseMetadata);
        return Results.Empty;
    }

    var (clientSessionId, setCookie) = ResolveDownloadSession(context, share.Token);
    var clientFingerprint = BuildClientFingerprint(remoteAddress, userAgent);
    var requestedRange = allowRangeRequests ? context.Request.GetTypedHeaders().Range?.Ranges.FirstOrDefault() : null;
    var initialBytesSent = requestedRange?.From ?? 0;
    var countsTowardUsage = !context.Request.Headers.ContainsKey("Range");
    usageSessionKey ??= transferKind == TransferKind.FolderFileDownload ? clientSessionId : null;
    var transfer = await coordinator.StartTransferAsync(
        share.Id,
        share.Token,
        transferFileName,
        transferKind,
        clientSessionId,
        clientFingerprint,
        remoteAddress,
        file.Length,
        initialBytesSent,
        null,
        cancellationToken);
    if (settings.KeepAwakeWhileTransferring)
    {
        powerManagementService.NotifyTransferStarted();
    }

    var rawStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var meteredStream = new global::InstantFileShare.Agent.MeteredReadStream(rawStream, settings.BandwidthLimitBytesPerSecond);
    var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var progressTask = TrackTransferProgressAsync(coordinator, transfer.Id, meteredStream, initialBytesSent, progressCancellation.Token);
    context.Response.OnCompleted(async () =>
    {
        try
        {
            progressCancellation.Cancel();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
            }

            var bytesSent = initialBytesSent + meteredStream.BytesRead;
            var reachedEnd = bytesSent >= file.Length;
            var requestAborted = context.RequestAborted.IsCancellationRequested;
            var paused = requestAborted && bytesSent > initialBytesSent && bytesSent < file.Length;
            var succeeded = reachedEnd && !requestAborted;
            var error = paused
                ? null
                : succeeded
                    ? null
                    : "Connection closed before the transfer completed.";

            await coordinator.MarkTransferCompletedAsync(
                transfer.Id,
                share.Id,
                share.Token,
                transferFileName,
                transferKind,
                remoteAddress,
                bytesSent,
                file.Length,
                paused,
                succeeded,
                countsTowardUsage,
                usageSessionKey,
                error,
                null,
                CancellationToken.None);
        }
        finally
        {
            progressCancellation.Dispose();
            meteredStream.Dispose();
            if (settings.KeepAwakeWhileTransferring)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }
    });

    if (setCookie)
    {
        context.Response.Cookies.Append(
            GetDownloadSessionCookieName(share.Token),
            clientSessionId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Path = $"/s/{share.Token}",
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
            });
    }

    ApplyFileResponseHeaders(context.Response, responseFileName, fileResponseMetadata);
    return Results.File(meteredStream, contentType: fileResponseMetadata.ContentType, enableRangeProcessing: allowRangeRequests);
}

async Task<IResult> HandleFolderBrowseDirectoryAsync(
    HttpContext context,
    IShareCoordinator coordinator,
    AppSettings settings,
    ShareRecord share,
    FolderSharePathResolver.ResolvedEntry directoryEntry,
    CancellationToken cancellationToken)
{
    var entries = FolderSharePathResolver.ListDirectory(directoryEntry);
    var html = BuildFolderBrowseHtml(context, share, directoryEntry, entries);
    var crawlerName = ResolveMetadataCrawlerName(context.Request);
    if (crawlerName is not null && settings.SendMetadataToCrawlers)
    {
        var remoteAddress = ResolveClientIpAddress(context);
        var previewTransfer = await coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            string.IsNullOrEmpty(directoryEntry.RelativePath) ? share.FileName : directoryEntry.RelativePath.Replace('/', '\\'),
            TransferKind.MetadataPreview,
            null,
            BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString()),
            remoteAddress,
            0,
            0,
            crawlerName,
            cancellationToken);
        await coordinator.MarkTransferCompletedAsync(
            previewTransfer.Id,
            share.Id,
            share.Token,
            string.IsNullOrEmpty(directoryEntry.RelativePath) ? share.FileName : directoryEntry.RelativePath.Replace('/', '\\'),
            TransferKind.MetadataPreview,
            remoteAddress,
            0,
            0,
            false,
            true,
            false,
            null,
            null,
            crawlerName,
            cancellationToken);
    }

    if (HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
        return Results.Empty;
    }

    return Results.Content(html, "text/html; charset=utf-8");
}

async Task<IResult> HandleFolderZipDownloadAsync(
    HttpContext context,
    IShareCoordinator coordinator,
    global::InstantFileShare.Agent.PowerManagementService powerManagementService,
    AppSettings settings,
    ShareRecord share,
    CancellationToken cancellationToken)
{
    if (!FolderSharePathResolver.TryResolveEntry(share.FilePath, null, out var rootEntry) || rootEntry is null || !rootEntry.IsDirectory)
    {
        return Results.NotFound();
    }

    var zipFileName = $"{share.FileName}.zip";
    var crawlerName = ResolveMetadataCrawlerName(context.Request);
    var remoteAddress = ResolveClientIpAddress(context);
    var userAgent = context.Request.Headers.UserAgent.ToString();

    if (crawlerName is not null && settings.SendMetadataToCrawlers)
    {
        var previewTransfer = await coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            zipFileName,
            TransferKind.MetadataPreview,
            null,
            BuildClientFingerprint(remoteAddress, userAgent),
            remoteAddress,
            0,
            0,
            crawlerName,
            cancellationToken);
        await coordinator.MarkTransferCompletedAsync(
            previewTransfer.Id,
            share.Id,
            share.Token,
            zipFileName,
            TransferKind.MetadataPreview,
            remoteAddress,
            0,
            0,
            false,
            true,
            false,
            null,
            null,
            crawlerName,
            cancellationToken);

        var metadataHtml = BuildFolderZipMetadataHtml(context, share);
        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(metadataHtml);
            return Results.Empty;
        }

        return Results.Content(metadataHtml, "text/html; charset=utf-8");
    }

    if (HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.ContentType = "application/zip";
        context.Response.Headers["Content-Disposition"] = BuildContentDispositionHeader("attachment", zipFileName);
        return Results.Empty;
    }

    var manifest = BuildFolderZipManifest(rootEntry);
    var (clientSessionId, setCookie) = ResolveDownloadSession(context, share.Token);
    var clientFingerprint = BuildClientFingerprint(remoteAddress, userAgent);
    var transfer = await coordinator.StartTransferAsync(
        share.Id,
        share.Token,
        zipFileName,
        TransferKind.FolderZipDownload,
        clientSessionId,
        clientFingerprint,
        remoteAddress,
        0,
        0,
        null,
        cancellationToken);

    if (setCookie)
    {
        context.Response.Cookies.Append(
            GetDownloadSessionCookieName(share.Token),
            clientSessionId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Path = $"/s/{share.Token}",
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
            });
    }

    context.Response.ContentType = "application/zip";
    context.Response.Headers["Content-Disposition"] = BuildContentDispositionHeader("attachment", zipFileName);
    var bodyControlFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
    if (bodyControlFeature is not null)
    {
        bodyControlFeature.AllowSynchronousIO = true;
    }

    if (settings.KeepAwakeWhileTransferring)
    {
        powerManagementService.NotifyTransferStarted();
    }

    await context.Response.StartAsync(cancellationToken);

    await using var meteredStream = new global::InstantFileShare.Agent.MeteredWriteStream(context.Response.Body, settings.BandwidthLimitBytesPerSecond);
    var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var progressTask = TrackWriteTransferProgressAsync(coordinator, transfer.Id, meteredStream, progressCancellation.Token);

    var succeeded = false;
    string? error = null;
    try
    {
        using (var archive = new ZipArchive(meteredStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(item.FullPath))
                {
                    throw new FileNotFoundException("A file disappeared while the ZIP archive was being generated.", item.FullPath);
                }

                var archiveEntry = archive.CreateEntry(item.EntryPath, MapCompressionLevel(settings.FolderZipCompressionLevel));
                await using var archiveStream = archiveEntry.Open();
                await using var sourceStream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await sourceStream.CopyToAsync(archiveStream, cancellationToken);
            }
        }

        succeeded = !context.RequestAborted.IsCancellationRequested;
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        error = "Connection closed before the transfer completed.";
    }
    catch (Exception exception)
    {
        error = exception.Message;
    }
    finally
    {
        progressCancellation.Cancel();
        try
        {
            await progressTask;
        }
        catch (OperationCanceledException)
        {
        }

        await coordinator.MarkTransferCompletedAsync(
            transfer.Id,
            share.Id,
            share.Token,
            zipFileName,
            TransferKind.FolderZipDownload,
            remoteAddress,
            meteredStream.BytesWritten,
            meteredStream.BytesWritten,
            paused: false,
            succeeded,
            countsTowardUsage: true,
            usageSessionKey: null,
            error,
            null,
            CancellationToken.None);

        progressCancellation.Dispose();
        if (settings.KeepAwakeWhileTransferring)
        {
            powerManagementService.NotifyTransferEnded();
        }
    }

    return Results.Empty;
}

async Task TrackTransferProgressAsync(
    IShareCoordinator coordinator,
    string transferId,
    global::InstantFileShare.Agent.MeteredReadStream meteredStream,
    long initialBytesSent,
    CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    while (await timer.WaitForNextTickAsync(cancellationToken))
    {
        await coordinator.UpdateTransferProgressAsync(transferId, initialBytesSent + meteredStream.BytesRead, cancellationToken);
    }
}

async Task TrackWriteTransferProgressAsync(
    IShareCoordinator coordinator,
    string transferId,
    global::InstantFileShare.Agent.MeteredWriteStream meteredStream,
    CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    while (await timer.WaitForNextTickAsync(cancellationToken))
    {
        await coordinator.UpdateTransferProgressAsync(transferId, meteredStream.BytesWritten, cancellationToken);
    }
}

string? ResolveClientIpAddress(HttpContext context)
{
    var remoteIpAddress = context.Connection.RemoteIpAddress;
    if (remoteIpAddress is not null && !IPAddress.IsLoopback(remoteIpAddress))
    {
        return remoteIpAddress.ToString();
    }

    return TryResolveForwardedClientIp(context.Request.Headers) ?? remoteIpAddress?.ToString();
}

string? TryResolveForwardedClientIp(IHeaderDictionary headers)
{
    if (TryResolveHeaderIp(headers, "CF-Connecting-IP", out var cloudflareIp))
    {
        return cloudflareIp;
    }

    if (TryResolveHeaderIp(headers, "True-Client-IP", out var trueClientIp))
    {
        return trueClientIp;
    }

    if (headers.TryGetValue("X-Forwarded-For", out var forwardedForValues))
    {
        foreach (var forwardedForValue in forwardedForValues)
        {
            if (string.IsNullOrWhiteSpace(forwardedForValue))
            {
                continue;
            }

            var segments = forwardedForValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (TryNormalizeIpAddress(segment, out var forwardedIp))
                {
                    return forwardedIp;
                }
            }
        }
    }

    if (headers.TryGetValue("Forwarded", out var forwardedValues))
    {
        foreach (var forwardedValue in forwardedValues)
        {
            if (string.IsNullOrWhiteSpace(forwardedValue))
            {
                continue;
            }

            var entries = forwardedValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                var segments = entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var segment in segments)
                {
                    if (!segment.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidate = segment[4..].Trim().Trim('"');
                    if (TryNormalizeIpAddress(candidate, out var forwardedIp))
                    {
                        return forwardedIp;
                    }
                }
            }
        }
    }

    return null;
}

bool TryResolveHeaderIp(IHeaderDictionary headers, string headerName, out string? ipAddress)
{
    ipAddress = null;
    if (!headers.TryGetValue(headerName, out var headerValues))
    {
        return false;
    }

    foreach (var headerValue in headerValues)
    {
        if (TryNormalizeIpAddress(headerValue, out ipAddress))
        {
            return true;
        }
    }

    return false;
}

bool TryNormalizeIpAddress(string? rawValue, out string? ipAddress)
{
    ipAddress = null;
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        return false;
    }

    var candidate = rawValue.Trim().Trim('"');
    if (candidate.Length == 0 || candidate.Equals("unknown", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (candidate[0] == '[')
    {
        var closingBracketIndex = candidate.IndexOf(']');
        if (closingBracketIndex <= 1)
        {
            return false;
        }

        candidate = candidate[1..closingBracketIndex];
    }
    else
    {
        var colonIndex = candidate.LastIndexOf(':');
        if (colonIndex > 0 && candidate.IndexOf(':') == colonIndex)
        {
            candidate = candidate[..colonIndex];
        }
    }

    if (!IPAddress.TryParse(candidate, out var parsedIpAddress))
    {
        return false;
    }

    ipAddress = parsedIpAddress.ToString();
    return true;
}

(string SessionId, bool SetCookie) ResolveDownloadSession(HttpContext context, string token)
{
    var cookieName = GetDownloadSessionCookieName(token);
    if (context.Request.Cookies.TryGetValue(cookieName, out var existingSessionId) &&
        !string.IsNullOrWhiteSpace(existingSessionId))
    {
        return (existingSessionId, false);
    }

    return (Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), true);
}

string GetDownloadSessionCookieName(string token) => $"ifs-download-{token}";

string? BuildClientFingerprint(string? remoteAddress, string? userAgent)
{
    if (string.IsNullOrWhiteSpace(remoteAddress) || string.IsNullOrWhiteSpace(userAgent))
    {
        return null;
    }

    var payload = $"{remoteAddress}\n{userAgent.Trim()}";
    return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}

string? ResolveMetadataCrawlerName(HttpRequest request)
{
    if (request.Headers.ContainsKey("Range"))
    {
        return null;
    }

    var userAgent = request.Headers.UserAgent.ToString();
    if (string.IsNullOrWhiteSpace(userAgent))
    {
        return null;
    }

    var normalizedUserAgent = userAgent.ToLowerInvariant();
    if (normalizedUserAgent.Contains("discordbot", StringComparison.Ordinal))
    {
        return "Discordbot";
    }

    if (normalizedUserAgent.Contains("whatsapp", StringComparison.Ordinal))
    {
        return "WhatsApp";
    }

    if (normalizedUserAgent.Contains("facebookexternalhit", StringComparison.Ordinal))
    {
        return "Facebook";
    }

    if (normalizedUserAgent.Contains("twitterbot", StringComparison.Ordinal))
    {
        return "Twitterbot";
    }

    if (normalizedUserAgent.Contains("slackbot", StringComparison.Ordinal))
    {
        return "Slackbot";
    }

    if (normalizedUserAgent.Contains("linkedinbot", StringComparison.Ordinal))
    {
        return "LinkedIn";
    }

    if (normalizedUserAgent.Contains("telegrambot", StringComparison.Ordinal))
    {
        return "Telegram";
    }

    if (normalizedUserAgent.Contains("skypeuripreview", StringComparison.Ordinal))
    {
        return "Skype Preview";
    }

    if (normalizedUserAgent.Contains("googlebot", StringComparison.Ordinal))
    {
        return "Googlebot";
    }

    if (normalizedUserAgent.Contains("preview", StringComparison.Ordinal))
    {
        return "Link Preview";
    }

    return null;
}

void ApplyFileResponseHeaders(HttpResponse response, string fileName, ShareFileResponseMetadata metadata)
{
    response.ContentType = metadata.ContentType;
    response.Headers["Content-Disposition"] = BuildContentDispositionHeader(metadata.ContentDispositionType, fileName);
}

string BuildContentDispositionHeader(string dispositionType, string fileName)
{
    var escapedFileName = fileName
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    return $"{dispositionType}; filename=\"{escapedFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
}

IReadOnlyList<(string FullPath, string EntryPath)> BuildFolderZipManifest(FolderSharePathResolver.ResolvedEntry rootEntry)
{
    var manifest = new List<(string FullPath, string EntryPath)>();
    var pending = new Stack<FolderSharePathResolver.ResolvedEntry>();
    pending.Push(rootEntry);

    while (pending.Count > 0)
    {
        var current = pending.Pop();
        foreach (var child in FolderSharePathResolver.ListDirectory(current).Reverse())
        {
            if (!FolderSharePathResolver.TryResolveEntry(rootEntry.RootPath, child.RelativePath, out var resolvedChild) || resolvedChild is null)
            {
                continue;
            }

            if (resolvedChild.IsDirectory)
            {
                pending.Push(resolvedChild);
                continue;
            }

            manifest.Add((resolvedChild.FullPath, resolvedChild.RelativePath.Replace('\\', '/')));
        }
    }

    return manifest;
}

CompressionLevel MapCompressionLevel(FolderZipCompressionLevel compressionLevel)
{
    return compressionLevel switch
    {
        FolderZipCompressionLevel.Fastest => CompressionLevel.Fastest,
        FolderZipCompressionLevel.NoCompression => CompressionLevel.NoCompression,
        FolderZipCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}

string BuildFileShareMetadataHtml(HttpContext context, ShareRecord share, string responseFileName, FileInfo file, ShareFileResponseMetadata fileResponseMetadata)
{
    var actionVerb = fileResponseMetadata.PreferInline ? "View" : "Download";
    var description = $"{actionVerb} {responseFileName} ({FormatFileSize(file.Length)}). Shared via Instant File Share.";
    var actionLabel = fileResponseMetadata.PreferInline
        ? $"Open this link to view {responseFileName} in your browser or download it."
        : $"Open this link to download {responseFileName}.";

    return BuildShellHtml(context, responseFileName, description, (builder) =>
    {
        builder.AppendLine($"      <p>{WebUtility.HtmlEncode(description)}</p>");
        builder.AppendLine($"      <p style=\"margin-top: 0.9rem;\">{WebUtility.HtmlEncode(actionLabel)}</p>");
    });
}

string BuildFolderZipMetadataHtml(HttpContext context, ShareRecord share)
{
    var description = $"Download a ZIP archive of {share.FileName}. Shared via Instant File Share.";
    return BuildShellHtml(context, share.FileName, description, (builder) =>
    {
        builder.AppendLine($"      <p>{WebUtility.HtmlEncode(description)}</p>");
    });
}

string BuildFolderBrowseHtml(
    HttpContext context,
    ShareRecord share,
    FolderSharePathResolver.ResolvedEntry directoryEntry,
    IReadOnlyList<FolderSharePathResolver.DirectoryEntry> entries)
{
    var title = string.IsNullOrEmpty(directoryEntry.RelativePath)
        ? share.FileName
        : $"{share.FileName} / {directoryEntry.RelativePath.Replace('/', '\\')}";
    var description = $"Browse {share.FileName}. Shared via Instant File Share.";
    var browseRootPath = $"/s/{share.Token}/{Uri.EscapeDataString(share.Slug ?? string.Empty)}";
    var currentRelativePath = directoryEntry.RelativePath;
    var showDownloadAll = share.CanBrowseFolderContents && share.CanDownloadFolderAsZip;
    var titleActionHtml = showDownloadAll
        ? $"<a class=\"secondary-action\" href=\"{WebUtility.HtmlEncode($"{browseRootPath}.zip")}\"><span aria-hidden=\"true\">&#x2B07;</span><span>Download All</span></a>"
        : null;

    return BuildShellHtml(context, title, description, (builder) =>
    {
        if (!string.IsNullOrEmpty(currentRelativePath))
        {
            builder.AppendLine("      <nav class=\"breadcrumbs\">");
            builder.AppendLine($"        <a href=\"{WebUtility.HtmlEncode(browseRootPath)}\">{WebUtility.HtmlEncode(share.FileName)}</a>");
            var breadcrumbPath = string.Empty;
            foreach (var segment in currentRelativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                breadcrumbPath = string.IsNullOrEmpty(breadcrumbPath) ? segment : $"{breadcrumbPath}/{segment}";
                builder.AppendLine("        <span>/</span>");
                builder.AppendLine($"        <a href=\"{WebUtility.HtmlEncode($"{browseRootPath}/{EncodeRelativePath(breadcrumbPath)}")}\">{WebUtility.HtmlEncode(segment)}</a>");
            }

            builder.AppendLine("      </nav>");
        }
        builder.AppendLine("      <table class=\"folder-table\">");
        builder.AppendLine("        <thead><tr><th>Name</th><th>Modified</th><th>Size</th></tr></thead>");
        builder.AppendLine("        <tbody>");

        if (!string.IsNullOrEmpty(currentRelativePath))
        {
            var parentPath = currentRelativePath.Contains('/')
                ? currentRelativePath[..currentRelativePath.LastIndexOf('/')]
                : string.Empty;
            var parentHref = string.IsNullOrEmpty(parentPath) ? browseRootPath : $"{browseRootPath}/{EncodeRelativePath(parentPath)}";
            builder.AppendLine($"          <tr><td><a href=\"{WebUtility.HtmlEncode(parentHref)}\">..</a></td><td></td><td></td></tr>");
        }

        foreach (var entry in entries)
        {
            var href = $"{browseRootPath}/{EncodeRelativePath(entry.RelativePath)}";
            builder.AppendLine("          <tr>");
            builder.AppendLine($"            <td><a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(entry.Name)}{(entry.IsDirectory ? "/" : string.Empty)}</a></td>");
            builder.AppendLine($"            <td>{WebUtility.HtmlEncode(entry.LastModifiedAtUtc.ToLocalTime().ToString("g"))}</td>");
            builder.AppendLine($"            <td>{(entry.IsDirectory ? string.Empty : WebUtility.HtmlEncode(FormatFileSize(entry.Size)))}</td>");
            builder.AppendLine("          </tr>");
        }

        if (entries.Count == 0)
        {
            builder.AppendLine("          <tr><td colspan=\"3\">This folder is empty.</td></tr>");
        }

        builder.AppendLine("        </tbody>");
        builder.AppendLine("      </table>");
    }, titleActionHtml);
}

string BuildShellHtml(HttpContext context, string title, string description, Action<StringBuilder> bodyBuilder, string? titleActionHtml = null)
{
    var currentUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";
    var encodedTitle = WebUtility.HtmlEncode(title);
    var encodedDescription = WebUtility.HtmlEncode(description);
    var encodedUrl = WebUtility.HtmlEncode(currentUrl);

    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("  <head>");
    html.AppendLine("    <meta charset=\"utf-8\" />");
    html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
    html.AppendLine($"    <title>{encodedTitle}</title>");
    html.AppendLine($"    <meta name=\"description\" content=\"{encodedDescription}\" />");
    html.AppendLine("    <meta name=\"robots\" content=\"noindex, nofollow\" />");
    html.AppendLine($"    <meta property=\"og:title\" content=\"{encodedTitle}\" />");
    html.AppendLine($"    <meta property=\"og:description\" content=\"{encodedDescription}\" />");
    html.AppendLine("    <meta property=\"og:type\" content=\"website\" />");
    html.AppendLine($"    <meta property=\"og:url\" content=\"{encodedUrl}\" />");
    html.AppendLine("    <meta property=\"og:site_name\" content=\"Instant File Share\" />");
    html.AppendLine("    <meta name=\"twitter:card\" content=\"summary\" />");
    html.AppendLine($"    <meta name=\"twitter:title\" content=\"{encodedTitle}\" />");
    html.AppendLine($"    <meta name=\"twitter:description\" content=\"{encodedDescription}\" />");
    html.AppendLine("    <style>");
    html.AppendLine("      body { font-family: Segoe UI, Arial, sans-serif; background: #101112; color: #f3efe7; padding: 2rem; }");
    html.AppendLine("      .card { max-width: 880px; margin: 0 auto; padding: 1.5rem; border-radius: 18px; background: #1b1f24; border: 1px solid rgba(255,255,255,0.08); }");
    html.AppendLine("      .eyebrow-row { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; flex-wrap: wrap; }");
    html.AppendLine("      .eyebrow { color: #ffb57d; text-transform: uppercase; letter-spacing: 0.12em; font-size: 0.72rem; margin: 0; }");
    html.AppendLine("      h1 { margin: 0.35rem 0 0.75rem; font-size: 1.7rem; }");
    html.AppendLine("      p, td, th, a, span { color: #d4d0ca; }");
    html.AppendLine("      a { color: #ffd3ad; text-decoration: none; }");
    html.AppendLine("      a:hover { text-decoration: underline; }");
    html.AppendLine("      .secondary-action { display: inline-flex; align-items: center; gap: 0.35rem; padding: 0.32rem 0.62rem; border-radius: 999px; background: #2f4f68; color: #e7f2fb; font-size: 0.84rem; font-weight: 600; line-height: 1; }");
    html.AppendLine("      .secondary-action:hover { text-decoration: none; background: #3a637f; }");
    html.AppendLine("      .breadcrumbs { display: flex; gap: 0.45rem; flex-wrap: wrap; margin: 1.2rem 0; }");
    html.AppendLine("      .folder-table { width: 100%; border-collapse: collapse; margin-top: 1rem; }");
    html.AppendLine("      .folder-table th, .folder-table td { padding: 0.7rem 0.4rem; border-bottom: 1px solid rgba(255,255,255,0.08); text-align: left; }");
    html.AppendLine("    </style>");
    html.AppendLine("  </head>");
    html.AppendLine("  <body>");
    html.AppendLine("    <main class=\"card\">");
    html.AppendLine("      <div class=\"eyebrow-row\">");
    html.AppendLine("        <p class=\"eyebrow\">Instant File Share</p>");
    if (!string.IsNullOrWhiteSpace(titleActionHtml))
    {
        html.AppendLine($"        {titleActionHtml}");
    }
    html.AppendLine("      </div>");
    html.AppendLine($"      <h1>{encodedTitle}</h1>");
    bodyBuilder(html);
    html.AppendLine("    </main>");
    html.AppendLine("  </body>");
    html.AppendLine("</html>");
    return html.ToString();
}

string EncodeRelativePath(string relativePath)
{
    return string.Join('/', relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}

string FormatFileSize(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double size = bytes;
    var unitIndex = 0;

    while (size >= 1024 && unitIndex < units.Length - 1)
    {
        size /= 1024;
        unitIndex++;
    }

    return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
}

bool IsAllowedControlOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return false;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
