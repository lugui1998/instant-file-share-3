namespace InstantFileShare.Core;

public enum PublishMode
{
    QuickTunnel = 0,
    ManagedCloudflare = 1,
    Manual = 2,
}

public enum ShareState
{
    Active = 0,
    Revoked = 1,
    Expired = 2,
    Broken = 3,
}

public enum FileChangeBehavior
{
    Strict = 0,
    Lenient = 1,
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
}

public enum TransferState
{
    InProgress = 0,
    Paused = 1,
    Completed = 2,
    Failed = 3,
}
