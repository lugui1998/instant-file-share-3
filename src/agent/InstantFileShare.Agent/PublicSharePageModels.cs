namespace InstantFileShare.Agent;

internal sealed record PublicSharePageModel(
    string Kind,
    string Title,
    string Description,
    string CanonicalUrl,
    string SiteName,
    string? PrimaryActionLabel,
    string? PrimaryActionUrl,
    PublicShareFileModel? File,
    PublicShareFolderModel? Folder,
    PublicShareZipModel? Zip,
    PublicShareReceiveModel? Receive);

internal sealed record PublicShareFileModel(
    string FileName,
    string DisplaySize,
    bool PreferInline,
    string ActionVerb,
    string ActionLabel);

internal sealed record PublicShareFolderModel(
    string Name,
    string RelativePath,
    bool CanDownloadAll,
    string? DownloadAllUrl,
    IReadOnlyList<PublicShareBreadcrumb> Breadcrumbs,
    IReadOnlyList<PublicShareFolderEntryModel> Entries,
    bool IsEmpty);

internal sealed record PublicShareZipModel(
    string FileName,
    string ActionLabel);

internal sealed record PublicShareReceiveModel(
    string TargetName,
    string UploadUrl,
    long RemainingQuotaBytes,
    string RemainingQuotaLabel,
    string? ExpiresAtLabel);

internal sealed record PublicShareBreadcrumb(string Label, string Href);

internal sealed record PublicShareFolderEntryModel(
    string Name,
    string Href,
    bool IsDirectory,
    string ModifiedAtLabel,
    string? SizeLabel,
    bool IsParentDirectory);
