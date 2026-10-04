using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace CentralDeDownloads.Core;

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

    public async Task UploadAsync(string path, string key, string contentType, CancellationToken ct)
    {
        using var transfer = new TransferUtility(_s3);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = path,
            ContentType = contentType
        }, ct);
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
