using System.Diagnostics;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent.Tests;

public sealed class ExternalAddressResolverTests
{
    [Fact]
    public async Task TryGetPublicIpAsync_ReturnsNullWhenLookupTimesOut()
    {
        using var httpClient = new HttpClient(new HangingHttpMessageHandler());
        var resolver = new ExternalAddressResolver(httpClient, TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        var publicIp = await resolver.TryGetPublicIpAsync(CancellationToken.None);

        Assert.Null(publicIp);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class HangingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
