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
    public FolderShareCapabilityPolicy FolderShareCapabilityPolicy { get; init; } = FolderShareCapabilityPolicy.Exclusive;
    public FolderZipCompressionLevel FolderZipCompressionLevel { get; init; } = FolderZipCompressionLevel.Optimal;
    public int HistoryRetentionValue { get; init; } = 3;
    public HistoryRetentionUnit HistoryRetentionUnit { get; init; } = HistoryRetentionUnit.Months;
    public int HistoryItemsPerPage { get; init; } = 25;
    public int SharesItemsPerPage { get; init; } = 25;
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
    public required IReadOnlyList<ShareRecord> Shares { get; init; }
    public required IReadOnlyList<TransferSnapshot> Transfers { get; init; }
    public required AppSettings Settings { get; init; }
    public required CloudflaredState Cloudflared { get; init; }
}

public static class Defaults
{
    public const int LocalApiPort = 46430;
    public const int PublicPort = 46431;
    public const string PublicBindAddress = "127.0.0.1";
    public const string NamedPipeName = "InstantFileShare.Agent";
}
