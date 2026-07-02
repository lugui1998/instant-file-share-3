using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent.Tests;

public sealed class ExternalAddressResolverTests
{
    [Fact]
    public async Task TryGetPublicIpAsync_ReturnsNullWhenLookupTimesOut()
    {
        var handler = new HangingHttpMessageHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var resolver = new ExternalAddressResolver(httpClient, TimeSpan.FromMilliseconds(50));

        var publicIp = await resolver.TryGetPublicIpAsync(CancellationToken.None);

        Assert.Null(publicIp);
        Assert.True(handler.SawCancellation);
    }

    private sealed class HangingHttpMessageHandler : HttpMessageHandler
    {
        public bool SawCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The hanging test handler should only complete through cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SawCancellation = true;
                throw;
            }
        }
    }
}
