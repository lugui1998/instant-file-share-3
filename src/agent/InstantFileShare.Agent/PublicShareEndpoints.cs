using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InstantFileShare.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using ContentDispositionHeaderValue = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue;
using HeaderUtilities = Microsoft.Net.Http.Headers.HeaderUtilities;
using MediaTypeHeaderValue = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;
using RangeItemHeaderValue = Microsoft.Net.Http.Headers.RangeItemHeaderValue;

namespace InstantFileShare.Agent;

internal static class PublicShareEndpoints
{
    private const string FolderListQueryValue = "folder-list";
    private const string FolderEventsQueryValue = "folder-events";
    private const string PartialUploadSuffix = ".downloadpart";
    private const string BinaryChunkContentType = "application/octet-stream";
    private const string UploadIdHeaderName = "X-IFS-Upload-Id";
    private const string BatchIdHeaderName = "X-IFS-Batch-Id";
    private const string RelativePathHeaderName = "X-IFS-Relative-Path";
    private const string FileNameHeaderName = "X-IFS-File-Name";
    private const string FileSizeHeaderName = "X-IFS-File-Size";
    private const string CompressionQueryName = "compression";
    private const string GzipCompressionQueryValue = "gzip";
    private const string BrowserCompressionHeaderName = "X-IFS-Transfer-Compression";
    private const string UncompressedLengthHeaderName = "X-IFS-Uncompressed-Length";
    private const string ChunkIndexHeaderName = "X-IFS-Chunk-Index";
    private const string ChunkCountHeaderName = "X-IFS-Chunk-Count";
    private const string ChunkStartHeaderName = "X-IFS-Chunk-Start";
    private const string ChunkSizeHeaderName = "X-IFS-Chunk-Size";
    private const double AdaptiveChunkSmoothingFactor = 0.35;
    private const int AdaptiveChunkGrowthFactor = 2;
    private const long MaximumAdaptiveChunkSizeBytes = 64L * 1024 * 1024;
    private const int ReceiveUploadBufferSizeBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions WebSocketJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, ReceiveUploadSpeedState> ReceiveUploadSpeedStates = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapPublicShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
            "/r/{token}",
            ["GET", "HEAD"],
            (string token, HttpContext context, IShareCoordinator coordinator, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, CancellationToken cancellationToken) =>
                HandleReceiveLinkPageAsync(token, context, coordinator, pageModelFactory, htmlRenderer, cancellationToken));

        endpoints.MapGet(
            "/r/{token}/events",
            (string token, HttpContext context, IShareCoordinator coordinator, IRuntimeEventStream runtimeEvents, CancellationToken cancellationToken) =>
                HandleReceiveUploadEventsAsync(token, context, coordinator, runtimeEvents, cancellationToken));

        endpoints.MapGet(
            "/r/{token}/upload-socket",
            (string token, HttpContext context, IShareCoordinator coordinator, INotificationService notificationService, PowerManagementService powerManagementService, ReceiveUploadBatchNotificationTracker batchNotificationTracker, ReceiveUploadChunkSessionStore chunkSessionStore, CancellationToken cancellationToken) =>
                HandleReceiveUploadSocketAsync(token, context, coordinator, notificationService, powerManagementService, batchNotificationTracker, chunkSessionStore, cancellationToken));

        endpoints.MapPost(
            "/r/{token}/cancel-upload",
            (string token, HttpContext context, IShareCoordinator coordinator, ReceiveUploadChunkSessionStore chunkSessionStore, CancellationToken cancellationToken) =>
                HandleReceiveUploadCancelAsync(token, context, coordinator, chunkSessionStore, cancellationToken));

        endpoints.MapPost(
            "/r/{token}",
            (string token, HttpContext context, IShareCoordinator coordinator, INotificationService notificationService, PowerManagementService powerManagementService, ReceiveUploadBatchNotificationTracker batchNotificationTracker, ReceiveUploadChunkSessionStore chunkSessionStore, CancellationToken cancellationToken) =>
                HandleReceiveUploadAsync(token, context, coordinator, notificationService, powerManagementService, batchNotificationTracker, chunkSessionStore, cancellationToken));

        endpoints.MapMethods(
            "/s/{token}/{slug}/{**path}",
            ["GET", "HEAD"],
            (string token, string slug, string path, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, PublicFolderChangeNotifier folderChangeNotifier, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, slug, path, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, folderChangeNotifier, cancellationToken));

        endpoints.MapMethods(
            "/s/{token}/{slug}",
            ["GET", "HEAD"],
            (string token, string slug, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, PublicFolderChangeNotifier folderChangeNotifier, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, slug, null, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, folderChangeNotifier, cancellationToken));

        endpoints.MapMethods(
            "/s/{token}",
            ["GET", "HEAD"],
            (string token, HttpContext context, IShareCoordinator coordinator, PowerManagementService powerManagementService, PublicSharePageModelFactory pageModelFactory, PublicShareHtmlRenderer htmlRenderer, PublicFolderChangeNotifier folderChangeNotifier, CancellationToken cancellationToken) =>
                HandlePublicShareAsync(token, null, null, context, coordinator, powerManagementService, pageModelFactory, htmlRenderer, folderChangeNotifier, cancellationToken));

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
        PublicFolderChangeNotifier folderChangeNotifier,
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
        var legacyZipSegment = string.IsNullOrWhiteSpace(slug) ? null : $"{slug}.zip";
        var isRootZipRequest = string.IsNullOrWhiteSpace(slugOrArchive) && IsCurrentDirectoryZipRequest(context.Request);
        var isLegacyRootZipRequest = string.IsNullOrEmpty(relativePath) &&
            legacyZipSegment is not null &&
            string.Equals(slugOrArchive, legacyZipSegment, StringComparison.OrdinalIgnoreCase);
        if (isRootZipRequest || isLegacyRootZipRequest)
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

        if (!share.CanBrowseFolderContents)
        {
            return Results.NotFound();
        }

        if (!TryResolveFolderBrowseEntry(share.FilePath, slug, slugOrArchive, relativePath, out var resolvedEntry) || resolvedEntry is null)
        {
            return Results.NotFound();
        }

        var folderSpecialRequest = context.Request.Query["ifs"].ToString();
        if (!string.IsNullOrEmpty(folderSpecialRequest))
        {
            if (!resolvedEntry.IsDirectory)
            {
                return Results.NotFound();
            }

            if (string.Equals(folderSpecialRequest, FolderListQueryValue, StringComparison.OrdinalIgnoreCase))
            {
                return HandleFolderBrowseDirectoryJson(context, settings, share, resolvedEntry, pageModelFactory);
            }

            if (string.Equals(folderSpecialRequest, FolderEventsQueryValue, StringComparison.OrdinalIgnoreCase))
            {
                return await HandleFolderChangeWebSocketAsync(context, resolvedEntry, folderChangeNotifier, cancellationToken);
            }

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

    private static bool TryResolveFolderBrowseEntry(
        string rootPath,
        string? slug,
        string? firstPathSegment,
        string? remainingPath,
        out FolderSharePathResolver.ResolvedEntry? resolvedEntry)
    {
        var cleanRelativePath = CombineFolderBrowsePath(firstPathSegment, remainingPath);
        if (FolderSharePathResolver.TryResolveEntry(rootPath, cleanRelativePath, out resolvedEntry))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(slug) &&
            string.Equals(firstPathSegment, slug, StringComparison.OrdinalIgnoreCase))
        {
            return FolderSharePathResolver.TryResolveEntry(rootPath, remainingPath, out resolvedEntry);
        }

        return false;
    }

    private static string? CombineFolderBrowsePath(string? firstPathSegment, string? remainingPath)
    {
        if (string.IsNullOrWhiteSpace(firstPathSegment))
        {
            return remainingPath;
        }

        return string.IsNullOrWhiteSpace(remainingPath)
            ? firstPathSegment
            : $"{firstPathSegment}/{remainingPath}";
    }

    private static IResult HandleFolderBrowseDirectoryJson(
        HttpContext context,
        AppSettings settings,
        ShareRecord share,
        FolderSharePathResolver.ResolvedEntry directoryEntry,
        PublicSharePageModelFactory pageModelFactory)
    {
        var entries = FolderSharePathResolver.ListDirectory(directoryEntry);
        var pageModel = pageModelFactory.BuildFolderBrowsePage(context, share, directoryEntry, entries, settings);
        return Results.Json(pageModel.Folder);
    }

    private static async Task<IResult> HandleFolderChangeWebSocketAsync(
        HttpContext context,
        FolderSharePathResolver.ResolvedEntry directoryEntry,
        PublicFolderChangeNotifier folderChangeNotifier,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { error = "Expected a WebSocket upgrade request." });
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await foreach (var changeEvent in folderChangeNotifier.ListenAsync(directoryEntry.FullPath, directoryEntry.RelativePath, cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(changeEvent, WebSocketJsonOptions);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleReceiveUploadAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        INotificationService notificationService,
        PowerManagementService powerManagementService,
        ReceiveUploadBatchNotificationTracker batchNotificationTracker,
        ReceiveUploadChunkSessionStore chunkSessionStore,
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

        if (IsReceiveUploadBatchCompleteRequest(context))
        {
            return HandleReceiveUploadBatchComplete(context, receiveLink, batchNotificationTracker);
        }

        DisableRequestBodySizeLimit(context);

        if (IsReceiveUploadBinaryChunkRequest(context.Request))
        {
            return await HandleReceiveBinaryChunkUploadAsync(
                receiveLink,
                context,
                coordinator,
                notificationService,
                powerManagementService,
                batchNotificationTracker,
                chunkSessionStore,
                cancellationToken);
        }

        if (!TryCreateMultipartReader(context.Request, out var multipartReader, out var multipartError))
        {
            return multipartError!;
        }

        var results = new List<ReceiveUploadFileResult>();
        var relativePaths = new Queue<string>();
        var fileSizes = new Queue<long>();
        var uploadIds = new Queue<string>();
        var chunkIndexes = new Queue<int>();
        var chunkCounts = new Queue<int>();
        var chunkStarts = new Queue<long>();
        var chunkSizes = new Queue<long>();
        string? batchId = null;
        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString());
        var uploadPlanState = new ReceiveUploadPlanState(receiveLink.TargetDirectoryPath);
        var successfulUploadCount = 0;
        var receivedFileCount = 0;
        var keepAwakeNotified = false;

        try
        {
            MultipartSection? section;
            while ((section = await multipartReader!.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    continue;
                }

                var sectionName = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value ?? string.Empty;
                if (IsMultipartFileSection(contentDisposition))
                {
                    receivedFileCount++;

                    var fileName = ResolveMultipartFileName(contentDisposition);
                    var clientRelativePath = relativePaths.Count > 0 ? relativePaths.Dequeue() : fileName;
                    var expectedFileSize = fileSizes.Count > 0 ? fileSizes.Dequeue() : 0;
                    var uploadId = uploadIds.Count > 0 ? uploadIds.Dequeue() : string.Empty;
                    if (TryDequeueReceiveUploadChunkMetadata(
                        uploadId,
                        chunkIndexes,
                        chunkCounts,
                        chunkStarts,
                        chunkSizes,
                        out var chunkMetadata))
                    {
                        if (!keepAwakeNotified && settings.KeepAwakeWhileTransferring)
                        {
                            powerManagementService.NotifyTransferStarted();
                            keepAwakeNotified = true;
                        }

                        var streamResult = await SaveReceiveUploadChunkSectionAsync(
                            section.Body,
                            chunkMetadata,
                            fileName,
                            expectedFileSize,
                            clientRelativePath,
                            receiveLink,
                            coordinator,
                            chunkSessionStore,
                            uploadPlanState,
                            remoteAddress,
                            clientFingerprint,
                            cancellationToken);

                        receiveLink = streamResult.ReceiveLink;
                        if (streamResult.Result is not null)
                        {
                            results.Add(streamResult.Result);
                            if (streamResult.Result.Success)
                            {
                                successfulUploadCount++;
                            }
                        }
                    }
                    else
                    {
                        var candidate = new ReceiveUploadCandidate(fileName, expectedFileSize, clientRelativePath);
                        var (plannedUploads, rejectedUploads) = ReceiveUploadPlanner.Plan(receiveLink.TargetDirectoryPath, [candidate], uploadPlanState);
                        results.AddRange(rejectedUploads);

                        if (plannedUploads.Count == 0)
                        {
                            await section.Body.CopyToAsync(Stream.Null, cancellationToken);
                            continue;
                        }

                        if (!keepAwakeNotified && settings.KeepAwakeWhileTransferring)
                        {
                            powerManagementService.NotifyTransferStarted();
                            keepAwakeNotified = true;
                        }

                        var streamResult = await SaveReceiveUploadSectionAsync(
                            section.Body,
                        plannedUploads[0],
                            receiveLink,
                            coordinator,
                            chunkSessionStore,
                            uploadId,
                            remoteAddress,
                            clientFingerprint,
                        cancellationToken);

                        receiveLink = streamResult.ReceiveLink;
                        if (streamResult.Result is not null)
                        {
                            results.Add(streamResult.Result);
                            if (streamResult.Result.Success)
                            {
                                successfulUploadCount++;
                            }
                        }
                    }

                    continue;
                }

                if (string.Equals(sectionName, "relativePaths", StringComparison.Ordinal))
                {
                    relativePaths.Enqueue(await ReadMultipartFieldValueAsync(section.Body, cancellationToken));
                }
                else if (string.Equals(sectionName, "fileSizes", StringComparison.Ordinal))
                {
                    fileSizes.Enqueue(ParseReceiveUploadFileSize(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
                else if (string.Equals(sectionName, "batchId", StringComparison.Ordinal))
                {
                    batchId = NormalizeReceiveUploadBatchId(await ReadMultipartFieldValueAsync(section.Body, cancellationToken));
                }
                else if (string.Equals(sectionName, "uploadId", StringComparison.Ordinal))
                {
                    uploadIds.Enqueue(NormalizeReceiveUploadId(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
                else if (string.Equals(sectionName, "chunkIndex", StringComparison.Ordinal))
                {
                    chunkIndexes.Enqueue(ParseReceiveUploadInt(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
                else if (string.Equals(sectionName, "chunkCount", StringComparison.Ordinal))
                {
                    chunkCounts.Enqueue(ParseReceiveUploadInt(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
                else if (string.Equals(sectionName, "chunkStart", StringComparison.Ordinal))
                {
                    chunkStarts.Enqueue(ParseReceiveUploadLong(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
                else if (string.Equals(sectionName, "chunkSize", StringComparison.Ordinal))
                {
                    chunkSizes.Enqueue(ParseReceiveUploadLong(await ReadMultipartFieldValueAsync(section.Body, cancellationToken)));
                }
            }
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        finally
        {
            if (keepAwakeNotified)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }

        if (receivedFileCount == 0)
        {
            return Results.BadRequest(new { message = "No files were uploaded." });
        }

        receiveLink = await coordinator.ResolveReceiveLinkAsync(token, cancellationToken) ?? receiveLink;
        var remainingQuotaBytes = receiveLink.MaxTotalBytes > 0
            ? Math.Max(0, receiveLink.MaxTotalBytes - receiveLink.BytesReceived)
            : 0;

        RecordSuccessfulReceiveUploads(
            receiveLink,
            settings,
            notificationService,
            batchNotificationTracker,
            batchId,
            successfulUploadCount);

        return CreateReceiveUploadBatchResponse(new ReceiveUploadBatchResult(
            successfulUploadCount,
            results.Count - successfulUploadCount,
            remainingQuotaBytes,
            results));
    }

    private static async Task<IResult> HandleReceiveBinaryChunkUploadAsync(
        ReceiveLinkRecord receiveLink,
        HttpContext context,
        IShareCoordinator coordinator,
        INotificationService notificationService,
        PowerManagementService powerManagementService,
        ReceiveUploadBatchNotificationTracker batchNotificationTracker,
        ReceiveUploadChunkSessionStore chunkSessionStore,
        CancellationToken cancellationToken)
    {
        if (!TryReadBinaryChunkHeaders(
            context.Request,
            out var batchId,
            out var clientRelativePath,
            out var fileName,
            out var expectedFileSize,
            out var chunkMetadata,
            out var error))
        {
            return error!;
        }

        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString());
        var uploadPlanState = new ReceiveUploadPlanState(receiveLink.TargetDirectoryPath);
        var keepAwakeNotified = false;

        try
        {
            if (settings.KeepAwakeWhileTransferring)
            {
                powerManagementService.NotifyTransferStarted();
                keepAwakeNotified = true;
            }

            var streamResult = await SaveReceiveUploadChunkSectionAsync(
                context.Request.Body,
                chunkMetadata,
                fileName,
                expectedFileSize,
                clientRelativePath,
                receiveLink,
                coordinator,
                chunkSessionStore,
                uploadPlanState,
                remoteAddress,
                clientFingerprint,
                cancellationToken);

            receiveLink = streamResult.ReceiveLink;
            receiveLink = await coordinator.ResolveReceiveLinkAsync(receiveLink.Token, cancellationToken) ?? receiveLink;
            var remainingQuotaBytes = receiveLink.MaxTotalBytes > 0
                ? Math.Max(0, receiveLink.MaxTotalBytes - receiveLink.BytesReceived)
                : 0;
            var results = streamResult.Result is null
                ? Array.Empty<ReceiveUploadFileResult>()
                : new[] { streamResult.Result };
            var successfulUploadCount = streamResult.Result?.Success == true ? 1 : 0;

            RecordSuccessfulReceiveUploads(
                receiveLink,
                settings,
                notificationService,
                batchNotificationTracker,
                batchId,
                successfulUploadCount);

            return CreateReceiveUploadBatchResponse(new ReceiveUploadBatchResult(
                successfulUploadCount,
                results.Length - successfulUploadCount,
                remainingQuotaBytes,
                results));
        }
        finally
        {
            if (keepAwakeNotified)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }
    }

    private static async Task<IResult> HandleReceiveUploadSocketAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        INotificationService notificationService,
        PowerManagementService powerManagementService,
        ReceiveUploadBatchNotificationTracker batchNotificationTracker,
        ReceiveUploadChunkSessionStore chunkSessionStore,
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

        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { message = "Expected a WebSocket upload request." });
        }

        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        var remoteAddress = RequestAddressResolver.ResolveClientIpAddress(context);
        var clientFingerprint = RequestAddressResolver.BuildClientFingerprint(remoteAddress, context.Request.Headers.UserAgent.ToString());
        var uploadPlanState = new ReceiveUploadPlanState(receiveLink.TargetDirectoryPath);
        var keepAwakeNotified = false;

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var textMessage = await ReadWebSocketTextMessageAsync(socket, cancellationToken);
                if (textMessage is null)
                {
                    break;
                }

                if (!TryReadWebSocketChunkMessage(
                    textMessage,
                    out var batchId,
                    out var clientRelativePath,
                    out var fileName,
                    out var expectedFileSize,
                    out var chunkMetadata,
                    out var errorMessage))
                {
                    await SendReceiveUploadSocketErrorAsync(socket, errorMessage ?? "Upload chunk metadata is invalid.", cancellationToken);
                    continue;
                }

                var chunkStream = await ReadWebSocketBinaryMessageAsync(socket, cancellationToken);
                if (chunkStream is null)
                {
                    break;
                }

                await using (chunkStream)
                {
                    if (!keepAwakeNotified && settings.KeepAwakeWhileTransferring)
                    {
                        powerManagementService.NotifyTransferStarted();
                        keepAwakeNotified = true;
                    }

                    var streamResult = await SaveReceiveUploadChunkSectionAsync(
                        chunkStream,
                        chunkMetadata,
                        fileName,
                        expectedFileSize,
                        clientRelativePath,
                        receiveLink,
                        coordinator,
                        chunkSessionStore,
                        uploadPlanState,
                        remoteAddress,
                        clientFingerprint,
                        cancellationToken);

                    receiveLink = streamResult.ReceiveLink;
                    receiveLink = await coordinator.ResolveReceiveLinkAsync(receiveLink.Token, cancellationToken) ?? receiveLink;
                    var remainingQuotaBytes = receiveLink.MaxTotalBytes > 0
                        ? Math.Max(0, receiveLink.MaxTotalBytes - receiveLink.BytesReceived)
                        : 0;
                    var results = streamResult.Result is null
                        ? Array.Empty<ReceiveUploadFileResult>()
                        : new[] { streamResult.Result };
                    var successfulUploadCount = streamResult.Result?.Success == true ? 1 : 0;

                    RecordSuccessfulReceiveUploads(
                        receiveLink,
                        settings,
                        notificationService,
                        batchNotificationTracker,
                        batchId,
                        successfulUploadCount);

                    await SendReceiveUploadSocketResponseAsync(
                        socket,
                        new ReceiveUploadBatchResult(
                            successfulUploadCount,
                            results.Length - successfulUploadCount,
                            remainingQuotaBytes,
                            results),
                        cancellationToken);
                }
            }
        }
        finally
        {
            if (keepAwakeNotified)
            {
                powerManagementService.NotifyTransferEnded();
            }
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleReceiveUploadCancelAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        ReceiveUploadChunkSessionStore chunkSessionStore,
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

        var uploadId = NormalizeReceiveUploadId(context.Request.Query["uploadId"].ToString());
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            uploadId = NormalizeReceiveUploadId(ReadHeaderValue(context.Request, UploadIdHeaderName));
        }

        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return Results.BadRequest(new { message = "Upload cancellation requires an upload ID." });
        }

        chunkSessionStore.MarkCanceled(uploadId);
        if (chunkSessionStore.TryGet(uploadId, out var session) &&
            session is not null &&
            await session.Gate.WaitAsync(0, cancellationToken))
        {
            try
            {
                chunkSessionStore.Remove(session.UploadId);
                await RollBackFailedUploadAsync(
                    coordinator,
                    session.ReceiveLinkId,
                    session.BytesWritten,
                    session.PartialDestinationPath,
                    session.Transfer,
                    session.Token,
                    session.StoredRelativePath,
                    RequestAddressResolver.ResolveClientIpAddress(context),
                    session.BytesWritten,
                    paused: true,
                    error: "Upload stopped.");
            }
            finally
            {
                session.Gate.Release();
            }
        }

        return Results.Ok(new { canceled = true });
    }

    private static async Task HandleReceiveUploadEventsAsync(
        string token,
        HttpContext context,
        IShareCoordinator coordinator,
        IRuntimeEventStream runtimeEvents,
        CancellationToken cancellationToken)
    {
        var receiveLink = await coordinator.ResolveReceiveLinkAsync(token, cancellationToken);
        if (receiveLink is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var unavailableResult = ResolveUnavailableReceiveLinkResult(receiveLink);
        if (unavailableResult is not null)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var settings = await coordinator.GetSettingsAsync(cancellationToken);
        await foreach (var runtimeEvent in runtimeEvents.ListenAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            if (!TryCreateReceiveUploadProgressEvent(
                receiveLink.Id,
                runtimeEvent,
                settings.ReceiveUploadChunkSizingMode,
                settings.ReceiveUploadChunkSizeBytes,
                settings.ReceiveUploadMaxBodySizeBytes,
                settings.ReceiveUploadChunkTargetSeconds,
                out var progressEvent) || progressEvent is null)
            {
                continue;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(progressEvent, WebSocketJsonOptions);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static async Task<ReceiveUploadStreamResult> SaveReceiveUploadSectionAsync(
        Stream sourceStream,
        PlannedReceiveUpload plannedUpload,
        ReceiveLinkRecord receiveLink,
        IShareCoordinator coordinator,
        ReceiveUploadChunkSessionStore chunkSessionStore,
        string? uploadId,
        string? remoteAddress,
        string? clientFingerprint,
        CancellationToken cancellationToken)
    {
        var parentDirectoryPath = Path.GetDirectoryName(plannedUpload.DestinationPath);
        if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
        {
            Directory.CreateDirectory(parentDirectoryPath);
        }

        if (!string.IsNullOrWhiteSpace(uploadId) && chunkSessionStore.IsCanceled(uploadId))
        {
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "Upload stopped.", 0));
        }

        TransferSnapshot? transfer = null;
        long bytesWritten = 0;
        long reservedBytes = 0;
        var expectedTotalBytes = plannedUpload.Length > 0 ? plannedUpload.Length : 0;
        var progressTotalBytes = expectedTotalBytes > 0 ? expectedTotalBytes : (long?)null;
        var partialDestinationPath = GetPartialUploadPath(plannedUpload.DestinationPath);

        try
        {
            transfer = await coordinator.StartTransferAsync(
                receiveLink.Id,
                receiveLink.Token,
                plannedUpload.StoredRelativePath,
                TransferKind.FileUpload,
                clientSessionId: string.IsNullOrWhiteSpace(uploadId) ? null : uploadId,
                clientFingerprint,
                remoteAddress,
                totalBytes: expectedTotalBytes,
                bytesSent: 0,
                requesterName: null,
                cancellationToken);

            {
                await using var destinationStream = new FileStream(
                    partialDestinationPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous,
                    });

                var buffer = new byte[ReceiveUploadBufferSizeBytes];
                int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                if (!string.IsNullOrWhiteSpace(uploadId) && chunkSessionStore.IsCanceled(uploadId))
                {
                    throw new ReceiveUploadCanceledException();
                }

                if (receiveLink.MaxTotalBytes > 0 &&
                    receiveLink.BytesReceived + bytesRead > receiveLink.MaxTotalBytes)
                    {
                        throw new ReceiveUploadQuotaExceededException();
                    }

                    var reservedReceiveLink = await coordinator.AddReceivedBytesAsync(receiveLink.Id, bytesRead, cancellationToken);
                    if (reservedReceiveLink is null)
                    {
                        throw new ReceiveUploadUnavailableException();
                    }

                    if (reservedReceiveLink.BytesReceived < receiveLink.BytesReceived + bytesRead)
                    {
                        throw new ReceiveUploadQuotaExceededException();
                    }

                    receiveLink = reservedReceiveLink;
                    reservedBytes += bytesRead;
                    await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesWritten += bytesRead;
                    await coordinator.UpdateTransferProgressAsync(
                        transfer.Id,
                        bytesWritten,
                        cancellationToken,
                        progressBytes: bytesWritten,
                        progressTotalBytes: progressTotalBytes);
                }
            }

            if (expectedTotalBytes > 0 && bytesWritten != expectedTotalBytes)
            {
                throw new ReceiveUploadIncompleteException();
            }

            File.Move(partialDestinationPath, plannedUpload.DestinationPath, overwrite: false);

            await coordinator.MarkTransferCompletedAsync(
                transfer.Id,
                receiveLink.Id,
                receiveLink.Token,
                plannedUpload.StoredRelativePath,
                TransferKind.FileUpload,
                remoteAddress,
                bytesWritten,
                expectedTotalBytes > 0 ? expectedTotalBytes : bytesWritten,
                paused: false,
                succeeded: true,
                countsTowardUsage: false,
                usageSessionKey: null,
                error: null,
                requesterName: null,
                cancellationToken);

            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, plannedUpload.StoredRelativePath, true, null, bytesWritten));
        }
        catch (ReceiveUploadQuotaExceededException)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "This receive link has reached its upload limit.", bytesWritten));
        }
        catch (ReceiveUploadIncompleteException)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "The upload did not complete.", bytesWritten));
        }
        catch (ReceiveUploadUnavailableException)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "The receive link is unavailable.", bytesWritten));
        }
        catch (ReceiveUploadCanceledException)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten, paused: true, error: "Upload stopped.");
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "Upload stopped.", bytesWritten));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, "The upload was interrupted.", bytesWritten));
        }
        catch (Exception exception)
        {
            await RollBackFailedUploadAsync(coordinator, receiveLink.Id, reservedBytes, partialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, bytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(plannedUpload.FileName, plannedUpload.ClientRelativePath, null, false, exception.Message, bytesWritten));
        }
    }

    private static async Task<ReceiveUploadStreamResult> SaveReceiveUploadChunkSectionAsync(
        Stream sourceStream,
        ReceiveUploadChunkMetadata chunkMetadata,
        string fileName,
        long expectedFileSize,
        string clientRelativePath,
        ReceiveLinkRecord receiveLink,
        IShareCoordinator coordinator,
        ReceiveUploadChunkSessionStore chunkSessionStore,
        ReceiveUploadPlanState uploadPlanState,
        string? remoteAddress,
        string? clientFingerprint,
        CancellationToken cancellationToken)
    {
        if (!chunkMetadata.IsValid || expectedFileSize <= 0)
        {
            await sourceStream.CopyToAsync(Stream.Null, cancellationToken);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(fileName, clientRelativePath, null, false, "The upload chunk metadata is invalid.", 0));
        }

        if (chunkSessionStore.IsCanceled(chunkMetadata.UploadId))
        {
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(fileName, clientRelativePath, null, false, "Upload stopped.", 0));
        }

        ReceiveUploadChunkSession? session = null;
        if (chunkMetadata.ChunkIndex == 0)
        {
            var candidate = new ReceiveUploadCandidate(fileName, expectedFileSize, clientRelativePath);
            var (plannedUploads, rejectedUploads) = ReceiveUploadPlanner.Plan(receiveLink.TargetDirectoryPath, [candidate], uploadPlanState);
            if (plannedUploads.Count == 0)
            {
                await sourceStream.CopyToAsync(Stream.Null, cancellationToken);
                return new ReceiveUploadStreamResult(receiveLink, rejectedUploads.FirstOrDefault());
            }

            var plannedUpload = plannedUploads[0];
            var parentDirectoryPath = Path.GetDirectoryName(plannedUpload.DestinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
            {
                Directory.CreateDirectory(parentDirectoryPath);
            }

            var transfer = await coordinator.StartTransferAsync(
                receiveLink.Id,
                receiveLink.Token,
                plannedUpload.StoredRelativePath,
                TransferKind.FileUpload,
                clientSessionId: chunkMetadata.UploadId,
                clientFingerprint,
                remoteAddress,
                totalBytes: expectedFileSize,
                bytesSent: 0,
                requesterName: null,
                cancellationToken);

            session = new ReceiveUploadChunkSession(
                chunkMetadata.UploadId,
                receiveLink.Id,
                receiveLink.Token,
                plannedUpload.FileName,
                plannedUpload.ClientRelativePath,
                plannedUpload.StoredRelativePath,
                plannedUpload.DestinationPath,
                GetPartialUploadPath(plannedUpload.DestinationPath),
                expectedFileSize,
                transfer);

            if (!chunkSessionStore.TryAdd(session))
            {
                await RollBackFailedUploadAsync(coordinator, receiveLink.Id, 0, session.PartialDestinationPath, transfer, receiveLink.Token, plannedUpload.StoredRelativePath, remoteAddress, 0);
                await sourceStream.CopyToAsync(Stream.Null, cancellationToken);
                return new ReceiveUploadStreamResult(
                    receiveLink,
                    new ReceiveUploadFileResult(fileName, clientRelativePath, null, false, "This upload is already in progress.", 0));
            }

            if (chunkSessionStore.IsCanceled(chunkMetadata.UploadId))
            {
                session.Cancel();
            }
        }
        else if (!chunkSessionStore.TryGet(chunkMetadata.UploadId, out session) || session is null)
        {
            await sourceStream.CopyToAsync(Stream.Null, cancellationToken);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(fileName, clientRelativePath, null, false, "The upload session was not found.", 0));
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        var sessionCancellationToken = linkedCancellation.Token;
        await session.Gate.WaitAsync(sessionCancellationToken);
        long chunkBytesWritten = 0;
        try
        {
            if (session.CancellationToken.IsCancellationRequested || chunkSessionStore.IsCanceled(session.UploadId))
            {
                throw new ReceiveUploadCanceledException();
            }

            if (!string.Equals(session.ReceiveLinkId, receiveLink.Id, StringComparison.Ordinal) ||
                session.ExpectedTotalBytes != expectedFileSize ||
                chunkMetadata.ChunkStart != session.BytesWritten)
            {
                throw new ReceiveUploadIncompleteException();
            }

            await using var destinationStream = new FileStream(
                session.PartialDestinationPath,
                new FileStreamOptions
                {
                    Mode = chunkMetadata.ChunkIndex == 0 ? FileMode.CreateNew : FileMode.Open,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                });
            destinationStream.Seek(chunkMetadata.ChunkStart, SeekOrigin.Begin);

            var buffer = new byte[ReceiveUploadBufferSizeBytes];
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), sessionCancellationToken)) > 0)
            {
                if (session.CancellationToken.IsCancellationRequested || chunkSessionStore.IsCanceled(session.UploadId))
                {
                    throw new ReceiveUploadCanceledException();
                }

                if (chunkBytesWritten + bytesRead > chunkMetadata.ChunkSize ||
                    session.BytesWritten + bytesRead > session.ExpectedTotalBytes ||
                    receiveLink.MaxTotalBytes > 0 &&
                    receiveLink.BytesReceived + bytesRead > receiveLink.MaxTotalBytes)
                {
                    throw new ReceiveUploadQuotaExceededException();
                }

                var reservedReceiveLink = await coordinator.AddReceivedBytesAsync(receiveLink.Id, bytesRead, sessionCancellationToken);
                if (reservedReceiveLink is null)
                {
                    throw new ReceiveUploadUnavailableException();
                }

                if (reservedReceiveLink.BytesReceived < receiveLink.BytesReceived + bytesRead)
                {
                    throw new ReceiveUploadQuotaExceededException();
                }

                receiveLink = reservedReceiveLink;
                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), sessionCancellationToken);
                chunkBytesWritten += bytesRead;
                session.BytesWritten += bytesRead;
                await coordinator.UpdateTransferProgressAsync(
                    session.Transfer.Id,
                    session.BytesWritten,
                    sessionCancellationToken,
                    progressBytes: session.BytesWritten,
                    progressTotalBytes: session.ExpectedTotalBytes);
            }

            if (chunkBytesWritten != chunkMetadata.ChunkSize)
            {
                throw new ReceiveUploadIncompleteException();
            }
        }
        catch (ReceiveUploadQuotaExceededException)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "This receive link has reached its upload limit.", session.BytesWritten));
        }
        catch (ReceiveUploadUnavailableException)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "The receive link is unavailable.", session.BytesWritten));
        }
        catch (ReceiveUploadCanceledException)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten, paused: true, error: "Upload stopped.");
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "Upload stopped.", session.BytesWritten));
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten, paused: true, error: "Upload stopped.");
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "Upload stopped.", session.BytesWritten));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "The upload did not complete.", session.BytesWritten));
        }
        finally
        {
            session.Gate.Release();
        }

        if (chunkMetadata.ChunkIndex < chunkMetadata.ChunkCount - 1)
        {
            return new ReceiveUploadStreamResult(receiveLink, null);
        }

        if (session.BytesWritten != session.ExpectedTotalBytes)
        {
            chunkSessionStore.Remove(session.UploadId);
            await RollBackFailedUploadAsync(coordinator, session.ReceiveLinkId, session.BytesWritten, session.PartialDestinationPath, session.Transfer, session.Token, session.StoredRelativePath, remoteAddress, session.BytesWritten);
            return new ReceiveUploadStreamResult(
                receiveLink,
                new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, null, false, "The upload did not complete.", session.BytesWritten));
        }

        File.Move(session.PartialDestinationPath, session.DestinationPath, overwrite: false);
        chunkSessionStore.Remove(session.UploadId);

        await coordinator.MarkTransferCompletedAsync(
            session.Transfer.Id,
            session.ReceiveLinkId,
            session.Token,
            session.StoredRelativePath,
            TransferKind.FileUpload,
            remoteAddress,
            session.BytesWritten,
            session.ExpectedTotalBytes,
            paused: false,
            succeeded: true,
            countsTowardUsage: false,
            usageSessionKey: null,
            error: null,
            requesterName: null,
            cancellationToken);

        return new ReceiveUploadStreamResult(
            receiveLink,
            new ReceiveUploadFileResult(session.FileName, session.ClientRelativePath, session.StoredRelativePath, true, null, session.BytesWritten));
    }

    private static void DisableRequestBodySizeLimit(HttpContext context)
    {
        var maxRequestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySizeFeature is { IsReadOnly: false })
        {
            maxRequestBodySizeFeature.MaxRequestBodySize = null;
        }
    }

    private static bool TryCreateMultipartReader(HttpRequest request, out MultipartReader? multipartReader, out IResult? error)
    {
        multipartReader = null;
        error = null;

        if (string.IsNullOrWhiteSpace(request.ContentType) ||
            !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaTypeHeader) ||
            !string.Equals(mediaTypeHeader.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            error = Results.BadRequest(new { message = "Upload requests must use multipart/form-data." });
            return false;
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaTypeHeader.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            error = Results.BadRequest(new { message = "Multipart boundary is missing." });
            return false;
        }

        multipartReader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = null,
        };
        return true;
    }

    private static bool IsReceiveUploadBinaryChunkRequest(HttpRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ContentType) &&
            MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaTypeHeader) &&
            string.Equals(mediaTypeHeader.MediaType.Value, BinaryChunkContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBinaryChunkHeaders(
        HttpRequest request,
        out string? batchId,
        out string clientRelativePath,
        out string fileName,
        out long expectedFileSize,
        out ReceiveUploadChunkMetadata chunkMetadata,
        out IResult? error)
    {
        batchId = null;
        clientRelativePath = string.Empty;
        fileName = string.Empty;
        expectedFileSize = 0;
        chunkMetadata = default;
        error = null;

        var uploadId = NormalizeReceiveUploadId(ReadHeaderValue(request, UploadIdHeaderName));
        batchId = NormalizeReceiveUploadBatchId(ReadHeaderValue(request, BatchIdHeaderName));
        clientRelativePath = DecodeReceiveUploadHeaderValue(ReadHeaderValue(request, RelativePathHeaderName));
        fileName = Path.GetFileName(DecodeReceiveUploadHeaderValue(ReadHeaderValue(request, FileNameHeaderName)));
        expectedFileSize = ParseReceiveUploadFileSize(ReadHeaderValue(request, FileSizeHeaderName));
        chunkMetadata = new ReceiveUploadChunkMetadata(
            uploadId,
            ParseReceiveUploadInt(ReadHeaderValue(request, ChunkIndexHeaderName)),
            ParseReceiveUploadInt(ReadHeaderValue(request, ChunkCountHeaderName)),
            ParseReceiveUploadLong(ReadHeaderValue(request, ChunkStartHeaderName)),
            ParseReceiveUploadLong(ReadHeaderValue(request, ChunkSizeHeaderName)));

        if (string.IsNullOrWhiteSpace(clientRelativePath))
        {
            error = Results.BadRequest(new { message = "Binary chunk uploads require a relative path." });
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = ResolveFileNameFromRelativePath(clientRelativePath);
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            expectedFileSize <= 0 ||
            !chunkMetadata.IsValid)
        {
            error = Results.BadRequest(new { message = "Binary chunk upload metadata is invalid." });
            return false;
        }

        return true;
    }

    private static bool TryReadWebSocketChunkMessage(
        string value,
        out string? batchId,
        out string clientRelativePath,
        out string fileName,
        out long expectedFileSize,
        out ReceiveUploadChunkMetadata chunkMetadata,
        out string? errorMessage)
    {
        batchId = null;
        clientRelativePath = string.Empty;
        fileName = string.Empty;
        expectedFileSize = 0;
        chunkMetadata = default;
        errorMessage = null;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty) ||
                !string.Equals(typeProperty.GetString(), "chunk", StringComparison.Ordinal))
            {
                errorMessage = "Expected an upload chunk message.";
                return false;
            }

            var uploadId = NormalizeReceiveUploadId(ReadJsonString(root, "uploadId"));
            batchId = NormalizeReceiveUploadBatchId(ReadJsonString(root, "batchId"));
            clientRelativePath = ReadJsonString(root, "relativePath").Trim();
            fileName = Path.GetFileName(ReadJsonString(root, "fileName"));
            expectedFileSize = ReadJsonLong(root, "fileSize");
            chunkMetadata = new ReceiveUploadChunkMetadata(
                uploadId,
                ReadJsonInt(root, "chunkIndex"),
                ReadJsonInt(root, "chunkCount"),
                ReadJsonLong(root, "chunkStart"),
                ReadJsonLong(root, "chunkSize"));
        }
        catch (JsonException)
        {
            errorMessage = "Upload chunk metadata is not valid JSON.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(clientRelativePath))
        {
            errorMessage = "Upload chunks require a relative path.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = ResolveFileNameFromRelativePath(clientRelativePath);
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            expectedFileSize <= 0 ||
            !chunkMetadata.IsValid)
        {
            errorMessage = "Upload chunk metadata is invalid.";
            return false;
        }

        return true;
    }

    private static string ReadJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadJsonInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : -1;
    }

    private static long ReadJsonLong(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : -1;
    }

    private static async Task<string?> ReadWebSocketTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Expected a text metadata message.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<MemoryStream?> ReadWebSocketBinaryMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        var buffer = new byte[ReceiveUploadBufferSizeBytes];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await stream.DisposeAsync();
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await stream.DisposeAsync();
                throw new InvalidDataException("Expected a binary chunk message.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        stream.Position = 0;
        return stream;
    }

    private static Task SendReceiveUploadSocketResponseAsync(WebSocket socket, ReceiveUploadBatchResult response, CancellationToken cancellationToken)
    {
        return SendReceiveUploadSocketJsonAsync(socket, response, cancellationToken);
    }

    private static Task SendReceiveUploadSocketErrorAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        return SendReceiveUploadSocketJsonAsync(
            socket,
            new ReceiveUploadBatchResult(
                0,
                1,
                0,
                [new ReceiveUploadFileResult(string.Empty, string.Empty, null, false, message, 0)]),
            cancellationToken);
    }

    private static async Task SendReceiveUploadSocketJsonAsync<T>(WebSocket socket, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, WebSocketJsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string ReadHeaderValue(HttpRequest request, string name)
    {
        return request.Headers.TryGetValue(name, out var values)
            ? values.ToString()
            : string.Empty;
    }

    private static string DecodeReceiveUploadHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(value.Trim());
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    private static string ResolveFileNameFromRelativePath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? string.Empty;
    }

    private static void RecordSuccessfulReceiveUploads(
        ReceiveLinkRecord receiveLink,
        AppSettings settings,
        INotificationService notificationService,
        ReceiveUploadBatchNotificationTracker batchNotificationTracker,
        string? batchId,
        int successfulUploadCount)
    {
        if (successfulUploadCount <= 0 || !settings.ReceiveNotificationsEnabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(batchId))
        {
            batchNotificationTracker.RecordSuccessfulUploads(receiveLink.Id, batchId, receiveLink.TargetDisplayName, successfulUploadCount);
            return;
        }

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

    private static bool IsMultipartFileSection(ContentDispositionHeaderValue contentDisposition)
    {
        return string.Equals(contentDisposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) &&
            (contentDisposition.FileName.HasValue || contentDisposition.FileNameStar.HasValue);
    }

    private static string ResolveMultipartFileName(ContentDispositionHeaderValue contentDisposition)
    {
        var fileName = HeaderUtilities.RemoveQuotes(contentDisposition.FileNameStar).Value;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = HeaderUtilities.RemoveQuotes(contentDisposition.FileName).Value;
        }

        return string.IsNullOrWhiteSpace(fileName) ? "upload" : Path.GetFileName(fileName);
    }

    private static async Task<string> ReadMultipartFieldValueAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static long ParseReceiveUploadFileSize(string value)
    {
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static int ParseReceiveUploadInt(string value)
    {
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
    }

    private static long ParseReceiveUploadLong(string value)
    {
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
    }

    private static string NormalizeReceiveUploadId(string value)
    {
        value = value.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128
            ? string.Empty
            : value;
    }

    private static bool TryDequeueReceiveUploadChunkMetadata(
        string uploadId,
        Queue<int> chunkIndexes,
        Queue<int> chunkCounts,
        Queue<long> chunkStarts,
        Queue<long> chunkSizes,
        out ReceiveUploadChunkMetadata metadata)
    {
        metadata = default;
        if (chunkIndexes.Count == 0 &&
            chunkCounts.Count == 0 &&
            chunkStarts.Count == 0 &&
            chunkSizes.Count == 0)
        {
            return false;
        }

        metadata = new ReceiveUploadChunkMetadata(
            uploadId,
            chunkIndexes.Count > 0 ? chunkIndexes.Dequeue() : -1,
            chunkCounts.Count > 0 ? chunkCounts.Dequeue() : -1,
            chunkStarts.Count > 0 ? chunkStarts.Dequeue() : -1,
            chunkSizes.Count > 0 ? chunkSizes.Dequeue() : -1);
        return true;
    }

    private static bool TryCreateReceiveUploadProgressEvent(
        string receiveLinkId,
        RuntimeEvent runtimeEvent,
        ReceiveUploadChunkSizingMode configuredChunkSizingMode,
        long configuredChunkSizeBytes,
        long configuredMaxBodySizeBytes,
        int targetChunkSeconds,
        out ReceiveUploadProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (runtimeEvent.Payload is not TransferSnapshot transfer ||
            transfer.ShareId != receiveLinkId ||
            transfer.TransferKind != TransferKind.FileUpload ||
            string.IsNullOrWhiteSpace(transfer.ClientSessionId))
        {
            return false;
        }

        if (runtimeEvent.Type is not (RuntimeEventType.TransferStarted or RuntimeEventType.TransferProgress or RuntimeEventType.TransferCompleted or RuntimeEventType.TransferFailed or RuntimeEventType.TransferPaused))
        {
            return false;
        }

        var recommendedChunkSizeBytes = ResolveRecommendedChunkSizeBytes(transfer, configuredChunkSizingMode, configuredChunkSizeBytes, configuredMaxBodySizeBytes, targetChunkSeconds);
        progressEvent = new ReceiveUploadProgressEvent(
            transfer.ClientSessionId,
            transfer.ProgressBytes,
            transfer.ProgressTotalBytes,
            transfer.State.ToString(),
            transfer.Succeeded,
            transfer.Error,
            recommendedChunkSizeBytes);
        return true;
    }

    private static long ResolveRecommendedChunkSizeBytes(
        TransferSnapshot transfer,
        ReceiveUploadChunkSizingMode configuredChunkSizingMode,
        long configuredChunkSizeBytes,
        long configuredMaxBodySizeBytes,
        int targetChunkSeconds)
    {
        var uploadId = transfer.ClientSessionId ?? string.Empty;
        var configuredPacketSizeBytes = Math.Clamp(
            configuredChunkSizeBytes <= 0 ? Defaults.DefaultReceiveUploadChunkSizeBytes : configuredChunkSizeBytes,
            Defaults.MinimumReceiveUploadChunkSizeBytes,
            MaximumAdaptiveChunkSizeBytes);
        var maxBodySizeBytes = Math.Clamp(
            configuredMaxBodySizeBytes <= 0 ? Defaults.DefaultReceiveUploadMaxBodySizeBytes : configuredMaxBodySizeBytes,
            Defaults.MinimumReceiveUploadChunkSizeBytes,
            MaximumAdaptiveChunkSizeBytes);
        var maxChunkSizeBytes = configuredChunkSizingMode == ReceiveUploadChunkSizingMode.Auto
            ? maxBodySizeBytes
            : Math.Min(configuredPacketSizeBytes, maxBodySizeBytes);

        if (string.IsNullOrWhiteSpace(uploadId) ||
            transfer.State is TransferState.Completed or TransferState.Failed or TransferState.Paused)
        {
            ReceiveUploadSpeedStates.TryRemove(uploadId, out _);
            return maxChunkSizeBytes;
        }

        var nextState = ReceiveUploadSpeedStates.AddOrUpdate(
            uploadId,
            _ => new ReceiveUploadSpeedState(
                transfer.ProgressBytes,
                transfer.LastUpdatedAtUtc,
                null,
                Defaults.MinimumReceiveUploadChunkSizeBytes),
            (_, previous) =>
            {
                var elapsedSeconds = (transfer.LastUpdatedAtUtc - previous.LastUpdatedAtUtc).TotalSeconds;
                var byteDelta = transfer.ProgressBytes - previous.ProgressBytes;
                if (elapsedSeconds <= 0 || byteDelta <= 0)
                {
                    return previous with
                    {
                        ProgressBytes = transfer.ProgressBytes,
                        LastUpdatedAtUtc = transfer.LastUpdatedAtUtc,
                    };
                }

                var instantBytesPerSecond = byteDelta / elapsedSeconds;
                var smoothedBytesPerSecond = previous.SmoothedBytesPerSecond is null
                    ? instantBytesPerSecond
                    : previous.SmoothedBytesPerSecond.Value * (1 - AdaptiveChunkSmoothingFactor) +
                      instantBytesPerSecond * AdaptiveChunkSmoothingFactor;

                var normalizedTargetSeconds = Math.Clamp(
                    targetChunkSeconds <= 0 ? Defaults.DefaultReceiveUploadChunkTargetSeconds : targetChunkSeconds,
                    Defaults.MinimumReceiveUploadChunkTargetSeconds,
                    int.MaxValue);
                var targetBytes = (long)Math.Round(smoothedBytesPerSecond * normalizedTargetSeconds);
                var measuredChunkSizeBytes = Math.Clamp(targetBytes, Defaults.MinimumReceiveUploadChunkSizeBytes, maxChunkSizeBytes);
                var nextRecommendedChunkSizeBytes = measuredChunkSizeBytes > previous.RecommendedChunkSizeBytes
                    ? Math.Min(measuredChunkSizeBytes, Math.Min(maxChunkSizeBytes, previous.RecommendedChunkSizeBytes * AdaptiveChunkGrowthFactor))
                    : measuredChunkSizeBytes;

                return new ReceiveUploadSpeedState(
                    transfer.ProgressBytes,
                    transfer.LastUpdatedAtUtc,
                    smoothedBytesPerSecond,
                    nextRecommendedChunkSizeBytes);
            });

        if (nextState.SmoothedBytesPerSecond is null || nextState.SmoothedBytesPerSecond <= 0)
        {
            return Math.Min(Defaults.MinimumReceiveUploadChunkSizeBytes, maxChunkSizeBytes);
        }

        return Math.Min(nextState.RecommendedChunkSizeBytes, maxChunkSizeBytes);
    }

    private static string GetPartialUploadPath(string destinationPath) => destinationPath + PartialUploadSuffix;

    private static string? NormalizeReceiveUploadBatchId(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 128 ? null : value;
    }

    private sealed record ReceiveUploadStreamResult(
        ReceiveLinkRecord ReceiveLink,
        ReceiveUploadFileResult? Result);

    private readonly record struct ReceiveUploadChunkMetadata(
        string UploadId,
        int ChunkIndex,
        int ChunkCount,
        long ChunkStart,
        long ChunkSize)
    {
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(UploadId) &&
            ChunkIndex >= 0 &&
            ChunkCount > 0 &&
            ChunkIndex < ChunkCount &&
            ChunkStart >= 0 &&
            ChunkSize >= 0;
    }

    private sealed record ReceiveUploadProgressEvent(
        string UploadId,
        long ReceivedBytes,
        long TotalBytes,
        string State,
        bool Succeeded,
        string? Error,
        long RecommendedChunkSizeBytes);

    private sealed record ReceiveUploadSpeedState(
        long ProgressBytes,
        DateTimeOffset LastUpdatedAtUtc,
        double? SmoothedBytesPerSecond,
        long RecommendedChunkSizeBytes);

    private sealed class ReceiveUploadQuotaExceededException : Exception;

    private sealed class ReceiveUploadIncompleteException : Exception;

    private sealed class ReceiveUploadCanceledException : Exception;

    private sealed class ReceiveUploadUnavailableException : Exception;

    private static IResult CreateReceiveUploadBatchResponse(ReceiveUploadBatchResult response)
    {
        return Results.Json(response, statusCode: ResolveReceiveUploadBatchStatusCode(response));
    }

    private static int ResolveReceiveUploadBatchStatusCode(ReceiveUploadBatchResult response)
    {
        if (response.FailedCount <= 0 && response.Results.All(result => result.Success))
        {
            return StatusCodes.Status200OK;
        }

        var failedMessages = response.Results
            .Where(result => !result.Success)
            .Select(result => result.Message ?? string.Empty)
            .ToArray();

        if (failedMessages.Any(message => message.Contains("receive link is unavailable", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCodes.Status410Gone;
        }

        if (failedMessages.Any(message => message.Contains("upload limit", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (failedMessages.Any(IsReceiveUploadConflictMessage))
        {
            return StatusCodes.Status409Conflict;
        }

        if (failedMessages.Any(IsReceiveUploadBadRequestMessage))
        {
            return StatusCodes.Status400BadRequest;
        }

        return StatusCodes.Status500InternalServerError;
    }

    private static bool IsReceiveUploadConflictMessage(string message)
    {
        return message.Contains("upload stopped", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("upload session", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("already in progress", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReceiveUploadBadRequestMessage(string message)
    {
        return message.Contains("metadata is invalid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("did not complete", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("interrupted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("path traverses", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReceiveUploadBatchCompleteRequest(HttpContext context)
    {
        return string.Equals(context.Request.Query["ifs"], "batch-complete", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult HandleReceiveUploadBatchComplete(
        HttpContext context,
        ReceiveLinkRecord receiveLink,
        ReceiveUploadBatchNotificationTracker batchNotificationTracker)
    {
        var batchId = context.Request.Query["batchId"].ToString();
        try
        {
            batchNotificationTracker.TryNotifyCompletedBatch(receiveLink.Id, batchId);
        }
        catch
        {
        }

        return Results.Ok(new { notified = true });
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
        var useBrowserCompression = IsBrowserCompressionRequest(context.Request)
            && ShareFileResponsePolicy.IsBrowserCompressionCandidate(responseFileName, fileResponseMetadata);
        var allowRangeRequestsForResponse = allowRangeRequests && !useBrowserCompression;
        var requestedRange = allowRangeRequestsForResponse ? context.Request.GetTypedHeaders().Range?.Ranges.FirstOrDefault() : null;
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
        if (useBrowserCompression)
        {
            context.Response.ContentType = BinaryChunkContentType;
            context.Response.Headers[BrowserCompressionHeaderName] = GzipCompressionQueryValue;
            context.Response.Headers[UncompressedLengthHeaderName] = file.Length.ToString(CultureInfo.InvariantCulture);

            await using var gzipStream = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            await meteredStream.CopyToAsync(gzipStream, cancellationToken);
            return Results.Empty;
        }

        return Results.File(meteredStream, contentType: fileResponseMetadata.ContentType, enableRangeProcessing: allowRangeRequestsForResponse);
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
        var progressTotalBytes = manifest.Sum(item => item.SizeBytes);
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
        long progressBytes = 0;
        var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var progressTask = TransferProgressTracker.TrackWriteProgressAsync(
            coordinator,
            transfer.Id,
            meteredStream,
            () => Interlocked.Read(ref progressBytes),
            progressTotalBytes,
            progressCancellation.Token);

        var succeeded = false;
        string? error = null;

        try
        {
            await coordinator.UpdateTransferProgressAsync(
                transfer.Id,
                meteredStream.BytesWritten,
                cancellationToken,
                progressBytes,
                progressTotalBytes);

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
                    await CopyToZipEntryAsync(
                        sourceStream,
                        archiveStream,
                        bytesWritten => Interlocked.Add(ref progressBytes, bytesWritten),
                        cancellationToken);
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

    private static async Task CopyToZipEntryAsync(
        Stream sourceStream,
        Stream archiveStream,
        Action<long> reportBytesWritten,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await archiveStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            reportBytesWritten(bytesRead);
        }
    }

    private static bool IsCurrentDirectoryZipRequest(HttpRequest request)
    {
        return string.Equals(request.Query["download"], "zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserCompressionRequest(HttpRequest request)
    {
        return string.Equals(request.Query[CompressionQueryName], GzipCompressionQueryValue, StringComparison.OrdinalIgnoreCase);
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

    private static async Task RollBackFailedUploadAsync(
        IShareCoordinator coordinator,
        string receiveLinkId,
        long reservedBytes,
        string destinationPath,
        TransferSnapshot? transfer,
        string token,
        string storedRelativePath,
        string? remoteAddress,
        long bytesWritten,
        bool paused = false,
        string error = "The upload did not complete.")
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
                transfer.TotalBytes > 0 ? transfer.TotalBytes : reservedBytes,
                paused,
                succeeded: false,
                countsTowardUsage: false,
                usageSessionKey: null,
                error,
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
