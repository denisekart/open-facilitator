namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Optional store capability: mint S3-standard presigned URLs so upload and
/// download byte flows bypass the kernel (spec §15.4 portable retrieval). An
/// adapter that cannot hand out URLs (filesystem) simply does not implement
/// this; callers probe with <c>is IStoreSignedUrlSource</c> and fall back to
/// the kernel-proxied surface. A URL is only ever minted for a key the
/// backend's own key law fixes ahead of time (the digest-derived path) —
/// never a caller-supplied key, and never for a bucket or writable prefix.
/// </summary>
public interface IStoreSignedUrlSource
{
    /// <summary>Presigned PUT URL for a not-yet-landed key; null when unavailable.</summary>
    Task<Uri?> PresignUploadAsync(string relativePath, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Presigned GET URL for a stored key; null when unavailable. When
    /// <paramref name="downloadContentType"/> rides, it overrides the stored
    /// object's content type on the response (S3-standard signed override), so
    /// a blob written without a media type still serves the ledger row's.
    /// </summary>
    Task<Uri?> PresignDownloadAsync(string relativePath, string? downloadContentType, TimeSpan ttl, CancellationToken ct);
}
