using System.Threading.Channels;
using InstantFileShare.Core;

namespace InstantFileShare.Infrastructure;

public sealed class ChannelRuntimeEventStream : IRuntimeEventStream
{
    private const int SubscriberCapacity = 128;
    private readonly object _subscriberLock = new();
    private readonly Dictionary<Guid, Channel<RuntimeEvent>> _subscribers = [];

    public Task PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        KeyValuePair<Guid, Channel<RuntimeEvent>>[] subscribers;
        lock (_subscriberLock)
        {
            subscribers = _subscribers.ToArray();
        }

        if (subscribers.Length == 0)
        {
            return Task.CompletedTask;
        }

        List<Guid>? completedSubscribers = null;
        foreach (var (subscriberId, subscriberChannel) in subscribers)
        {
            if (!subscriberChannel.Writer.TryWrite(runtimeEvent))
            {
                completedSubscribers ??= [];
                completedSubscribers.Add(subscriberId);
            }
        }

        if (completedSubscribers is not null)
        {
            lock (_subscriberLock)
            {
                foreach (var subscriberId in completedSubscribers)
                {
                    _subscribers.Remove(subscriberId);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RuntimeEvent> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscriberId = Guid.NewGuid();
        var subscriberChannel = Channel.CreateBounded<RuntimeEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_subscriberLock)
        {
            _subscribers[subscriberId] = subscriberChannel;
        }

        try
        {
            await foreach (var runtimeEvent in subscriberChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return runtimeEvent;
            }
        }
        finally
        {
            lock (_subscriberLock)
            {
                _subscribers.Remove(subscriberId);
            }

            subscriberChannel.Writer.TryComplete();
        }
    }
}
