using System.Buffers;
using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;

namespace CentralDeDownloads.Core;

// O escritor dos formatos recebe um Stream sem seek. Cada parte completa é
// enviada ao S3 antes que o buffer seja reutilizado; o arquivo não usa disco.
internal sealed class MultipartUploadStream : Stream
{
    public const int DefaultPartSize = 64 * 1024 * 1024;
    private const int MaximumParts = 10_000;

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _key;
    private readonly string _uploadId;
    private readonly byte[] _buffer;
    private readonly List<PartETag> _parts = [];
    private readonly CancellationToken _operationToken;
    private int _buffered;
    private bool _completed;
    private bool _disposed;

    public long BytesWritten { get; private set; }
    public int PartsUploaded => _parts.Count;
    public long S3UploadMilliseconds { get; private set; }

    public MultipartUploadStream(IAmazonS3 s3, string bucket, string key, string uploadId,
        CancellationToken operationToken, int partSize = DefaultPartSize)
    {
        if (partSize < 5 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(partSize));
        _s3 = s3;
        _bucket = bucket;
        _key = key;
        _uploadId = uploadId;
        _operationToken = operationToken;
        _buffer = ArrayPool<byte>.Shared.Rent(partSize);
        PartSize = partSize;
    }

    private int PartSize { get; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> source)
    {
        EnsureWritable();
        while (!source.IsEmpty)
        {
            _operationToken.ThrowIfCancellationRequested();
            var count = Math.Min(source.Length, PartSize - _buffered);
            source[..count].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += count;
            BytesWritten += count;
            source = source[count..];
            if (_buffered == PartSize)
                UploadBufferedPartAsync(_operationToken).GetAwaiter().GetResult();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        EnsureWritable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _operationToken);
        while (!source.IsEmpty)
        {
            linked.Token.ThrowIfCancellationRequested();
            var count = Math.Min(source.Length, PartSize - _buffered);
            source[..count].CopyTo(_buffer.AsMemory(_buffered));
            _buffered += count;
            BytesWritten += count;
            source = source[count..];
            if (_buffered == PartSize) await UploadBufferedPartAsync(linked.Token);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public async Task CompleteAsync(CancellationToken ct)
    {
        EnsureWritable();
        if (_buffered > 0) await UploadBufferedPartAsync(ct);
        if (_parts.Count == 0) throw new InvalidOperationException("Arquivo vazio.");
        var started = Stopwatch.GetTimestamp();
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = _key,
            UploadId = _uploadId,
            PartETags = _parts
        }, ct);
        S3UploadMilliseconds += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _completed = true;
    }

    private async Task UploadBufferedPartAsync(CancellationToken ct)
    {
        if (_parts.Count >= MaximumParts)
            throw new InvalidOperationException("Arquivo excedeu 10.000 partes S3; aumente o tamanho da parte.");
        using var input = new MemoryStream(_buffer, 0, _buffered, writable: false);
        var number = _parts.Count + 1;
        var started = Stopwatch.GetTimestamp();
        var response = await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket,
            Key = _key,
            UploadId = _uploadId,
            PartNumber = number,
            PartSize = _buffered,
            InputStream = input
        }, ct);
        S3UploadMilliseconds += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _parts.Add(new PartETag(number, response.ETag));
        _buffered = 0;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Upload já concluído.");
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
        }
        base.Dispose(disposing);
    }
}
