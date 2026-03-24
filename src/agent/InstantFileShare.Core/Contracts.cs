namespace InstantFileShare.Core;

public sealed record CreateShareRequest(string FilePath, PublishMode? PublishMode = null, DateTimeOffset? ExpiresAtUtc = null, int? MaxUses = null);

public sealed record RevokeShareRequest(string ShareId);

public sealed record UpdateSettingsRequest(AppSettings Settings);

public sealed record PipeCommand(string Command, string? FilePath = null);

public sealed record PipeCommandResult(bool Success, string Message, string? ShareUrl = null);

public sealed record CloudflaredDetectionResult(bool Found, string? Path, string? Version, CloudflaredOwnership Ownership, string Message);

public sealed record CloudflaredActionResult(bool Success, string Message);

public sealed record CloudflaredDashboardStatus(
    bool Installed,
    string? ExecutablePath,
    string? InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    CloudflaredOwnership Ownership,
    bool LoggedIn,
    string LoginMessage);

public sealed record CloudflareDomainOption(string ZoneId, string Name);

public sealed record CloudflareManagedStatus(
    bool LoggedIn,
    string Message,
    string? AccountId = null,
    string? ZoneId = null,
    string? ZoneName = null,
    IReadOnlyList<CloudflareDomainOption>? Domains = null,
    string? ConfiguredHostname = null,
    string? ConfiguredTunnelName = null);

public sealed record CreateManagedTunnelRequest(string Domain, string Subdomain);

public sealed record ManagedTunnelProvisionResult(bool Success, string Message, string? Hostname = null, string? TunnelName = null);

public sealed record CloudflareManagedAvailability(
    string Domain,
    string Subdomain,
    string Hostname,
    string TunnelName,
    bool TunnelExists,
    bool HostnameExists,
    string Message);

public sealed record CheckManagedTunnelRequest(string Domain, string Subdomain);
