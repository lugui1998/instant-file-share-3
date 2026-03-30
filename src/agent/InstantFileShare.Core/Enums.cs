namespace InstantFileShare.Core;

public enum PublishMode
{
    QuickTunnel = 0,
    ManagedCloudflare = 1,
    Manual = 2,
}

public enum ShareKind
{
    File = 0,
    Folder = 1,
}

public enum FolderShareEntryPoint
{
    Browse = 0,
    Zip = 1,
}

public enum FolderShareCapabilityPolicy
{
    Exclusive = 0,
    AllowBoth = 1,
}

public enum FolderZipCompressionLevel
{
    Optimal = 0,
    Fastest = 1,
    NoCompression = 2,
    SmallestSize = 3,
}

public enum ShareState
{
    Active = 0,
    Revoked = 1,
    Expired = 2,
    Broken = 3,
}

public enum UsageCountingMode
{
    Never = 0,
    PerSuccessfulTransfer = 1,
    PerSession = 2,
}

public enum FileChangeBehavior
{
    Strict = 0,
    Lenient = 1,
}

public enum ExpiryUnit
{
    Minutes = 0,
    Hours = 1,
    Days = 2,
}

public enum HistoryRetentionUnit
{
    Minutes = 0,
    Hours = 1,
    Days = 2,
    Months = 3,
    Years = 4,
}

public enum CloudflaredOwnership
{
    Unknown = 0,
    External = 1,
    Winget = 2,
}

public enum RuntimeEventType
{
    ShareCreated = 0,
    ShareUpdated = 1,
    ShareRevoked = 2,
    TransferStarted = 3,
    TransferCompleted = 4,
    TransferFailed = 5,
    SettingsUpdated = 6,
    CloudflaredUpdated = 7,
    TransferProgress = 8,
    TransferPaused = 9,
    TransferRemoved = 10,
    TransferHistoryCleared = 11,
}

public enum TransferState
{
    InProgress = 0,
    Paused = 1,
    Completed = 2,
    Failed = 3,
}

public enum TransferKind
{
    FileDownload = 0,
    FolderZipDownload = 1,
    FolderFileDownload = 2,
    MetadataPreview = 3,
}
