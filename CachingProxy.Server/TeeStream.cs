namespace CachingProxy.Server;

public class TeeStream(Stream primaryStream, Stream secondaryStream) : Stream
{
    private readonly Stream _primaryStream = primaryStream ?? throw new ArgumentNullException(nameof(primaryStream));

    private readonly Stream _secondaryStream =
        secondaryStream ?? throw new ArgumentNullException(nameof(secondaryStream));

    private bool _disposed;

    public override bool CanRead => _primaryStream.CanRead;
    public override bool CanSeek => _primaryStream.CanSeek && _secondaryStream.CanSeek;
    public override bool CanWrite => _primaryStream.CanWrite && _secondaryStream.CanWrite;
    public override long Length => _primaryStream.Length;

    public override long Position
    {
        get => _primaryStream.Position;
        set
        {
            _primaryStream.Position = value;
            if (_secondaryStream.CanSeek)
                _secondaryStream.Position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _primaryStream.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await _primaryStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _primaryStream.Write(buffer, offset, count);
        _secondaryStream.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var task1 = _primaryStream.WriteAsync(buffer, offset, count, cancellationToken);
        var task2 = _secondaryStream.WriteAsync(buffer, offset, count, cancellationToken);
        await Task.WhenAll(task1, task2);
    }

    public override void Flush()
    {
        _primaryStream.Flush();
        _secondaryStream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        var task1 = _primaryStream.FlushAsync(cancellationToken);
        var task2 = _secondaryStream.FlushAsync(cancellationToken);
        await Task.WhenAll(task1, task2);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var result = _primaryStream.Seek(offset, origin);
        if (_secondaryStream.CanSeek)
            _secondaryStream.Seek(offset, origin);
        return result;
    }

    public override void SetLength(long value)
    {
        _primaryStream.SetLength(value);
        _secondaryStream.SetLength(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _primaryStream.Dispose();
            _secondaryStream.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}