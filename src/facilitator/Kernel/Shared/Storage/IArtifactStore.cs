namespace Facilitator.Kernel.Shared.Storage;

/// <summary>Outcome of a content-addressed put: where the blob landed, the digest
/// the bytes ACTUALLY hash to, and the byte count actually written.</summary>
public sealed record ArtifactPut(string RelativePath, string DigestHex, long Bytes);

/// <summary>
/// The artifact byte-store port (spec §15.4): content-addressed blobs keyed by
/// a hex SHA-256 digest, addressed through opaque relative paths. A put hashes
/// the received stream itself and lands the blob at the COMPUTED digest's path
/// — the port never trusts a caller-declared address, so a mismatched upload
/// can never squat the declared digest's location. Listing is deliberately
/// absent — the metadata ledger table answers listing queries, a filesystem
/// walk would add a second source of truth. The kernel never names a vendor
/// through this port.
/// </summary>
public interface IArtifactStore
{
    /// <summary>StorageUri scheme this adapter writes (<c>kernel-fs</c>, <c>s3</c>); rows decide every later action by their scheme, never by re-reading config.</summary>
    string StorageUriScheme { get; }

    /// <summary>True when a StorageUri was written by THIS adapter (scheme prefix match).</summary>
    bool Owns(string storageUri);

    /// <summary>Content length of a stored blob; null when absent (the confirm path needs size without a full GET).</summary>
    Task<long?> StatAsync(string relativePath, CancellationToken ct);

    /// <summary>Persist the stream at its content address; idempotent (same bytes ⇒ same blob). Returns path + computed digest + byte count.</summary>
    Task<ArtifactPut> PutAsync(Stream content, CancellationToken ct);

    /// <summary>Open a stored blob; null when the path was never stored.</summary>
    Task<Stream?> GetAsync(string relativePath, CancellationToken ct);

    /// <summary>Whether a blob exists at the relative path.</summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct);

    /// <summary>Remove a blob; idempotent. Refcount policy is the caller's (GC law: nothing deletes until retention lands).</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>Re-hash a stored blob and compare against the digest it was stored under.</summary>
    Task<bool> VerifyDigestAsync(string relativePath, string digestHex, CancellationToken ct);
}
