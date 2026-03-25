using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
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
        policy.SetIsOriginAllowed(static origin => IsAllowedControlOrigin(origin))
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

if (initialSettings.OpenDashboardOnStart)
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
    var isMetadataPreview = IsMetadataPreviewRequest(context.Request);

    if (isMetadataPreview)
    {
        var metadataHtml = BuildShareMetadataHtml(context, share, file);

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
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{share.FileName}\"";
        return Results.Empty;
    }

    var remoteAddress = ResolveClientIpAddress(context);
    var (clientSessionId, setCookie) = ResolveDownloadSession(context, token);
    var clientFingerprint = BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString());
    var requestedRange = context.Request.GetTypedHeaders().Range?.Ranges.FirstOrDefault();
    var initialBytesSent = requestedRange?.From ?? 0;
    var countsTowardUsage = !context.Request.Headers.ContainsKey("Range");
    var transfer = await coordinator.StartTransferAsync(
        share.Id,
        share.Token,
        share.FileName,
        clientSessionId,
        clientFingerprint,
        remoteAddress,
        file.Length,
        initialBytesSent,
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
                share.FileName,
                remoteAddress,
                bytesSent,
                file.Length,
                paused,
                succeeded,
                countsTowardUsage,
                error,
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
            GetDownloadSessionCookieName(token),
            clientSessionId,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Path = $"/s/{token}",
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
            });
    }

    return Results.File(meteredStream, fileDownloadName: share.FileName, enableRangeProcessing: true);
}

static async Task TrackTransferProgressAsync(
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

static string? ResolveClientIpAddress(HttpContext context)
{
    var remoteIpAddress = context.Connection.RemoteIpAddress;
    if (remoteIpAddress is not null && !IPAddress.IsLoopback(remoteIpAddress))
    {
        return remoteIpAddress.ToString();
    }

    return TryResolveForwardedClientIp(context.Request.Headers) ?? remoteIpAddress?.ToString();
}

static string? TryResolveForwardedClientIp(IHeaderDictionary headers)
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

static bool TryResolveHeaderIp(IHeaderDictionary headers, string headerName, out string? ipAddress)
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

static bool TryNormalizeIpAddress(string? rawValue, out string? ipAddress)
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

static (string SessionId, bool SetCookie) ResolveDownloadSession(HttpContext context, string token)
{
    var cookieName = GetDownloadSessionCookieName(token);
    if (context.Request.Cookies.TryGetValue(cookieName, out var existingSessionId) &&
        !string.IsNullOrWhiteSpace(existingSessionId))
    {
        return (existingSessionId, false);
    }

    return (Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), true);
}

static string GetDownloadSessionCookieName(string token) => $"ifs-download-{token}";

static string? BuildClientFingerprint(string? remoteAddress, string? userAgent)
{
    if (string.IsNullOrWhiteSpace(remoteAddress) || string.IsNullOrWhiteSpace(userAgent))
    {
        return null;
    }

    var payload = $"{remoteAddress}\n{userAgent.Trim()}";
    return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}

static bool IsMetadataPreviewRequest(HttpRequest request)
{
    if (request.Headers.ContainsKey("Range"))
    {
        return false;
    }

    var userAgent = request.Headers.UserAgent.ToString();
    if (string.IsNullOrWhiteSpace(userAgent))
    {
        return false;
    }

    var normalizedUserAgent = userAgent.ToLowerInvariant();
    return normalizedUserAgent.Contains("discordbot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("whatsapp", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("facebookexternalhit", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("twitterbot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("slackbot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("linkedinbot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("telegrambot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("skypeuripreview", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("googlebot", StringComparison.Ordinal) ||
           normalizedUserAgent.Contains("preview", StringComparison.Ordinal);
}

static string BuildShareMetadataHtml(HttpContext context, ShareRecord share, FileInfo file)
{
    var currentUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";
    var fileName = WebUtility.HtmlEncode(share.FileName);
    var description = WebUtility.HtmlEncode($"Download {share.FileName} ({FormatFileSize(file.Length)}) shared via Instant File Share.");
    var encodedUrl = WebUtility.HtmlEncode(currentUrl);
    var downloadLabel = WebUtility.HtmlEncode($"Open this link to download {share.FileName}.");

    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("  <head>");
    html.AppendLine("    <meta charset=\"utf-8\" />");
    html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
    html.AppendLine($"    <title>{fileName}</title>");
    html.AppendLine($"    <meta name=\"description\" content=\"{description}\" />");
    html.AppendLine("    <meta name=\"robots\" content=\"noindex, nofollow\" />");
    html.AppendLine($"    <meta property=\"og:title\" content=\"{fileName}\" />");
    html.AppendLine($"    <meta property=\"og:description\" content=\"{description}\" />");
    html.AppendLine("    <meta property=\"og:type\" content=\"website\" />");
    html.AppendLine($"    <meta property=\"og:url\" content=\"{encodedUrl}\" />");
    html.AppendLine("    <meta property=\"og:site_name\" content=\"Instant File Share\" />");
    html.AppendLine("    <meta name=\"twitter:card\" content=\"summary\" />");
    html.AppendLine($"    <meta name=\"twitter:title\" content=\"{fileName}\" />");
    html.AppendLine($"    <meta name=\"twitter:description\" content=\"{description}\" />");
    html.AppendLine("    <style>");
    html.AppendLine("      body { font-family: Segoe UI, Arial, sans-serif; background: #101112; color: #f3efe7; padding: 2rem; }");
    html.AppendLine("      .card { max-width: 720px; margin: 0 auto; padding: 1.5rem; border-radius: 18px; background: #1b1f24; border: 1px solid rgba(255,255,255,0.08); }");
    html.AppendLine("      .eyebrow { color: #ffb57d; text-transform: uppercase; letter-spacing: 0.12em; font-size: 0.72rem; }");
    html.AppendLine("      h1 { margin: 0.35rem 0 0.75rem; font-size: 1.7rem; }");
    html.AppendLine("      p { margin: 0; color: #d4d0ca; }");
    html.AppendLine("    </style>");
    html.AppendLine("  </head>");
    html.AppendLine("  <body>");
    html.AppendLine("    <main class=\"card\">");
    html.AppendLine("      <p class=\"eyebrow\">Instant File Share</p>");
    html.AppendLine($"      <h1>{fileName}</h1>");
    html.AppendLine($"      <p>{description}</p>");
    html.AppendLine($"      <p style=\"margin-top: 0.9rem;\">{downloadLabel}</p>");
    html.AppendLine("    </main>");
    html.AppendLine("  </body>");
    html.AppendLine("</html>");
    return html.ToString();
}

static string FormatFileSize(long bytes)
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

static bool IsAllowedControlOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return false;
    }

    if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
