namespace InstantFileShare.Agent;

public sealed class MeteredReadStream(Stream inner, long? bytesPerSecondLimit) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += bytesRead;
        await ApplyLimitAsync(bytesRead, cancellationToken);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        BytesRead += bytesRead;
        await ApplyLimitAsync(bytesRead, cancellationToken);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task ApplyLimitAsync(int bytesRead, CancellationToken cancellationToken)
    {
        if (bytesPerSecondLimit is null || bytesPerSecondLimit <= 0 || bytesRead == 0)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(bytesRead / (double)bytesPerSecondLimit.Value);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }
}
