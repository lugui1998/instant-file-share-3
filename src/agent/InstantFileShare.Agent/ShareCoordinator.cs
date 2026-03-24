using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InstantFileShare.Core;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal sealed class ShareCoordinator(
    IShareStore shareStore,
    IRuntimeEventStream runtimeEventStream,
    CloudflaredSupervisor cloudflaredSupervisor,
    ExternalAddressResolver externalAddressResolver,
    IStartupRegistrationService startupRegistrationService,
    IContextMenuRegistrationService contextMenuRegistrationService,
    IHttpClientFactory httpClientFactory) : IShareCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<TransferSnapshot> _transfers = [];

    public async Task<(ShareRecord Share, string Url)> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.FilePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The selected file does not exist.", request.FilePath);
        }

        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var mode = request.PublishMode ?? settings.DefaultPublishMode;
        var publicBaseUrl = await EnsureTunnelBaseUrlAsync(mode, cancellationToken)
            ?? throw new InvalidOperationException(GetMissingPublishModeMessage(mode));

        var share = new ShareRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Token = ShareTokenGenerator.Generate(),
            FilePath = fileInfo.FullName,
            FileName = fileInfo.Name,
            Slug = settings.FriendlyUrlsEnabled ? FileNameSlug.Create(fileInfo.Name) : null,
            PublicBaseUrl = publicBaseUrl,
            FileSize = fileInfo.Length,
            FileModifiedAtUtc = fileInfo.LastWriteTimeUtc,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(settings.DefaultExpiryHours),
            MaxUses = request.MaxUses ?? settings.DefaultMaxUses,
            UseCount = 0,
            PublishMode = mode,
            State = ShareState.Active,
        };

        await shareStore.AddShareAsync(share, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareCreated, DateTimeOffset.UtcNow, share), cancellationToken);
        return (share, ShareUrlBuilder.Build(publicBaseUrl, share.Token, share.Slug));
    }

    public Task<IReadOnlyList<ShareRecord>> ListSharesAsync(CancellationToken cancellationToken) => shareStore.ListSharesAsync(cancellationToken);

    public async Task<ShareRecord?> ResolveDownloadAsync(string token, CancellationToken cancellationToken)
    {
        var share = await shareStore.GetShareByTokenAsync(token, cancellationToken);
        if (share is null)
        {
            return null;
        }

        if (share.State is ShareState.Revoked or ShareState.Broken or ShareState.Expired)
        {
            return share;
        }

        if (share.ExpiresAtUtc is not null && share.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            share = share with { State = ShareState.Expired };
            await shareStore.UpdateShareAsync(share, cancellationToken);
            return share;
        }

        if (share.MaxUses is not null && share.UseCount >= share.MaxUses)
        {
            return share with { State = ShareState.Expired, BrokenReason = "Maximum number of uses reached." };
        }

        var fileInfo = new FileInfo(share.FilePath);
        if (!fileInfo.Exists)
        {
            share = share with { State = ShareState.Broken, BrokenReason = "The file is no longer available." };
            await shareStore.UpdateShareAsync(share, cancellationToken);
            await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareUpdated, DateTimeOffset.UtcNow, share), cancellationToken);
            return share;
        }

        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        if (settings.FileChangeBehavior == FileChangeBehavior.Strict &&
            (fileInfo.Length != share.FileSize || fileInfo.LastWriteTimeUtc != share.FileModifiedAtUtc.UtcDateTime))
        {
            share = share with { State = ShareState.Broken, BrokenReason = "The shared file changed after the link was created." };
            await shareStore.UpdateShareAsync(share, cancellationToken);
            await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareUpdated, DateTimeOffset.UtcNow, share), cancellationToken);
            return share;
        }

        return share with { LastAccessedAtUtc = DateTimeOffset.UtcNow };
    }

    public async Task RevokeShareAsync(string shareId, CancellationToken cancellationToken)
    {
        var share = await shareStore.GetShareByIdAsync(shareId, cancellationToken);
        if (share is null)
        {
            return;
        }

        share = share with { State = ShareState.Revoked };
        await shareStore.UpdateShareAsync(share, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.ShareRevoked, DateTimeOffset.UtcNow, share), cancellationToken);
    }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => shareStore.GetSettingsAsync(cancellationToken);

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await shareStore.SaveSettingsAsync(settings, cancellationToken);
        startupRegistrationService.Apply(settings.StartOnLogin);
        await contextMenuRegistrationService.ApplyAsync(settings.AddFileContextMenuButton, cancellationToken);
        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.SettingsUpdated, DateTimeOffset.UtcNow, settings), cancellationToken);
    }

    public Task<IReadOnlyList<PublishProfile>> GetPublishProfilesAsync(CancellationToken cancellationToken)
        => shareStore.GetPublishProfilesAsync(cancellationToken);

    public Task SavePublishProfileAsync(PublishProfile profile, CancellationToken cancellationToken)
        => shareStore.SavePublishProfileAsync(profile, cancellationToken);

    public Task<IReadOnlyList<TransferSnapshot>> GetTransfersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TransferSnapshot>>(_transfers.OrderByDescending(entry => entry.StartedAtUtc).ToList());

    public async Task MarkTransferCompletedAsync(string shareId, string token, string fileName, string? remoteAddress, long bytesSent, long totalBytes, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var transfer = new TransferSnapshot
        {
            ShareId = shareId,
            Token = token,
            FileName = fileName,
            RemoteAddress = remoteAddress,
            BytesSent = bytesSent,
            TotalBytes = totalBytes,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Succeeded = succeeded,
            Error = error,
        };

        _transfers.Add(transfer);
        if (_transfers.Count > 100)
        {
            _transfers.RemoveRange(0, _transfers.Count - 100);
        }

        var share = await shareStore.GetShareByIdAsync(shareId, cancellationToken);
        if (share is not null && succeeded && bytesSent >= totalBytes)
        {
            share = share with { UseCount = share.UseCount + 1, LastAccessedAtUtc = DateTimeOffset.UtcNow };
            await shareStore.UpdateShareAsync(share, cancellationToken);
        }

        await runtimeEventStream.PublishAsync(
            new RuntimeEvent(
                succeeded ? RuntimeEventType.TransferCompleted : RuntimeEventType.TransferFailed,
                DateTimeOffset.UtcNow,
                transfer),
            cancellationToken);
    }

    public Task<CloudflaredState> GetCloudflaredStateAsync(CancellationToken cancellationToken)
        => shareStore.GetCloudflaredStateAsync(cancellationToken);

    public async Task<CloudflaredDetectionResult> DetectCloudflaredAsync(CancellationToken cancellationToken)
        => await DetectCloudflaredCoreAsync(publishEvent: true, cancellationToken);

    private async Task<CloudflaredDetectionResult> DetectCloudflaredCoreAsync(bool publishEvent, CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var detection = await cloudflaredSupervisor.DetectAsync(settings.CloudflaredPathOverride, cancellationToken);
        var currentState = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        currentState = currentState with
        {
            ExecutablePath = detection.Path,
            Version = detection.Version,
            Ownership = detection.Ownership,
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
        };

        await shareStore.SaveCloudflaredStateAsync(currentState, cancellationToken);
        if (publishEvent)
        {
            await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.CloudflaredUpdated, DateTimeOffset.UtcNow, currentState), cancellationToken);
        }

        return detection;
    }

    public async Task<string?> EnsureTunnelBaseUrlAsync(PublishMode mode, CancellationToken cancellationToken)
    {
        var settings = await shareStore.GetSettingsAsync(cancellationToken);
        var profiles = await shareStore.GetPublishProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(entry => entry.Mode == mode) ?? new PublishProfile { Mode = mode, Enabled = true };

        return mode switch
        {
            PublishMode.QuickTunnel => await EnsureQuickTunnelUrlAsync(settings, cancellationToken),
            PublishMode.ManagedCloudflare => await EnsureManagedUrlAsync(profile, settings, cancellationToken),
            PublishMode.Manual => await EnsureManualUrlAsync(profile, settings, cancellationToken),
            _ => null,
        };
    }

    public async Task<CloudflaredActionResult> InstallCloudflaredAsync(CancellationToken cancellationToken)
    {
        var result = await cloudflaredSupervisor.InstallWithWingetAsync(cancellationToken);
        if (result.Success)
        {
            await DetectCloudflaredAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CloudflaredActionResult> UpdateCloudflaredAsync(CancellationToken cancellationToken)
    {
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(state.ExecutablePath))
        {
            return new CloudflaredActionResult(false, "cloudflared is not available.");
        }

        var result = await cloudflaredSupervisor.UpdateAsync(state.ExecutablePath, state.Ownership, cancellationToken);
        if (result.Success)
        {
            await DetectCloudflaredAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CloudflaredActionResult> StartManagedTunnelLoginAsync(CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return new CloudflaredActionResult(false, "cloudflared is not available.");
        }

        return await cloudflaredSupervisor.LaunchLoginAsync(detection.Path, cancellationToken);
    }

    public async Task<CloudflaredActionResult> LogoutCloudflareAsync(CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        if (File.Exists(certPath))
        {
            File.Delete(certPath);
        }

        await Task.CompletedTask;
        return new CloudflaredActionResult(true, "Logged out locally. Existing managed tunnel settings were kept, but you must log in again to manage them.");
    }

    public async Task<CloudflaredDashboardStatus> GetCloudflaredDashboardStatusAsync(CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredCoreAsync(publishEvent: false, cancellationToken);
        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        var latestVersion = await TryGetLatestCloudflaredVersionAsync(cancellationToken);
        var installedVersion = ExtractCloudflaredVersion(detection.Version);
        var updateAvailable = installedVersion is not null &&
                              latestVersion is not null &&
                              CompareVersions(installedVersion, latestVersion) < 0;

        return new CloudflaredDashboardStatus(
            detection.Found,
            detection.Path,
            installedVersion,
            latestVersion,
            updateAvailable,
            detection.Ownership,
            managedStatus.LoggedIn,
            managedStatus.Message);
    }

    public async Task<CloudflareManagedStatus> GetManagedCloudflareStatusAsync(CancellationToken cancellationToken)
    {
        var profile = (await shareStore.GetPublishProfilesAsync(cancellationToken))
            .FirstOrDefault(entry => entry.Mode == PublishMode.ManagedCloudflare);

        var certToken = await TryReadCloudflareLoginTokenAsync(cancellationToken);
        if (certToken is null)
        {
            return new CloudflareManagedStatus(
                false,
                "Not logged in. Run Start Cloudflare Login to authorize a zone.",
                ConfiguredHostname: profile?.CloudflareHostname,
                ConfiguredTunnelName: profile?.CloudflareTunnelName);
        }

        var zoneName = await TryResolveZoneNameAsync(certToken, cancellationToken);
        var domains = zoneName is null
            ? Array.Empty<CloudflareDomainOption>()
            : [new CloudflareDomainOption(certToken.ZoneId, zoneName)];

        return new CloudflareManagedStatus(
            true,
            zoneName is null ? "Logged in, but the zone name could not be resolved." : $"Logged in for zone {zoneName}.",
            certToken.AccountId,
            certToken.ZoneId,
            zoneName,
            domains,
            profile?.CloudflareHostname,
            profile?.CloudflareTunnelName);
    }

    public async Task<CloudflareManagedAvailability> CheckManagedTunnelAvailabilityAsync(CheckManagedTunnelRequest request, CancellationToken cancellationToken)
    {
        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        var selectedDomain = request.Domain.Trim().Trim('.');
        var subdomain = SanitizeSubdomain(request.Subdomain);
        var hostname = $"{subdomain}.{selectedDomain}";
        var tunnelName = $"ifs-{subdomain}-{selectedDomain.Replace('.', '-')}".ToLowerInvariant();

        if (!managedStatus.LoggedIn)
        {
            return new CloudflareManagedAvailability(
                selectedDomain,
                subdomain,
                hostname,
                tunnelName,
                false,
                false,
                managedStatus.Message);
        }

        var availableDomains = managedStatus.Domains ?? Array.Empty<CloudflareDomainOption>();
        if (availableDomains.Count > 0 && !availableDomains.Any(domain => string.Equals(domain.Name, selectedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return new CloudflareManagedAvailability(
                selectedDomain,
                subdomain,
                hostname,
                tunnelName,
                false,
                false,
                $"The current login is not authorized for {selectedDomain}.");
        }

        var detection = await DetectCloudflaredAsync(cancellationToken);
        var tunnelExists = detection.Found && !string.IsNullOrWhiteSpace(detection.Path)
            ? await cloudflaredSupervisor.TunnelExistsAsync(detection.Path, tunnelName, cancellationToken)
            : false;
        var hostnameExists = managedStatus.ZoneId is not null &&
                             await HostnameRecordExistsAsync(managedStatus.ZoneId, hostname, cancellationToken);

        var message = $"Tunnel {(tunnelExists ? "exists" : "does not exist")}; hostname {(hostnameExists ? "already exists" : "does not exist")} for {hostname}.";

        return new CloudflareManagedAvailability(
            selectedDomain,
            subdomain,
            hostname,
            tunnelName,
            tunnelExists,
            hostnameExists,
            message);
    }

    public async Task<ManagedTunnelProvisionResult> CreateManagedTunnelAsync(CreateManagedTunnelRequest request, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return new ManagedTunnelProvisionResult(false, "cloudflared is not available.");
        }

        var managedStatus = await GetManagedCloudflareStatusAsync(cancellationToken);
        if (!managedStatus.LoggedIn)
        {
            return new ManagedTunnelProvisionResult(false, managedStatus.Message);
        }

        var selectedDomain = request.Domain.Trim().Trim('.');
        var availableDomains = managedStatus.Domains ?? Array.Empty<CloudflareDomainOption>();
        if (availableDomains.Count > 0 && !availableDomains.Any(domain => string.Equals(domain.Name, selectedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            return new ManagedTunnelProvisionResult(false, $"The current login is not authorized for {selectedDomain}.");
        }

        var subdomain = SanitizeSubdomain(request.Subdomain);
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return new ManagedTunnelProvisionResult(false, "A valid subdomain is required.");
        }

        var hostname = $"{subdomain}.{selectedDomain}";
        var tunnelName = $"ifs-{subdomain}-{selectedDomain.Replace('.', '-')}".ToLowerInvariant();
        var existingProfile = (await shareStore.GetPublishProfilesAsync(cancellationToken))
            .FirstOrDefault(entry => entry.Mode == PublishMode.ManagedCloudflare);

        if (existingProfile is not null &&
            !string.IsNullOrWhiteSpace(existingProfile.CloudflareTunnelName) &&
            !string.Equals(existingProfile.CloudflareTunnelName, tunnelName, StringComparison.OrdinalIgnoreCase))
        {
            var deleteResult = await cloudflaredSupervisor.DeleteTunnelAsync(detection.Path, existingProfile.CloudflareTunnelName, cancellationToken);
            if (!deleteResult.Success)
            {
                return new ManagedTunnelProvisionResult(false, $"Failed to replace the previous managed tunnel. {deleteResult.Message}");
            }
        }

        var provisionResult = await cloudflaredSupervisor.ProvisionManagedTunnelAsync(detection.Path, tunnelName, hostname, cancellationToken);
        if (!provisionResult.Success)
        {
            return provisionResult;
        }

        await shareStore.SavePublishProfileAsync(new PublishProfile
        {
            Mode = PublishMode.ManagedCloudflare,
            Enabled = true,
            BaseUrl = $"https://{hostname}",
            PublicPort = Defaults.PublicPort,
            BindAddress = "127.0.0.1",
            CloudflareHostname = hostname,
            CloudflareTunnelName = tunnelName,
            CloudflareToken = await FetchManagedTunnelTokenAsync(detection.Path, tunnelName, cancellationToken),
        }, cancellationToken);

        await runtimeEventStream.PublishAsync(new RuntimeEvent(RuntimeEventType.SettingsUpdated, DateTimeOffset.UtcNow, hostname), cancellationToken);
        return provisionResult with
        {
            Message = $"Managed tunnel ready for https://{hostname}",
        };
    }

    public async Task<RuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        return new RuntimeSnapshot
        {
            Shares = await shareStore.ListSharesAsync(cancellationToken),
            Transfers = await GetTransfersAsync(cancellationToken),
            Settings = await shareStore.GetSettingsAsync(cancellationToken),
            Cloudflared = await shareStore.GetCloudflaredStateAsync(cancellationToken),
        };
    }

    private async Task<string?> EnsureQuickTunnelUrlAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return null;
        }

        var url = await cloudflaredSupervisor.EnsureQuickTunnelAsync(detection.Path, settings.ManualPublicPort, cancellationToken);
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        await shareStore.SaveCloudflaredStateAsync(state with
        {
            QuickTunnelUrl = url,
            ActiveMode = PublishMode.QuickTunnel,
        }, cancellationToken);
        return url;
    }

    private async Task<string?> EnsureManagedUrlAsync(PublishProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        var detection = await DetectCloudflaredAsync(cancellationToken);
        if (!detection.Found || string.IsNullOrWhiteSpace(detection.Path))
        {
            return null;
        }

        await cloudflaredSupervisor.StartManagedTunnelAsync(detection.Path, profile, settings.ManualPublicPort, cancellationToken);
        var state = await shareStore.GetCloudflaredStateAsync(cancellationToken);
        await shareStore.SaveCloudflaredStateAsync(state with
        {
            ManagedTunnelRunning = cloudflaredSupervisor.ManagedTunnelRunning,
            ActiveMode = PublishMode.ManagedCloudflare,
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return profile.BaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(profile.CloudflareHostname))
        {
            return $"https://{profile.CloudflareHostname.TrimStart('/')}";
        }

        return null;
    }

    private async Task<string> EnsureManualUrlAsync(PublishProfile profile, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.ManualBaseUrl))
        {
            return settings.ManualBaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return profile.BaseUrl.TrimEnd('/');
        }

        var publicIp = await externalAddressResolver.TryGetPublicIpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(publicIp))
        {
            return $"http://{publicIp}:{settings.ManualPublicPort}";
        }

        var localIp = await externalAddressResolver.TryGetLocalIpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(localIp))
        {
            return $"http://{localIp}:{settings.ManualPublicPort}";
        }

        return $"http://127.0.0.1:{settings.ManualPublicPort}";
    }

    private static string GetMissingPublishModeMessage(PublishMode mode)
    {
        return mode switch
        {
            PublishMode.QuickTunnel => "Quick Tunnel mode requires cloudflared. Install it from Diagnostics or set an explicit cloudflared path in Settings.",
            PublishMode.ManagedCloudflare => "Managed Cloudflare mode requires cloudflared plus a configured managed tunnel profile.",
            PublishMode.Manual => "Manual mode could not resolve a usable base URL.",
            _ => "No public URL is available for the selected publish mode.",
        };
    }

    private async Task<CloudflareLoginToken?> TryReadCloudflareLoginTokenAsync(CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        if (!File.Exists(certPath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(certPath, cancellationToken);
        var payload = string.Concat(lines.Where(line => !line.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) && !line.Contains("END", StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonSerializer.Deserialize<CloudflareLoginToken>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryResolveZoneNameAsync(CloudflareLoginToken token, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{token.ZoneId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.ApiToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CloudflareZoneResponse>(stream, JsonOptions, cancellationToken);
            return payload?.Result?.Name ?? await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
        }
        catch
        {
            return await TryResolveZoneNameWithPowerShellAsync(token, cancellationToken);
        }
    }

    private async Task<string?> FetchManagedTunnelTokenAsync(string executablePath, string tunnelName, CancellationToken cancellationToken)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"tunnel token {tunnelName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private async Task<string?> TryGetLatestCloudflaredVersionAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/cloudflare/cloudflared/releases/latest");
        request.Headers.UserAgent.ParseAdd("InstantFileShare/1.0");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, cancellationToken);
            return ExtractCloudflaredVersion(payload?.TagName);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeSubdomain(string value)
    {
        var candidate = value.Trim().Trim('.').ToLowerInvariant();
        var normalized = new string(candidate.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "share" : normalized;
    }

    private static string? ExtractCloudflaredVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\d+\.\d+\.\d+");
        return match.Success ? match.Value : null;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(value => int.TryParse(value, out var parsed) ? parsed : 0).ToArray();
        var rightParts = right.Split('.').Select(value => int.TryParse(value, out var parsed) ? parsed : 0).ToArray();
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Length ? leftParts[index] : 0;
            var rightValue = index < rightParts.Length ? rightParts[index] : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private static async Task<string?> TryResolveZoneNameWithPowerShellAsync(CloudflareLoginToken token, CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
        var escapedCertPath = certPath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$certPath = '" + escapedCertPath + "'; " +
            "$content = Get-Content $certPath | Where-Object { $_ -notmatch 'BEGIN|END' }; " +
            "$json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($content -join ''))); " +
            "$obj = $json | ConvertFrom-Json; " +
            "$headers = @{ Authorization = \"Bearer $($obj.apiToken)\" }; " +
            "$zone = Invoke-RestMethod -Headers $headers -Uri \"https://api.cloudflare.com/client/v4/zones/$($obj.zoneID)\" -Method Get; " +
            "$zone.result.name";

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return null;
        }

        var zoneName = output.Trim();
        return string.IsNullOrWhiteSpace(zoneName) ? null : zoneName;
    }

    private async Task<bool> HostnameRecordExistsAsync(string zoneId, string hostname, CancellationToken cancellationToken)
    {
        var certToken = await TryReadCloudflareLoginTokenAsync(cancellationToken);
        if (certToken is null)
        {
            return false;
        }

        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?name={Uri.EscapeDataString(hostname)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", certToken.ApiToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CloudflareDnsListResponse>(stream, JsonOptions, cancellationToken);
            return payload?.Result?.Any(record => string.Equals(record.Name, hostname, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CloudflareLoginToken(string ZoneId, string AccountId, string ApiToken);

    private sealed record CloudflareZoneResponse(CloudflareZoneResult? Result);

    private sealed record CloudflareZoneResult(string Id, string Name);

    private sealed record CloudflareDnsListResponse(IReadOnlyList<CloudflareDnsRecord>? Result);

    private sealed record CloudflareDnsRecord(string Id, string Name, string Type, string Content);

    private sealed record GitHubReleaseResponse(string TagName);
}
