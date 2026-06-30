using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class PublicSharePageModelFactory
{
    public PublicSharePageModel BuildFileMetadataPage(
        HttpContext context,
        ShareRecord share,
        string responseFileName,
        FileInfo file,
        ShareFileResponseMetadata fileResponseMetadata)
    {
        var actionVerb = fileResponseMetadata.PreferInline ? "View" : "Download";
        var description = $"{actionVerb} {responseFileName} ({FormatFileSize(file.Length)}). Shared via Instant File Share.";
        var actionLabel = fileResponseMetadata.PreferInline
            ? $"Open this link to view {responseFileName} in your browser or download it."
            : $"Open this link to download {responseFileName}.";

        var currentUrl = BuildCurrentUrl(context);
        return new PublicSharePageModel(
            Kind: "file",
            Title: responseFileName,
            Description: description,
            CanonicalUrl: currentUrl,
            SiteName: "Instant File Share",
            RepositoryUrl: Defaults.RepositoryUrl,
            PrimaryActionLabel: $"{actionVerb} file",
            PrimaryActionUrl: currentUrl,
            File: new PublicShareFileModel(
                responseFileName,
                FormatFileSize(file.Length),
                fileResponseMetadata.PreferInline,
                actionVerb,
                actionLabel,
                CreateBrowserTransferEncryptionExperiment(currentUrl)),
            Folder: null,
            Zip: null,
            Receive: null);
    }

    public PublicSharePageModel BuildFolderZipMetadataPage(
        HttpContext context,
        ShareRecord share,
        FolderSharePathResolver.ResolvedEntry directoryEntry)
    {
        var folderLabel = string.IsNullOrEmpty(directoryEntry.RelativePath)
            ? share.FileName
            : $"{share.FileName} / {directoryEntry.RelativePath.Replace('/', '\\')}";
        var description = $"Download a ZIP archive of {folderLabel}. Shared via Instant File Share.";

        return new PublicSharePageModel(
            Kind: "zip",
            Title: folderLabel,
            Description: description,
            CanonicalUrl: BuildCurrentUrl(context),
            SiteName: "Instant File Share",
            RepositoryUrl: Defaults.RepositoryUrl,
            PrimaryActionLabel: "Download ZIP",
            PrimaryActionUrl: BuildCurrentUrl(context),
            File: null,
            Folder: null,
            Zip: new PublicShareZipModel(
                directoryEntry.Name,
                $"Download a ZIP archive of {folderLabel}."),
            Receive: null);
    }

    public PublicSharePageModel BuildFolderBrowsePage(
        HttpContext context,
        ShareRecord share,
        FolderSharePathResolver.ResolvedEntry directoryEntry,
        IReadOnlyList<FolderSharePathResolver.DirectoryEntry> entries,
        AppSettings settings)
    {
        var description = $"Browse {share.FileName}. Shared via Instant File Share.";
        var browseRootPath = $"/s/{share.Token}";
        var currentRelativePath = directoryEntry.RelativePath;
        var showDownloadAll = share.CanBrowseFolderContents && share.CanDownloadFolderAsZip;
        var downloadAllUrl = showDownloadAll ? $"{BuildCurrentUrl(context)}?download=zip" : null;
        var breadcrumbs = BuildBreadcrumbs(share.FileName, browseRootPath, currentRelativePath);
        var folderEntries = BuildFolderEntries(browseRootPath, currentRelativePath, entries);

        return new PublicSharePageModel(
            Kind: "folder",
            Title: ResolveFolderBrowsePageTitle(settings),
            Description: description,
            CanonicalUrl: BuildCurrentUrl(context),
            SiteName: "Instant File Share",
            RepositoryUrl: Defaults.RepositoryUrl,
            PrimaryActionLabel: showDownloadAll ? "Download All" : null,
            PrimaryActionUrl: downloadAllUrl,
            File: null,
            Folder: new PublicShareFolderModel(
                share.FileName,
                currentRelativePath,
                showDownloadAll,
                downloadAllUrl,
                breadcrumbs,
                folderEntries,
                folderEntries.Count == 0),
            Zip: null,
            Receive: null);
    }

    public PublicSharePageModel BuildReceivePage(HttpContext context, ReceiveLinkRecord receiveLink, AppSettings settings)
    {
        var remainingBytes = receiveLink.MaxTotalBytes > 0
            ? Math.Max(0, receiveLink.MaxTotalBytes - receiveLink.BytesReceived)
            : 0;
        var uploadMode = settings.ReceiveUploadMode == ReceiveUploadMode.AdaptiveBinaryChunks
            ? ReceiveUploadMode.BinaryChunks
            : settings.ReceiveUploadMode;
        var uploadChunkSizingMode = settings.ReceiveUploadMode == ReceiveUploadMode.AdaptiveBinaryChunks
            ? ReceiveUploadChunkSizingMode.Auto
            : settings.ReceiveUploadChunkSizingMode;
        return new PublicSharePageModel(
            Kind: "receive",
            Title: ResolveReceivePageTitle(settings),
            Description: "Upload files through Instant File Share.",
            CanonicalUrl: BuildCurrentUrl(context),
            SiteName: "Instant File Share",
            RepositoryUrl: Defaults.RepositoryUrl,
            PrimaryActionLabel: null,
            PrimaryActionUrl: null,
            File: null,
            Folder: null,
            Zip: null,
            Receive: new PublicShareReceiveModel(
                receiveLink.TargetDisplayName,
                BuildCurrentPath(context),
                $"{BuildCurrentPath(context)}/upload-socket",
                $"{BuildCurrentPath(context)}/events",
                remainingBytes,
                receiveLink.MaxTotalBytes > 0 ? FormatFileSize(remainingBytes) : "Unlimited",
                Math.Max(0, settings.ReceiveParallelUploadLimit),
                uploadMode.ToString(),
                uploadChunkSizingMode.ToString(),
                Math.Max(Defaults.MinimumReceiveUploadChunkSizeBytes, settings.ReceiveUploadChunkSizeBytes),
                Math.Max(Defaults.MinimumReceiveUploadChunkSizeBytes, settings.ReceiveUploadMaxBodySizeBytes),
                Math.Max(
                    Defaults.MinimumReceiveUploadChunkTargetSeconds,
                    settings.ReceiveUploadChunkTargetSeconds <= 0 ? Defaults.DefaultReceiveUploadChunkTargetSeconds : settings.ReceiveUploadChunkTargetSeconds),
                CreateBrowserTransferEncryptionExperiment(BuildCurrentPath(context)),
                receiveLink.ExpiresAtUtc?.ToLocalTime().ToString("g")));
    }

    private static BrowserTransferEncryptionExperimentModel CreateBrowserTransferEncryptionExperiment(string baseUrl)
    {
        return new BrowserTransferEncryptionExperimentModel(
            DownloadManifestUrl: AppendQueryValue(baseUrl, "ifs", "encrypted-download-plan"),
            EncryptedDownloadUrl: AppendQueryValue(baseUrl, "ifs", "encrypted-download"),
            FragmentKeyParameter: "ifs-key",
            Algorithm: "AES-GCM",
            IvStrategy: "96-bit AES-GCM IV: 4 random nonce-prefix bytes plus an 8-byte big-endian chunk index; never reuse an IV with the same key.",
            KeyDelivery: "Prototype keys are passed in the URL fragment so browsers do not include them in HTTP requests.",
            ReceiveUploadModes: ["store-encrypted"]);
    }

    private static string ResolveReceivePageTitle(AppSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.ReceivePageTitle)
            ? Defaults.CreateDefaultReceivePageTitle()
            : settings.ReceivePageTitle.Trim();
    }

    private static string ResolveFolderBrowsePageTitle(AppSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.FolderBrowsePageTitle)
            ? Defaults.CreateDefaultFolderBrowsePageTitle()
            : settings.FolderBrowsePageTitle.Trim();
    }

    private static IReadOnlyList<PublicShareBreadcrumb> BuildBreadcrumbs(string fileName, string browseRootPath, string currentRelativePath)
    {
        var breadcrumbs = new List<PublicShareBreadcrumb>
        {
            new(fileName, browseRootPath),
        };

        if (string.IsNullOrEmpty(currentRelativePath))
        {
            return breadcrumbs;
        }

        var breadcrumbPath = string.Empty;
        foreach (var segment in currentRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            breadcrumbPath = string.IsNullOrEmpty(breadcrumbPath) ? segment : $"{breadcrumbPath}/{segment}";
            breadcrumbs.Add(new PublicShareBreadcrumb(segment, $"{browseRootPath}/{EncodeRelativePath(breadcrumbPath)}"));
        }

        return breadcrumbs;
    }

    private static IReadOnlyList<PublicShareFolderEntryModel> BuildFolderEntries(
        string browseRootPath,
        string currentRelativePath,
        IReadOnlyList<FolderSharePathResolver.DirectoryEntry> entries)
    {
        var result = new List<PublicShareFolderEntryModel>();

        if (!string.IsNullOrEmpty(currentRelativePath))
        {
            var parentPath = currentRelativePath.Contains('/')
                ? currentRelativePath[..currentRelativePath.LastIndexOf('/')]
                : string.Empty;
            var parentHref = string.IsNullOrEmpty(parentPath) ? browseRootPath : $"{browseRootPath}/{EncodeRelativePath(parentPath)}";
            result.Add(new PublicShareFolderEntryModel("..", parentHref, true, string.Empty, null, true));
        }

        foreach (var entry in entries)
        {
            var href = $"{browseRootPath}/{EncodeRelativePath(entry.RelativePath)}";
            result.Add(new PublicShareFolderEntryModel(
                entry.Name,
                href,
                entry.IsDirectory,
                entry.LastModifiedAtUtc.ToLocalTime().ToString("g"),
                entry.IsDirectory ? null : FormatFileSize(entry.Size),
                false));
        }

        return result;
    }

    private static string BuildCurrentUrl(HttpContext context)
    {
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";
    }

    private static string BuildCurrentPath(HttpContext context)
    {
        var path = $"{context.Request.PathBase}{context.Request.Path}";
        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    private static string AppendQueryValue(string url, string name, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
    }

    private static string EncodeRelativePath(string relativePath)
    {
        return string.Join('/', relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string FormatFileSize(long bytes)
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
}
