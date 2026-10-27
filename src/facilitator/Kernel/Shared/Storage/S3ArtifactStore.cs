using System.Security.Cryptography;

using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// S3-wire <see cref="IArtifactStore"/> (vendor-neutral: the kernel speaks
/// only the S3 standard via SigV4): blobs land at
/// <c>s3://{bucket}/{digest[0..2]}/{digest}</c> — the same content-addressed
/// key law as every other adapter (<see cref="ArtifactKeys"/>). A put
/// streams to a temp key while hashing, then server-side copies to the
/// COMPUTED digest key (S3 has no atomic rename; same-bucket CopyObject is
/// atomic, and a re-put of the same digest converges like the FS store's
/// rename-if-absent). Also implements <see cref="IStoreSignedUrlSource"/>:
/// presigned PUT/GET URLs keep large byte flows off the kernel; the kernel
/// NEVER trusts an ETag — the digest is always the kernel's own SHA-256.
/// </summary>
public sealed class S3ArtifactStore : IArtifactStore, IStoreSignedUrlSource, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly bool _presignHttps;

    /// <summary>Bind to <see cref="ArtifactOptions.S3"/>; fails closed naming every missing key.</summary>
    public S3ArtifactStore(ArtifactOptions options)
    {
        var s3 = options.S3;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(s3.Endpoint))
        {
            missing.Add("Storage:S3:Endpoint");
        }

        if (string.IsNullOrWhiteSpace(s3.Bucket))
        {
            missing.Add("Storage:S3:Bucket");
        }

        if (string.IsNullOrWhiteSpace(s3.Region))
        {
            missing.Add("Storage:S3:Region");
        }

        if (string.IsNullOrWhiteSpace(s3.AccessKey))
        {
            missing.Add("Storage:S3:AccessKey");
        }

        if (string.IsNullOrWhiteSpace(s3.SecretKey))
        {
            missing.Add("Storage:S3:SecretKey");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Storage:Provider '{options.Provider}' requires {string.Join(", ", missing)} (fail-closed).");
        }

        _bucket = s3.Bucket!;
        _presignHttps = !Uri.TryCreate(s3.Endpoint, UriKind.Absolute, out var ep)
            || ep.Scheme == Uri.UriSchemeHttps;
        _client = new AmazonS3Client(
            new BasicAWSCredentials(s3.AccessKey, s3.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = s3.Endpoint,
                ForcePathStyle = s3.ForcePathStyle,
                AuthenticationRegion = s3.Region,
            });
    }

    /// <summary>Test seam: run against an injected client (same key law).</summary>
    internal S3ArtifactStore(IAmazonS3 client, string bucket)
    {
        _client = client;
        _bucket = bucket;
    }

    /// <inheritdoc />
    public string StorageUriScheme => "s3";

    /// <inheritdoc />
    public bool Owns(string storageUri) => storageUri.StartsWith("s3:", StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<ArtifactPut> PutAsync(Stream content, CancellationToken ct)
    {
        // Two-step land: hash while streaming to a temp key, then server-side
        // copy to the COMPUTED digest key. A re-put of the same digest
        // converges (skip copy when the target exists) — dedupe, not error.
        var tempKey = $".pending-{Guid.NewGuid():N}";
        var tempFile = Path.GetTempFileName();
        string digestHex;
        long bytes;
        try
        {
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var spool = new FileStream(tempFile, FileMode.Truncate, FileAccess.Write, FileShare.None))
                {
                    bytes = await CopyCountedAsync(content, spool, hasher, ct);
                }

                digestHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            await using (var spool = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await _client.PutObjectAsync(
                    new PutObjectRequest { BucketName = _bucket, Key = tempKey, InputStream = spool },
                    ct);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }

        var relative = ArtifactKeys.RelativePathOf(digestHex);
        if (!await ExistsAsync(relative, ct))
        {
            try
            {
                await _client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = tempKey,
                    DestinationBucket = _bucket,
                    DestinationKey = relative,
                }, ct);
            }
            catch (AmazonS3Exception)
            {
                // A concurrent put of the same digest may have won the copy —
                // re-check, and treat a now-present target as idempotent success.
                if (!await ExistsAsync(relative, ct))
                {
                    throw;
                }
            }
        }

        await DeleteQuietAsync(tempKey, ct);
        return new ArtifactPut(relative, digestHex, bytes);
    }

    /// <inheritdoc />
    public async Task<Stream?> GetAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, relativePath, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct) =>
        await StatAsync(relativePath, ct) is not null;

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct) => DeleteQuietAsync(relativePath, ct);

    /// <inheritdoc />
    public async Task<bool> VerifyDigestAsync(string relativePath, string digestHex, CancellationToken ct)
    {
        var stream = await GetAsync(relativePath, ct);
        if (stream is null)
        {
            return false;
        }

        await using (stream)
        {
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant() == digestHex.ToLowerInvariant();
        }
    }

    /// <inheritdoc />
    public async Task<long?> StatAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, relativePath, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<Uri?> PresignUploadAsync(string relativePath, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult<Uri?>(Presign(HttpVerb.PUT, relativePath, ttl));

    /// <inheritdoc />
    public Task<Uri?> PresignDownloadAsync(string relativePath, string? downloadContentType, TimeSpan ttl, CancellationToken ct) =>
        Task.FromResult<Uri?>(Presign(HttpVerb.GET, relativePath, ttl, downloadContentType));

    private Uri? Presign(HttpVerb verb, string relativePath, TimeSpan ttl, string? downloadContentType = null)
    {
        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = relativePath,
                // The SDK defaults the verb to GET — a PUT ticket signed as
                // GET dies with SignatureDoesNotMatch on every compatible
                // endpoint (SeaweedFS recomputes over the actual method).
                Verb = verb,
                // The SDK defaults presigned URLs to https; follow the
                // configured endpoint's scheme so plaintext deployments
                // (tests, TLS-offloaded ingress) stay speakable.
                Protocol = _presignHttps ? Protocol.HTTPS : Protocol.HTTP,
                Expires = DateTime.UtcNow.Add(ttl),
            };
            if (downloadContentType is not null)
            {
                // S3-standard signed override: the response carries the row's
                // media type even though the blob landed without one (the
                // direct-upload client decides the PUT's headers, not us).
                request.ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentType = downloadContentType,
                };
            }

            return new Uri(_client.GetPreSignedURL(request));
        }
        catch (AmazonS3Exception)
        {
            return null;
        }
    }

    private async Task DeleteQuietAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent: absent is deleted.
        }
    }

    private static async Task<long> CopyCountedAsync(Stream source, Stream target, IncrementalHash hasher, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }

        return total;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
