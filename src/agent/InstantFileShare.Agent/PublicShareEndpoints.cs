using System.IO.Compression;
using System.Text;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal static class PublicShareEndpoints
{
    public static IEndpointRouteBuilder MapPublicShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
            "/s/{token}/{slug}/{**path}",
            ["GET", "HEAD"],
            (string token, string slug, string path, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, slug, path, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, cancellationToken));

        endpoints.MapMethods(
            "/s/{token}/{slug}",
            ["GET", "HEAD"],
            (string token, string slug, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, slug, null, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, cancellationToken));

        endpoints.MapMethods(
            "/s/{token}",
            ["GET", "HEAD"],
            (string token, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, null, null, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, cancellationToken));

        return endpoints;
    }

    private static async Task<IResult> HandlePublicShareAsync(
        string token,
        string? slugOrArchive,
        string? relativePath,
        HttpContext context,
        IShareCoordinator coordinator,
        PowerManagementService powerManagementService,
        PublicSharePageModelFactory pageModelFactory,
        PublicShareHtmlRenderer htmlRenderer,
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
                pageModelFactory,
                htmlRenderer,
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

            return await HandleFolderZipDownloadAsync(
                context,
                coordinator,
                powerManagementService,
                settings,
                share,
                new FolderSharePathResolver.ResolvedEntry(
                    share.FilePath,
                    share.FilePath,
                    string.Empty,
                    true,
                    share.FileName,
                    0,
                    DateTimeOffset.UtcNow),
                pageModelFactory,
                htmlRenderer,
                cancellationToken);
        }

        if (!string.Equals(slugOrArchive, slug, StringComparison.OrdinalIgnoreCase) || !share.CanBrowseFolderContents)
        {
            return Results.NotFound();
        }

        if (!FolderSharePathResolver.TryResolveEntry(share.FilePath, relativePath, out var resolvedEntry) || resolvedEntry is null)
        {
            return Results.NotFound();
        }

        if (resolvedEntry.IsDirectory && IsCurrentDirectoryZipRequest(context.Request))
        {
            if (!share.CanDownloadFolderAsZip)
            {
                return Results.NotFound();
            }

            return await HandleFolderZipDownloadAsync(
                context,
                coordinator,
                powerManagementService,
                settings,
                share,
                resolvedEntry,
                pageModelFactory,
                htmlRenderer,
                cancellationToken);
        }

        return resolvedEntry.IsDirectory
            ? await HandleFolderBrowseDirectoryAsync(context, coordinator, settings, share, resolvedEntry, pageModelFactory, htmlRenderer, cancellationToken)
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
                pageModelFactory,
                htmlRenderer,
                cancellationToken);
    }

    private static IResult? ResolveUnavailableShareResult(ShareRecord share)
    {
        if (share.State == ShareState.Revoked || share.State == ShareState.Expired)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        if (share.State == ShareState.Broken)
        {
            return Results.Problem(share.BrokenReason ?? "The shared file is unavailable.", statusCode: StatusCodes.Status410Gone);
        }

        return null;
    }

    private static async Task<IResult> HandlePhysicalFileDownloadAsync(
        HttpContext context,
        IShareCoordinator coordinator,
        PowerManagementService powerManagementService,
        AppSettings settings,
        ShareRecord share,
        string physicalPath,
        string transferFileName,
        string responseFileName,
        TransferKind transferKind,
        bool allowRangeRequests,
        string? usageSessionKey,
        PublicSharePageModelFactory pageModelFactory,
        PublicShareHtmlRenderer htmlRenderer,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(physicalPath);
        if (!file.Exists)
        {
            return Results.NotFound();
        }

        var isHead = HttpMethods.IsHead(context.Request.Method);
        var crawlerName = MetadataCrawlerDetector.ResolveMetadataCrawlerName(context.Request);
        var fileResponseMetadata = ShareFileResponsePolicy.Resolve(responseFileName, settings);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var userAgent = context.Request.Headers.UserAgent.ToString();

        if (crawlerName is not null && settings.SendMetadataToCrawlers)
        {
            var previewTransfer = await coordinator.StartTransferAsync(
                share.Id,
                share.Token,
                transferFileName,
                TransferKind.MetadataPreview,
                null,
                RequestAddressResolver.BuildClientFingerprint(remoteAddress, userAgent),
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
                paused: false,
                succeeded: true,
                countsTowardUsage: false,
                usageSessionKey: null,
                error: null,
                requesterName: crawlerName,
                cancellationToken);

            var pageModel = pageModelFactory.BuildFileMetadataPage(context, share, responseFileName, file, fileResponseMetadata);
            if (!htmlRenderer.TryRender(pageModel, out var metadataHtml, out var renderError))
            {
                return Results.Problem(renderError, statusCode: StatusCodes.Status500InternalServerError);
            }

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
            ResponseHeaderWriter.ApplyFileResponseHeaders(context.Response, responseFileName, fileResponseMetadata);
            return Results.Empty;
        }

        var (clientSessionId, setCookie) = DownloadSessionManager.ResolveDownloadSession(context, share.Token);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, userAgent);
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
            requesterName: null,
            cancellationToken);

        if (settings.KeepAwakeWhileTransferring)
        {
            powerManagementService.NotifyTransferStarted();
        }

        var rawStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var meteredStream = new MeteredReadStream(rawStream, settings.BandwidthLimitBytesPerSecond);
        var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var progressTask = TransferProgressTracker.TrackReadProgressAsync(coordinator, transfer.Id, meteredStream, initialBytesSent, progressCancellation.Token);

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
                var error = paused || succeeded
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
                    requesterName: null,
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
                DownloadSessionManager.GetCookieName(share.Token),
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

        ResponseHeaderWriter.ApplyFileResponseHeaders(context.Response, responseFileName, fileResponseMetadata);
        return Results.File(meteredStream, contentType: fileResponseMetadata.ContentType, enableRangeProcessing: allowRangeRequests);
    }

    private static async Task<IResult> HandleFolderBrowseDirectoryAsync(
        HttpContext context,
        IShareCoordinator coordinator,
        AppSettings settings,
        ShareRecord share,
        FolderSharePathResolver.ResolvedEntry directoryEntry,
        PublicSharePageModelFactory pageModelFactory,
        PublicShareHtmlRenderer htmlRenderer,
        CancellationToken cancellationToken)
    {
        var entries = FolderSharePathResolver.ListDirectory(directoryEntry);
        var crawlerName = MetadataCrawlerDetector.ResolveMetadataCrawlerName(context.Request);
        if (crawlerName is not null && settings.SendMetadataToCrawlers)
        {
            var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
            var previewTransfer = await coordinator.StartTransferAsync(
                share.Id,
                share.Token,
                string.IsNullOrEmpty(directoryEntry.RelativePath) ? share.FileName : directoryEntry.RelativePath.Replace('/', '\\'),
                TransferKind.MetadataPreview,
                null,
                RequestAddressResolver.BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString()),
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
                paused: false,
                succeeded: true,
                countsTowardUsage: false,
                usageSessionKey: null,
                error: null,
                requesterName: crawlerName,
                cancellationToken);
        }

        var pageModel = pageModelFactory.BuildFolderBrowsePage(context, share, directoryEntry, entries);
        if (!htmlRenderer.TryRender(pageModel, out var html, out var renderError))
        {
            return Results.Problem(renderError, statusCode: StatusCodes.Status500InternalServerError);
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            return Results.Empty;
        }

        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> HandleFolderZipDownloadAsync(
        HttpContext context,
        IShareCoordinator coordinator,
        PowerManagementService powerManagementService,
        AppSettings settings,
        ShareRecord share,
        FolderSharePathResolver.ResolvedEntry directoryEntry,
        PublicSharePageModelFactory pageModelFactory,
        PublicShareHtmlRenderer htmlRenderer,
        CancellationToken cancellationToken)
    {
        if (!directoryEntry.IsDirectory)
        {
            return Results.NotFound();
        }

        var zipFileName = $"{directoryEntry.Name}.zip";
        var crawlerName = MetadataCrawlerDetector.ResolveMetadataCrawlerName(context.Request);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var userAgent = context.Request.Headers.UserAgent.ToString();

        if (crawlerName is not null && settings.SendMetadataToCrawlers)
        {
            var previewTransfer = await coordinator.StartTransferAsync(
                share.Id,
                share.Token,
                zipFileName,
                TransferKind.MetadataPreview,
                null,
                RequestAddressResolver.BuildClientFingerprint(remoteAddress, userAgent),
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
                paused: false,
                succeeded: true,
                countsTowardUsage: false,
                usageSessionKey: null,
                error: null,
                requesterName: crawlerName,
                cancellationToken);

            var pageModel = pageModelFactory.BuildFolderZipMetadataPage(context, share, directoryEntry);
            if (!htmlRenderer.TryRender(pageModel, out var metadataHtml, out var renderError))
            {
                return Results.Problem(renderError, statusCode: StatusCodes.Status500InternalServerError);
            }

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
            context.Response.Headers["Content-Disposition"] = ResponseHeaderWriter.BuildContentDispositionHeader("attachment", zipFileName);
            return Results.Empty;
        }

        var manifest = FolderZipManifestBuilder.Build(directoryEntry);
        var (clientSessionId, setCookie) = DownloadSessionManager.ResolveDownloadSession(context, share.Token);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, userAgent);
        var transfer = await coordinator.StartTransferAsync(
            share.Id,
            share.Token,
            zipFileName,
            TransferKind.FolderZipDownload,
            clientSessionId,
            clientFingerprint,
            remoteAddress,
            totalBytes: 0,
            bytesSent: 0,
            requesterName: null,
            cancellationToken);

        if (setCookie)
        {
            context.Response.Cookies.Append(
                DownloadSessionManager.GetCookieName(share.Token),
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
        context.Response.Headers["Content-Disposition"] = ResponseHeaderWriter.BuildContentDispositionHeader("attachment", zipFileName);

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

        await using var meteredStream = new MeteredWriteStream(context.Response.Body, settings.BandwidthLimitBytesPerSecond);
        var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var progressTask = TransferProgressTracker.TrackWriteProgressAsync(coordinator, transfer.Id, meteredStream, progressCancellation.Token);

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
                requesterName: null,
                CancellationToken.None);

            progressCancellation.Dispose();

            if (settings.KeepAwakeWhileTransferring)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }

        return Results.Empty;
    }

    private static CompressionLevel MapCompressionLevel(FolderZipCompressionLevel compressionLevel)
    {
        return compressionLevel switch
        {
            FolderZipCompressionLevel.Fastest => CompressionLevel.Fastest,
            FolderZipCompressionLevel.NoCompression => CompressionLevel.NoCompression,
            FolderZipCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };
    }

    private static bool IsCurrentDirectoryZipRequest(HttpRequest request)
    {
        return string.Equals(request.Query["download"], "zip", StringComparison.OrdinalIgnoreCase);
    }
}
