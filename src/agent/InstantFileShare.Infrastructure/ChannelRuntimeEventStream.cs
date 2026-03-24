using System.Threading.Channels;
using InstantFileShare.Core;

namespace InstantFileShare.Infrastructure;

public sealed class ChannelRuntimeEventStream : IRuntimeEventStream
{
    private readonly Channel<RuntimeEvent> _channel = Channel.CreateUnbounded<RuntimeEvent>();

    public Task PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(runtimeEvent, cancellationToken).AsTask();

    public IAsyncEnumerable<RuntimeEvent> ListenAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
