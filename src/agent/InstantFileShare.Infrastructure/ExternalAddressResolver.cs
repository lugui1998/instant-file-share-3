using System.Net;

namespace InstantFileShare.Infrastructure;

public sealed class ExternalAddressResolver(HttpClient httpClient)
{
    public async Task<string?> TryGetPublicIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ip = await httpClient.GetStringAsync("https://api.ipify.org", cancellationToken);
            return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
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
