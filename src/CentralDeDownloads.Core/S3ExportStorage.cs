using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace CentralDeDownloads.Core;

public readonly record struct GeneratedUpload(long Bytes, int Parts, long S3UploadMilliseconds);

public sealed class S3ExportStorage : IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly bool _useHttpUrl;

    public static string ObjectKey(string jobId) => $"arquivos/{jobId}/arquivo";

    public S3ExportStorage(string region, string bucket, string? serviceUrl = null)
    {
        var endpoint = RegionEndpoint.GetBySystemName(region);
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            _s3 = new AmazonS3Client(endpoint);
        }
        else
        {
            _s3 = new AmazonS3Client(new AmazonS3Config
            { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = region });
        }
        _bucket = bucket;
        _useHttpUrl = serviceUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<GeneratedUpload> UploadGeneratedAsync(ExportJob job, IAsyncEnumerable<ExportBatch> batches,
        string key, string contentType, CancellationToken ct, int partSize = MultipartUploadStream.DefaultPartSize)
    {
        var started = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentType = contentType
        }, ct);
        try
        {
            using var output = new MultipartUploadStream(_s3, _bucket, key, started.UploadId, ct, partSize);
            await ExportFileWriter.WriteAsync(job, batches, output, ct);
            await output.CompleteAsync(ct);
            return new GeneratedUpload(output.BytesWritten, output.PartsUploaded,
                output.S3UploadMilliseconds);
        }
        catch
        {
            try
            {
                await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                { BucketName = _bucket, Key = key, UploadId = started.UploadId }, CancellationToken.None);
            }
            catch { /* O worker registra a falha original; o lifecycle remove partes órfãs. */ }
            throw;
        }
    }

    public Task DeleteObjectAsync(string key, CancellationToken ct) =>
        _s3.DeleteObjectAsync(_bucket, key, ct);

    public string GetShortDownloadUrl(string key, string fileName)
    {
        var safeName = new string(Path.GetFileName(fileName)
            .Select(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '.' or '-' or '_' or ' ' ? ch : '_').ToArray());
        return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET,
            Protocol = _useHttpUrl ? Protocol.HTTP : Protocol.HTTPS,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}"
            }
        });
    }

    public void Dispose()
    {
        _s3.Dispose();
    }
}
