using InstantFileShare.Core;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent.Tests;

public sealed class RuntimeEventStreamTests
{
    [Fact]
    public async Task ListenAsync_BroadcastsEventsToAllSubscribers()
    {
        var stream = new ChannelRuntimeEventStream();
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var firstListener = stream.ListenAsync(firstCancellation.Token).GetAsyncEnumerator(firstCancellation.Token);
        await using var secondListener = stream.ListenAsync(secondCancellation.Token).GetAsyncEnumerator(secondCancellation.Token);
        var firstMoveNextTask = firstListener.MoveNextAsync().AsTask();
        var secondMoveNextTask = secondListener.MoveNextAsync().AsTask();

        var runtimeEvent = new RuntimeEvent(RuntimeEventType.ShareCreated, DateTimeOffset.UtcNow, new { Id = "share-1" });
        await stream.PublishAsync(runtimeEvent, CancellationToken.None);

        Assert.True(await firstMoveNextTask);
        Assert.True(await secondMoveNextTask);
        Assert.Equal(runtimeEvent, firstListener.Current);
        Assert.Equal(runtimeEvent, secondListener.Current);
    }

    [Fact]
    public async Task ListenAsync_RemovesClosedSubscriberWithoutAffectingOtherListeners()
    {
        var stream = new ChannelRuntimeEventStream();
        using var activeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var disposedListener = stream.ListenAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var activeListener = stream.ListenAsync(activeCancellation.Token).GetAsyncEnumerator(activeCancellation.Token);

        await disposedListener.DisposeAsync();
        var activeMoveNextTask = activeListener.MoveNextAsync().AsTask();

        var runtimeEvent = new RuntimeEvent(RuntimeEventType.TransferCompleted, DateTimeOffset.UtcNow, new { Id = "transfer-1" });
        await stream.PublishAsync(runtimeEvent, CancellationToken.None);

        Assert.True(await activeMoveNextTask);
        Assert.Equal(runtimeEvent, activeListener.Current);
    }

    [Fact]
    public async Task PublishAsync_DoesNotReplayBufferedEventsToNewSubscribers()
    {
        var stream = new ChannelRuntimeEventStream();
        await stream.PublishAsync(new RuntimeEvent(RuntimeEventType.SettingsUpdated, DateTimeOffset.UtcNow, new { LocalApiPort = 46431 }), CancellationToken.None);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await using var listener = stream.ListenAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.MoveNextAsync().AsTask());
    }
}
