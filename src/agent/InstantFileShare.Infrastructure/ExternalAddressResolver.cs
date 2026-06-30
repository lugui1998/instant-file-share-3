using System.Net;

namespace InstantFileShare.Infrastructure;

public sealed class ExternalAddressResolver
{
    private static readonly TimeSpan DefaultPublicIpTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _publicIpTimeout;

    public ExternalAddressResolver(HttpClient httpClient)
        : this(httpClient, DefaultPublicIpTimeout)
    {
    }

    internal ExternalAddressResolver(HttpClient httpClient, TimeSpan publicIpTimeout)
    {
        _httpClient = httpClient;
        _publicIpTimeout = publicIpTimeout;
    }

    public async Task<string?> TryGetPublicIpAsync(CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_publicIpTimeout);

        try
        {
            var ip = await _httpClient.GetStringAsync("https://api.ipify.org", timeoutCancellation.Token);
            return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> TryGetLocalIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var address = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(entry => entry.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(entry));
            return Task.FromResult(address?.ToString());
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }
}
