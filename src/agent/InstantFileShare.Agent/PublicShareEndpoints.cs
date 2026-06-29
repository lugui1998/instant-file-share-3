using System.IO.Compression;
using System.Text;
using InstantFileShare.Core;
using Microsoft.AspNetCore.Http.Features;
using RangeItemHeaderValue = Microsoft.Net.Http.Headers.RangeItemHeaderValue;

namespace InstantFileShare.Agent;

internal static class PublicShareEndpoints
{
    public static IEndpointRouteBuilder MapPublicShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
            "/r/{token}",
            ["GET", "HEAD"],
            (string token, HttpContext context, IShareCoordinator coordinator, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, CancellationToken cancellationToken) =>
                HandleReceiveLinkPageAsync(token, context, coordinator, pageModelFactory, htmlRenderer, cancellationToken));

        endpoints.MapPost(
            "/r/{token}",
            (string token, HttpContext context, IShareCoordinator coordinator, INotificationService notificationService, PowerManagementService powerManagementService, CancellationToken cancellationToken) =>
                HandleReceiveUploadAsync(token, context, coordinator, notificationService, powerManagementService, cancellationToken));

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
        if (HasTraversalAttempt(context))
        {
            return Results.NotFound();
        }

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

    private static async Task<IResult> HandleReceiveLinkPageAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        PublicSharePageModelFactory pageModelFactory,
        PublicShareHtmlRenderer htmlRenderer,
        CancellationToken cancellationToken)
    {
        var receiveLink = await coordinator.ResolveReceiveLinkAsync(token, cancellationToken);
        if (receiveLink is null)
        {
            return Results.NotFound();
        }

        var unavailableResult = ResolveUnavailableReceiveLinkResult(receiveLink);
        if (unavailableResult is not null)
        {
            return unavailableResult;
        }

        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        var pageModel = pageModelFactory.BuildReceivePage(context, receiveLink, settings);
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

    private static async Task<IResult> HandleReceiveUploadAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        INotificationService notificationService,
        PowerManagementService powerManagementService,
        CancellationToken cancellationToken)
    {
        var receiveLink = await coordinator.ResolveReceiveLinkAsync(token, cancellationToken);
        if (receiveLink is null)
        {
            return Results.NotFound();
        }

        var unavailableResult = ResolveUnavailableReceiveLinkResult(receiveLink);
        if (unavailableResult is not null)
        {
            return unavailableResult;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }

        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new { message = "No files were uploaded." });
        }

        var candidates = BuildReceiveUploadCandidates(form);
        var (plannedUploads, rejectedUploads) = ReceiveUploadPlanner.Plan(receiveLink.TargetDirectoryPath, candidates);
        var results = rejectedUploads.ToList();
        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString());
        var successfulUploadCount = 0;

        if (plannedUploads.Count > 0 && settings.KeepAwakeWhileTransferring)
        {
            powerManagementService.NotifyTransferStarted();
        }

        try
        {
            foreach (var plannedUpload in plannedUploads)
            {
                if (plannedUpload.File.Length > 0 &&
                    receiveLink.BytesReceived + plannedUpload.File.Length > receiveLink.MaxTotalBytes)
                {
                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, null, false, "This receive link has reached its upload limit.", 0));
                    continue;
                }

                var reservedReceiveLink = await coordinator.AddReceivedBytesAsync(receiveLink.Id, plannedUpload.File.Length, cancellationToken);
                if (reservedReceiveLink is null)
                {
                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, null, false, "The receive link is unavailable.", 0));
                    continue;
                }

                if (receiveLink.MaxTotalBytes > 0 &&
                    plannedUpload.File.Length > 0 &&
                    reservedReceiveLink.State == ReceiveLinkState.Exhausted &&
                    reservedReceiveLink.BytesReceived == receiveLink.BytesReceived)
                {
                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, null, false, "This receive link has reached its upload limit.", 0));
                    continue;
                }

                receiveLink = reservedReceiveLink;

                var parentDirectoryPath = Path.GetDirectoryName(plannedUpload.DestinationPath);
                if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
                {
                    Directory.CreateDirectory(parentDirectoryPath);
                }

                TransferSnapshot? transfer = null;
                long bytesWritten = 0;

                try
                {
                    transfer = await coordinator.StartTransferAsync(
                        receiveLink.Id,
                        receiveLink.Token,
                        plannedUpload.StoredRelativePath,
                        TransferKind.FileUpload,
                        clientSessionId: null,
                        clientFingerprint,
                        remoteAddress,
                        plannedUpload.File.Length,
                        bytesSent: 0,
                        requesterName: null,
                        cancellationToken);

                    await using var sourceStream = plannedUpload.File.OpenReadStream();
                    await using var destinationStream = new FileStream(
                        plannedUpload.DestinationPath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            Options = FileOptions.Asynchronous,
                        });

                    var buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        bytesWritten += bytesRead;
                        await coordinator.UpdateTransferProgressAsync(transfer.Id, bytesWritten, cancellationToken);
                    }

                    if (bytesWritten != plannedUpload.File.Length)
                    {
                        var adjustedReceiveLink = await coordinator.AddReceivedBytesAsync(receiveLink.Id, bytesWritten - plannedUpload.File.Length, cancellationToken);
                        if (adjustedReceiveLink is not null)
                        {
                            receiveLink = adjustedReceiveLink;
                        }
                    }

                    await coordinator.MarkTransferCompletedAsync(
                        transfer.Id,
                        receiveLink.Id,
                        receiveLink.Token,
                        plannedUpload.StoredRelativePath,
                        TransferKind.FileUpload,
                        remoteAddress,
                        bytesWritten,
                        plannedUpload.File.Length,
                        paused: false,
                        succeeded: true,
                        countsTowardUsage: false,
                        usageSessionKey: null,
                        error: null,
                        requesterName: null,
                        cancellationToken);

                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, plannedUpload.StoredRelativePath, true, null, bytesWritten));
                    successfulUploadCount++;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    await RollBackFailedUploadAsync(coordinator, receiveLink.Id, plannedUpload.File.Length, plannedUpload.DestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, null, false, "The upload was interrupted.", bytesWritten));
                    break;
                }
                catch (Exception exception)
                {
                    await RollBackFailedUploadAsync(coordinator, receiveLink.Id, plannedUpload.File.Length, plannedUpload.DestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
                    results.Add(new ReceiveUploadFileResult(plannedUpload.File.FileName, plannedUpload.ClientRelativePath, null, false, exception.Message, bytesWritten));
                }
            }
        }
        finally
        {
            if (plannedUploads.Count > 0 && settings.KeepAwakeWhileTransferring)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }

        receiveLink = await coordinator.ResolveReceiveLinkAsync(token, cancellationToken) ?? receiveLink;
        var remainingQuotaBytes = receiveLink.MaxTotalBytes > 0
            ? Math.Max(0, receiveLink.MaxTotalBytes - receiveLink.BytesReceived)
            : 0;

        if (successfulUploadCount > 0 && settings.ReceiveNotificationsEnabled)
        {
            try
            {
                var summary = successfulUploadCount == 1
                    ? $"1 file received in {receiveLink.TargetDisplayName}"
                    : $"{successfulUploadCount} files received in {receiveLink.TargetDisplayName}";
                notificationService.ShowInfo("Files received", summary);
            }
            catch
            {
            }
        }

        return Results.Ok(new ReceiveUploadBatchResult(
            successfulUploadCount,
            results.Count - successfulUploadCount,
            remainingQuotaBytes,
            results));
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

    private static IResult? ResolveUnavailableReceiveLinkResult(ReceiveLinkRecord receiveLink)
    {
        return receiveLink.State switch
        {
            ReceiveLinkState.Revoked or ReceiveLinkState.Expired or ReceiveLinkState.Exhausted => Results.StatusCode(StatusCodes.Status410Gone),
            ReceiveLinkState.Broken => Results.Problem(receiveLink.BrokenReason ?? "The receive link is unavailable.", statusCode: StatusCodes.Status410Gone),
            _ => null,
        };
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
        var initialBytesSent = ResolveRangeStartOffset(file.Length, requestedRange);
        var expectedTransferBytes = ResolveExpectedTransferBytes(file.Length, requestedRange);
        var countsTowardUsage = true;
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
                var completedRequestedBytes = meteredStream.BytesRead >= expectedTransferBytes;
                var requestAborted = context.RequestAborted.IsCancellationRequested;
                var paused = requestAborted && meteredStream.BytesRead > 0 && !completedRequestedBytes;
                var succeeded = completedRequestedBytes && !requestAborted;
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

        var pageModel = pageModelFactory.BuildFolderBrowsePage(context, share, directoryEntry, entries, settings);
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

    private static long ResolveRangeStartOffset(long fileLength, RangeItemHeaderValue? requestedRange)
    {
        if (requestedRange is null || fileLength <= 0)
        {
            return 0;
        }

        if (requestedRange.From is long rangeStart)
        {
            return Math.Clamp(rangeStart, 0, Math.Max(0, fileLength - 1));
        }

        if (requestedRange.To is long suffixLength)
        {
            return Math.Max(0, fileLength - Math.Min(fileLength, suffixLength));
        }

        return 0;
    }

    private static long ResolveExpectedTransferBytes(long fileLength, RangeItemHeaderValue? requestedRange)
    {
        if (fileLength <= 0)
        {
            return 0;
        }

        if (requestedRange is null)
        {
            return fileLength;
        }

        if (requestedRange.From is long rangeStart)
        {
            var normalizedStart = Math.Clamp(rangeStart, 0, Math.Max(0, fileLength - 1));
            var normalizedEnd = requestedRange.To is long rangeEnd
                ? Math.Clamp(rangeEnd, normalizedStart, Math.Max(0, fileLength - 1))
                : fileLength - 1;
            return normalizedEnd - normalizedStart + 1;
        }

        if (requestedRange.To is long suffixLength)
        {
            return Math.Min(fileLength, suffixLength);
        }

        return fileLength;
    }

    private static IReadOnlyList<ReceiveUploadCandidate> BuildReceiveUploadCandidates(IFormCollection form)
    {
        var relativePaths = form["relativePaths"];
        var candidates = new List<ReceiveUploadCandidate>(form.Files.Count);

        for (var index = 0; index < form.Files.Count; index++)
        {
            var file = form.Files[index];
            var relativePath = index < relativePaths.Count ? relativePaths[index] ?? string.Empty : string.Empty;
            candidates.Add(new ReceiveUploadCandidate(file, relativePath));
        }

        return candidates;
    }

    private static async Task RollBackFailedUploadAsync(
        IShareCoordinator coordinator,
        string receiveLinkId,
        long reservedBytes,
        string destinationPath,
        TransferSnapshot? transfer,
        string token,
        string storedRelativePath,
        string? remoteAddress,
        long bytesWritten)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
        catch
        {
        }

        try
        {
            await coordinator.AddReceivedBytesAsync(receiveLinkId, -reservedBytes, CancellationToken.None);
        }
        catch
        {
        }

        if (transfer is null)
        {
            return;
        }

        try
        {
            await coordinator.MarkTransferCompletedAsync(
                transfer.Id,
                receiveLinkId,
                token,
                storedRelativePath,
                TransferKind.FileUpload,
                remoteAddress,
                bytesWritten,
                reservedBytes,
                paused: false,
                succeeded: false,
                countsTowardUsage: false,
                usageSessionKey: null,
                error: "The upload did not complete.",
                requesterName: null,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static bool HasTraversalAttempt(HttpContext context)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? string.Empty;
        var rawPath = rawTarget.Split('?', 2)[0];
        var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (!TryDecodeRepeatedly(segment, out var decodedSegment))
            {
                return true;
            }

            if (string.Equals(decodedSegment, "..", StringComparison.Ordinal) ||
                decodedSegment.Contains('/') ||
                decodedSegment.Contains('\\'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeRepeatedly(string value, out string decodedValue)
    {
        var current = value;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (ArgumentException)
            {
                decodedValue = value;
                return false;
            }

            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                decodedValue = decoded;
                return true;
            }

            current = decoded;
        }

        decodedValue = current;
        return true;
    }
}
