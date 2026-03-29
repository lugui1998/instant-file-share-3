namespace InstantFileShare.Agent;

public sealed class MeteredWriteStream(Stream inner, long? bytesPerSecondLimit) : Stream
{
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        BytesWritten += count;
        ApplyLimit(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        BytesWritten += buffer.Length;
        ApplyLimit(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        BytesWritten += 1;
        ApplyLimit(1);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        BytesWritten += buffer.Length;
        await ApplyLimitAsync(buffer.Length, cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        BytesWritten += count;
        await ApplyLimitAsync(count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private async Task ApplyLimitAsync(int bytesWritten, CancellationToken cancellationToken)
    {
        if (bytesPerSecondLimit is null || bytesPerSecondLimit <= 0 || bytesWritten == 0)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(bytesWritten / (double)bytesPerSecondLimit.Value);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private void ApplyLimit(int bytesWritten)
    {
        if (bytesPerSecondLimit is null || bytesPerSecondLimit <= 0 || bytesWritten == 0)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(bytesWritten / (double)bytesPerSecondLimit.Value);
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }
}
