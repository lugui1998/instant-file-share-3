using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using InstantFileShare.Core;

namespace InstantFileShare.Infrastructure;

public sealed partial class CloudflaredSupervisor(FileLogStore logStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex QuickTunnelRegex = QuickTunnelRegexFactory();
    private readonly FileLogStore _logStore = logStore;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private Process? _quickTunnelProcess;
    private Process? _managedTunnelProcess;

    public string? ActiveQuickTunnelUrl { get; private set; }
    public bool ManagedTunnelRunning => _managedTunnelProcess is { HasExited: false };

    public async Task<CloudflaredDetectionResult> DetectAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var candidate = configuredPath;
        var ownership = CloudflaredOwnership.External;

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            candidate = await ResolveFromPathAsync(cancellationToken);
            ownership = CloudflaredOwnership.External;
        }

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            candidate = ResolveFromWingetPackage();
            ownership = CloudflaredOwnership.Winget;
        }

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            return new CloudflaredDetectionResult(false, null, null, CloudflaredOwnership.Unknown, "cloudflared was not found.");
        }

        var version = await ReadVersionAsync(candidate, cancellationToken);
        if (!string.IsNullOrWhiteSpace(candidate) && candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            ownership = CloudflaredOwnership.Winget;
        }

        return new CloudflaredDetectionResult(true, candidate, version, ownership, "cloudflared detected.");
    }

    public async Task<string?> EnsureQuickTunnelAsync(string executablePath, int publicPort, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_quickTunnelProcess is { HasExited: false } && !string.IsNullOrWhiteSpace(ActiveQuickTunnelUrl))
            {
                return ActiveQuickTunnelUrl;
            }

            await StopQuickTunnelAsync();

            var quickTunnelReady = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var process = StartProcess(
                executablePath,
                $"tunnel --url http://127.0.0.1:{publicPort}",
                redirectOutput: true,
                outputHandler: line => _ = HandleCloudflareOutputAsync(line, url => quickTunnelReady.TrySetResult(url)));

            _quickTunnelProcess = process;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var registration = cts.Token.Register(() => quickTunnelReady.TrySetCanceled(cts.Token));

            return await quickTunnelReady.Task.WaitAsync(cts.Token);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StartManagedTunnelAsync(
        string executablePath,
        PublishProfile profile,
        int publicPort,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.CloudflareToken))
        {
            await StartManagedProcessAsync(
                executablePath,
                $"tunnel run --token {profile.CloudflareToken} --url http://127.0.0.1:{publicPort}",
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(profile.CloudflareConfigPath))
        {
            await StartManagedProcessAsync(
                executablePath,
                $"tunnel --config \"{profile.CloudflareConfigPath}\" run",
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(profile.CloudflareTunnelName))
        {
            await StartManagedProcessAsync(
                executablePath,
                $"tunnel --url http://127.0.0.1:{publicPort} run {profile.CloudflareTunnelName}",
                cancellationToken);
        }
    }

    public Task StopQuickTunnelAsync()
    {
        StopProcess(_quickTunnelProcess);
        _quickTunnelProcess = null;
        ActiveQuickTunnelUrl = null;
        return Task.CompletedTask;
    }

    public Task StopManagedTunnelAsync()
    {
        StopProcess(_managedTunnelProcess);
        _managedTunnelProcess = null;
        return Task.CompletedTask;
    }

    public async Task<CloudflaredActionResult> InstallWithWingetAsync(CancellationToken cancellationToken)
    {
        var process = StartProcess("winget", "install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements", redirectOutput: true);
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await LogCloudflareOutputAsync(stdOut, stdErr, cancellationToken);
        return process.ExitCode == 0
            ? new CloudflaredActionResult(true, "cloudflared installed with winget.")
            : new CloudflaredActionResult(false, stdErr);
    }

    public async Task<CloudflaredActionResult> UpdateAsync(string executablePath, CloudflaredOwnership ownership, CancellationToken cancellationToken)
    {
        if (ownership != CloudflaredOwnership.Winget)
        {
            return new CloudflaredActionResult(false, "Automatic updates are only allowed for winget-managed cloudflared installs.");
        }

        var process = StartProcess("winget", "upgrade --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements", redirectOutput: true);
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await LogCloudflareOutputAsync(stdOut, stdErr, cancellationToken);
        return process.ExitCode == 0
            ? new CloudflaredActionResult(true, "cloudflared updated.")
            : new CloudflaredActionResult(false, stdErr);
    }

    public async Task<CloudflaredActionResult> LaunchLoginAsync(string executablePath, CancellationToken cancellationToken)
    {
        var process = StartProcess(executablePath, "tunnel login", redirectOutput: false, useShellExecute: true);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0
            ? new CloudflaredActionResult(true, "Cloudflare login completed.")
            : new CloudflaredActionResult(false, "Cloudflare login flow exited with an error.");
    }

    public async Task<ManagedTunnelProvisionResult> ProvisionManagedTunnelAsync(
        string executablePath,
        string tunnelName,
        string hostname,
        CancellationToken cancellationToken)
    {
        var created = await TryCreateTunnelAsync(executablePath, tunnelName, cancellationToken);
        if (!created.Success)
        {
            return new ManagedTunnelProvisionResult(false, created.Message);
        }

        var routed = await RouteDnsAsync(executablePath, tunnelName, hostname, cancellationToken);
        if (!routed.Success)
        {
            return new ManagedTunnelProvisionResult(false, routed.Message, hostname, tunnelName);
        }

        return new ManagedTunnelProvisionResult(true, "Managed tunnel created and DNS routed.", hostname, tunnelName);
    }

    public async Task<CloudflaredActionResult> DeleteTunnelAsync(
        string executablePath,
        string tunnelName,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            await StopManagedTunnelAsync();
            var tunnelIds = await ResolveTunnelIdsByNameAsync(executablePath, tunnelName, cancellationToken);
            if (tunnelIds.Count == 0)
            {
                return new CloudflaredActionResult(true, $"No existing tunnel named {tunnelName} needed deletion.");
            }

            var failures = new List<string>();
            foreach (var tunnelId in tunnelIds)
            {
                await RunBestEffortAsync(executablePath, $"tunnel cleanup {tunnelId}", cancellationToken);

                var process = StartProcess(executablePath, $"tunnel delete -f {tunnelId}", redirectOutput: true);
                var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await LogCloudflareOutputAsync(stdOut, stdErr, cancellationToken);

                if (process.ExitCode != 0)
                {
                    var error = string.Join(Environment.NewLine, new[] { stdOut, stdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    failures.Add(string.IsNullOrWhiteSpace(error) ? $"Failed to delete tunnel {tunnelId}." : error.Trim());
                }
            }

            return failures.Count == 0
                ? new CloudflaredActionResult(true, $"Deleted tunnel {tunnelName}.")
                : new CloudflaredActionResult(false, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            _sync.Release();
        }
    }

    private static string? ResolveFromWingetPackage()
    {
        try
        {
            var packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");

            if (!Directory.Exists(packagesRoot))
            {
                return null;
            }

            var packageDirectory = Directory.EnumerateDirectories(packagesRoot, "Cloudflare.cloudflared_*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (packageDirectory is null)
            {
                return null;
            }

            var executablePath = Path.Combine(packageDirectory, "cloudflared.exe");
            return File.Exists(executablePath) ? executablePath : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task StartManagedProcessAsync(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        await StopManagedTunnelAsync();
        _managedTunnelProcess = StartProcess(
            executablePath,
            arguments,
            redirectOutput: true,
            outputHandler: line => _ = _logStore.AppendCloudflareAsync(line, CancellationToken.None));
        await Task.Delay(500, cancellationToken);
    }

    private async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        var process = StartProcess(executablePath, "version", redirectOutput: true);
        await process.WaitForExitAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private async Task<string?> ResolveFromPathAsync(CancellationToken cancellationToken)
    {
        var process = StartProcess("where.exe", "cloudflared", redirectOutput: true);
        await process.WaitForExitAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }

        process.Dispose();
    }

    private Process StartProcess(string fileName, string arguments, bool redirectOutput, bool useShellExecute = false, Action<string>? outputHandler = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = useShellExecute,
                RedirectStandardOutput = redirectOutput && !useShellExecute,
                RedirectStandardError = redirectOutput && !useShellExecute,
                CreateNoWindow = !useShellExecute,
            },
        };

        if (redirectOutput && !useShellExecute && outputHandler is not null)
        {
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    outputHandler?.Invoke(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    outputHandler?.Invoke(args.Data);
                }
            };
        }

        process.Start();
        if (redirectOutput && !useShellExecute && outputHandler is not null)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    [GeneratedRegex(@"https:\/\/[a-z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelRegexFactory();

    private async Task<IReadOnlyList<string>> ResolveTunnelIdsByNameAsync(
        string executablePath,
        string tunnelName,
        CancellationToken cancellationToken)
    {
        var process = StartProcess(executablePath, "tunnel list --output json", redirectOutput: true);
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var jsonLine = stdOut
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            jsonLine = stdOut.Trim();
        }

        try
        {
            var tunnels = JsonSerializer.Deserialize<List<CloudflaredTunnelListItem>>(jsonLine, JsonOptions) ?? [];
            return tunnels
                .Where(item => string.Equals(item.Name, tunnelName, StringComparison.OrdinalIgnoreCase) &&
                               (string.IsNullOrWhiteSpace(item.DeletedAt) || item.DeletedAt == "0001-01-01T00:00:00Z"))
                .Select(item => item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<bool> TunnelExistsAsync(
        string executablePath,
        string tunnelName,
        CancellationToken cancellationToken)
    {
        var ids = await ResolveTunnelIdsByNameAsync(executablePath, tunnelName, cancellationToken);
        return ids.Count > 0;
    }

    private async Task RunBestEffortAsync(string executablePath, string arguments, CancellationToken cancellationToken)
    {
        var process = StartProcess(executablePath, arguments, redirectOutput: true);
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await LogCloudflareOutputAsync(stdOut, stdErr, cancellationToken);
    }

    private async Task<(bool Success, string Message, string? Token)> TryCreateTunnelAsync(
        string executablePath,
        string tunnelName,
        CancellationToken cancellationToken)
    {
        var createProcess = StartProcess(executablePath, $"tunnel create --output json {tunnelName}", redirectOutput: true);
        var createStdOut = await createProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var createStdErr = await createProcess.StandardError.ReadToEndAsync(cancellationToken);
        await createProcess.WaitForExitAsync(cancellationToken);

        if (createProcess.ExitCode == 0)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<CloudflaredCreateTunnelResponse>(createStdOut, JsonOptions);
                if (payload is not null && !string.IsNullOrWhiteSpace(payload.Token))
                {
                    return (true, "Tunnel created.", payload.Token);
                }
            }
            catch
            {
            }
        }

        var tokenProcess = StartProcess(executablePath, $"tunnel token {tunnelName}", redirectOutput: true);
        var tokenStdOut = await tokenProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var tokenStdErr = await tokenProcess.StandardError.ReadToEndAsync(cancellationToken);
        await tokenProcess.WaitForExitAsync(cancellationToken);

        if (tokenProcess.ExitCode == 0)
        {
            var token = tokenStdOut.Trim();
            return string.IsNullOrWhiteSpace(token)
                ? (false, "Tunnel exists, but its token could not be fetched.", null)
                : (true, "Tunnel already existed. Reusing it.", token);
        }

        var error = string.Join(Environment.NewLine, new[] { createStdErr, tokenStdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return (false, string.IsNullOrWhiteSpace(error) ? $"Failed to create tunnel {tunnelName}." : error.Trim(), null);
    }

    private async Task<(bool Success, string Message)> RouteDnsAsync(
        string executablePath,
        string tunnelName,
        string hostname,
        CancellationToken cancellationToken)
    {
        var process = StartProcess(executablePath, $"tunnel route dns --overwrite-dns {tunnelName} {hostname}", redirectOutput: true);
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            return (true, string.IsNullOrWhiteSpace(stdOut) ? "DNS routed." : stdOut.Trim());
        }

        var error = string.Join(Environment.NewLine, new[] { stdOut, stdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return (false, string.IsNullOrWhiteSpace(error) ? $"Failed to route DNS for {hostname}." : error.Trim());
    }

    private sealed record CloudflaredCreateTunnelResponse(string Id, string Name, string Token);
    private sealed record CloudflaredTunnelListItem(string Id, string Name, string DeletedAt);

    private async Task HandleCloudflareOutputAsync(string line, Action<string?>? quickTunnelUrlSetter)
    {
        await _logStore.AppendCloudflareAsync(line, CancellationToken.None);
        var match = QuickTunnelRegex.Match(line);
        if (match.Success)
        {
            ActiveQuickTunnelUrl = match.Value;
            quickTunnelUrlSetter?.Invoke(match.Value);
        }
    }

    private async Task LogCloudflareOutputAsync(string stdOut, string stdErr, CancellationToken cancellationToken)
    {
        foreach (var line in (stdOut + Environment.NewLine + stdErr).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            await _logStore.AppendCloudflareAsync(line, cancellationToken);
        }
    }
}
