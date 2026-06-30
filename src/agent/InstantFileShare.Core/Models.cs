namespace InstantFileShare.Core;

public sealed record ShareRecord
{
    public required string Id { get; init; }
    public required string Token { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public string? Slug { get; init; }
    public required string PublicBaseUrl { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset FileModifiedAtUtc { get; init; }
    public ShareKind ShareKind { get; init; } = ShareKind.File;
    public bool CanBrowseFolderContents { get; init; }
    public bool CanDownloadFolderAsZip { get; init; }
    public FolderShareEntryPoint? PrimaryFolderEntryPoint { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public int? MaxUses { get; init; }
    public int UseCount { get; init; }
    public PublishMode PublishMode { get; init; }
    public ShareState State { get; init; }
    public string? BrokenReason { get; init; }
    public DateTimeOffset? LastAccessedAtUtc { get; init; }
}

public sealed record PublishProfile
{
    public required PublishMode Mode { get; init; }
    public string? BaseUrl { get; init; }
    public string? BindAddress { get; init; }
    public int PublicPort { get; init; } = Defaults.PublicPort;
    public string? CloudflareTunnelName { get; init; }
    public string? CloudflareHostname { get; init; }
    public string? CloudflareConfigPath { get; init; }
    public string? CloudflareToken { get; init; }
    public bool Enabled { get; init; }
}

public sealed record AppSettings
{
    public PublishMode DefaultPublishMode { get; init; } = PublishMode.QuickTunnel;
    public int PublicTokenLength { get; init; } = ShareTokenGenerator.RecommendedLength;
    public int DefaultExpiryValue { get; init; } = 24;
    public ExpiryUnit DefaultExpiryUnit { get; init; } = ExpiryUnit.Hours;
    public int? DefaultMaxUses { get; init; } = null;
    public int DefaultReceiveExpiryValue { get; init; } = 24;
    public ExpiryUnit DefaultReceiveExpiryUnit { get; init; } = ExpiryUnit.Hours;
    public long DefaultReceiveMaxTotalBytes { get; init; } = Defaults.DefaultReceiveMaxTotalBytes;
    public int ReceiveParallelUploadLimit { get; init; } = Defaults.DefaultReceiveParallelUploadLimit;
    public ReceiveUploadMode ReceiveUploadMode { get; init; } = Defaults.DefaultReceiveUploadMode;
    public ReceiveUploadChunkSizingMode ReceiveUploadChunkSizingMode { get; init; } = Defaults.DefaultReceiveUploadChunkSizingMode;
    public long ReceiveUploadChunkSizeBytes { get; init; } = Defaults.DefaultReceiveUploadChunkSizeBytes;
    public long ReceiveUploadMaxBodySizeBytes { get; init; } = Defaults.DefaultReceiveUploadMaxBodySizeBytes;
    public int ReceiveUploadChunkTargetSeconds { get; init; } = Defaults.DefaultReceiveUploadChunkTargetSeconds;
    public string FolderBrowsePageTitle { get; init; } = Defaults.CreateDefaultFolderBrowsePageTitle();
    public string ReceivePageTitle { get; init; } = Defaults.CreateDefaultReceivePageTitle();
    public bool FriendlyUrlsEnabled { get; init; } = true;
    public bool SendMetadataToCrawlers { get; init; } = true;
    public bool OpenImagesInBrowser { get; init; } = true;
    public bool OpenVideosInBrowser { get; init; } = true;
    public bool OpenPdfInBrowser { get; init; } = true;
    public FileChangeBehavior FileChangeBehavior { get; init; } = FileChangeBehavior.Strict;
    public bool KeepAwakeWhileTransferring { get; init; } = true;
    public long? BandwidthLimitBytesPerSecond { get; init; } = null;
    public string? CloudflaredPathOverride { get; init; }
    public bool StartOnLogin { get; init; } = true;
    public bool OpenDashboardOnStart { get; init; } = false;
    public string ManualBindAddress { get; init; } = Defaults.PublicBindAddress;
    public int ManualPublicPort { get; init; } = Defaults.PublicPort;
    public string? ManualBaseUrl { get; init; }
    public int LocalApiPort { get; init; } = Defaults.LocalApiPort;
    public bool ShowLogs { get; init; } = false;
    public bool AddFileContextMenuButton { get; init; } = true;
    public bool AddFolderZipContextMenuButton { get; init; } = true;
    public bool AddFolderBrowseContextMenuButton { get; init; } = true;
    public bool AddFolderReceiveContextMenuButton { get; init; } = true;
    public bool ReceiveNotificationsEnabled { get; init; } = true;
    public FolderShareCapabilityPolicy FolderShareCapabilityPolicy { get; init; } = FolderShareCapabilityPolicy.Exclusive;
    public FolderZipCompressionLevel FolderZipCompressionLevel { get; init; } = FolderZipCompressionLevel.Optimal;
    public int HistoryRetentionValue { get; init; } = 3;
    public HistoryRetentionUnit HistoryRetentionUnit { get; init; } = HistoryRetentionUnit.Months;
    public int HistoryItemsPerPage { get; init; } = 25;
    public int SharesItemsPerPage { get; init; } = 25;
}

public sealed record ReceiveLinkRecord
{
    public required string Id { get; init; }
    public required string Token { get; init; }
    public required string TargetDirectoryPath { get; init; }
    public required string TargetDisplayName { get; init; }
    public required string PublicBaseUrl { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public long MaxTotalBytes { get; init; }
    public long BytesReceived { get; init; }
    public PublishMode PublishMode { get; init; }
    public ReceiveLinkState State { get; init; } = ReceiveLinkState.Active;
    public string? BrokenReason { get; init; }
}

public sealed record ShareListItem
{
    public required string Id { get; init; }
    public required string Token { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public string? Slug { get; init; }
    public required string PublicBaseUrl { get; init; }
    public required string Url { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset? FileModifiedAtUtc { get; init; }
    public required ShareListItemKind ItemKind { get; init; }
    public ShareKind? ShareKind { get; init; }
    public bool CanBrowseFolderContents { get; init; }
    public bool CanDownloadFolderAsZip { get; init; }
    public FolderShareEntryPoint? PrimaryFolderEntryPoint { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public int? MaxUses { get; init; }
    public int UseCount { get; init; }
    public long? MaxTotalBytes { get; init; }
    public long? BytesReceived { get; init; }
    public PublishMode PublishMode { get; init; }
    public required string State { get; init; }
    public string? BrokenReason { get; init; }
    public DateTimeOffset? LastAccessedAtUtc { get; init; }

    public static ShareListItem FromShare(ShareRecord share)
    {
        return new ShareListItem
        {
            Id = share.Id,
            Token = share.Token,
            FileName = share.FileName,
            FilePath = share.FilePath,
            Slug = share.Slug,
            PublicBaseUrl = share.PublicBaseUrl,
            Url = ShareUrlBuilder.Build(share),
            FileSize = share.FileSize,
            FileModifiedAtUtc = share.FileModifiedAtUtc,
            ItemKind = share.ShareKind == InstantFileShare.Core.ShareKind.Folder ? ShareListItemKind.Folder : ShareListItemKind.File,
            ShareKind = share.ShareKind,
            CanBrowseFolderContents = share.CanBrowseFolderContents,
            CanDownloadFolderAsZip = share.CanDownloadFolderAsZip,
            PrimaryFolderEntryPoint = share.PrimaryFolderEntryPoint,
            CreatedAtUtc = share.CreatedAtUtc,
            ExpiresAtUtc = share.ExpiresAtUtc,
            MaxUses = share.MaxUses,
            UseCount = share.UseCount,
            PublishMode = share.PublishMode,
            State = share.State.ToString(),
            BrokenReason = share.BrokenReason,
            LastAccessedAtUtc = share.LastAccessedAtUtc,
        };
    }

    public static ShareListItem FromReceiveLink(ReceiveLinkRecord receiveLink)
    {
        return new ShareListItem
        {
            Id = receiveLink.Id,
            Token = receiveLink.Token,
            FileName = receiveLink.TargetDisplayName,
            FilePath = receiveLink.TargetDirectoryPath,
            PublicBaseUrl = receiveLink.PublicBaseUrl,
            Url = ShareUrlBuilder.BuildReceiveLink(receiveLink.PublicBaseUrl, receiveLink.Token),
            ItemKind = ShareListItemKind.Receive,
            CreatedAtUtc = receiveLink.CreatedAtUtc,
            ExpiresAtUtc = receiveLink.ExpiresAtUtc,
            MaxTotalBytes = receiveLink.MaxTotalBytes,
            BytesReceived = receiveLink.BytesReceived,
            PublishMode = receiveLink.PublishMode,
            State = receiveLink.State.ToString(),
            BrokenReason = receiveLink.BrokenReason,
        };
    }
}

public sealed record CloudflaredState
{
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public CloudflaredOwnership Ownership { get; init; } = CloudflaredOwnership.Unknown;
    public DateTimeOffset? LastCheckedAtUtc { get; init; }
    public string? QuickTunnelUrl { get; init; }
    public bool ManagedTunnelRunning { get; init; }
    public PublishMode? ActiveMode { get; init; }
}

public sealed record TransferSnapshot
{
    public required string Id { get; init; }
    public required string ShareId { get; init; }
    public required string Token { get; init; }
    public required string FileName { get; init; }
    public TransferKind TransferKind { get; init; } = TransferKind.FileDownload;
    public string? RequesterName { get; init; }
    public string? ClientSessionId { get; init; }
    public string? ClientFingerprint { get; init; }
    public string? RemoteAddress { get; init; }
    public long BytesSent { get; init; }
    public long TotalBytes { get; init; }
    public long ProgressBytes { get; init; }
    public long ProgressTotalBytes { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset LastUpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public TransferState State { get; init; }
    public bool IsActive { get; init; }
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
}

public sealed record RuntimeEvent(RuntimeEventType Type, DateTimeOffset OccurredAtUtc, object Payload);

public sealed record RuntimeSnapshot
{
    public required IReadOnlyList<ShareListItem> Shares { get; init; }
    public required IReadOnlyList<TransferSnapshot> Transfers { get; init; }
    public required AppSettings Settings { get; init; }
    public required CloudflaredState Cloudflared { get; init; }
}

public static class Defaults
{
    public const int LocalApiPort = 46430;
    public const int PublicPort = 46431;
    public const string PublicBindAddress = "0.0.0.0";
    public const string NamedPipeName = "InstantFileShare.Agent";
    public const long DefaultReceiveMaxTotalBytes = 10L * 1024 * 1024 * 1024;
    public const int DefaultReceiveParallelUploadLimit = 4;
    public const ReceiveUploadMode DefaultReceiveUploadMode = ReceiveUploadMode.MultipartChunks;
    public const ReceiveUploadChunkSizingMode DefaultReceiveUploadChunkSizingMode = ReceiveUploadChunkSizingMode.Fixed;
    public const long DefaultReceiveUploadChunkSizeBytes = 16L * 1024 * 1024;
    public const long DefaultReceiveUploadMaxBodySizeBytes = 95L * 1024 * 1024;
    public const long MinimumReceiveUploadChunkSizeBytes = 1L * 1024 * 1024;
    public const int DefaultReceiveUploadChunkTargetSeconds = 30;
    public const int MinimumReceiveUploadChunkTargetSeconds = 5;
    public const string RepositoryUrl = "https://github.com/lugui1998/instant-file-share-3";

    public static string CreateDefaultReceivePageTitle()
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName)
            ? "Upload files"
            : $"Upload to {userName}";
    }

    public static string CreateDefaultFolderBrowsePageTitle()
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName)
            ? "Browse files"
            : $"Browse files from {userName}";
    }
}
