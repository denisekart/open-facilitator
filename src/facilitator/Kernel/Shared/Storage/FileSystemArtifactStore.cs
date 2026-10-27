using System.Security.Cryptography;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Filesystem <see cref="IArtifactStore"/>: blobs live at
/// <c>{root}/{digest[0..2]}/{digest}</c> — paths derive ONLY from hex digests,
/// never client text. A put hashes while streaming to a temp file, then lands
/// at the COMPUTED digest's path (rename-if-absent), so an address never lies
/// and dedupe across works is free. Idempotent re-put of identical bytes
/// succeeds without rewriting.
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _root;

    /// <summary>Bind to <see cref="ArtifactOptions"/>; fails closed when the root path is missing.</summary>
    public FileSystemArtifactStore(ArtifactOptions options)
    {
        var root = options.FileSystem.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"Storage:Provider '{options.Provider}' requires Storage:FileSystem:RootPath (fail-closed).");
        }

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public string StorageUriScheme => "kernel-fs";

    /// <inheritdoc />
    public bool Owns(string storageUri) => storageUri.StartsWith("kernel-fs:", StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<ArtifactPut> PutAsync(Stream content, CancellationToken ct)
    {
        var temp = Path.Combine(_root, $".part-{Guid.NewGuid():N}");
        long bytes;
        string digestHex;
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                bytes = await CopyCountedAsync(content, file, hasher, ct);
            }

            digestHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        var relative = ArtifactKeys.RelativePathOf(digestHex);
        var target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            // Rename-if-absent: concurrent/second put of the same bytes is success, not an error.
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException)
        {
            // Already content-addressed at this path — drop the duplicate temp.
            File.Delete(temp);
        }

        return new ArtifactPut(relative, digestHex, bytes);
    }

    /// <inheritdoc />
    public Task<Stream?> GetAsync(string relativePath, CancellationToken ct)
    {
        var full = FullPath(relativePath);
        if (full is null || !File.Exists(full))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        var full = FullPath(relativePath);
        return Task.FromResult(full is not null && File.Exists(full));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var full = FullPath(relativePath);
        if (full is not null && File.Exists(full))
        {
            File.Delete(full);
        }

        return Task.CompletedTask;
    }

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
    public Task<long?> StatAsync(string relativePath, CancellationToken ct)
    {
        var full = FullPath(relativePath);
        return Task.FromResult<long?>(full is not null && File.Exists(full) ? new FileInfo(full).Length : null);
    }

    // Defense in depth: relative paths come from the ledger (stored under the
    // digest at put time); a non-hex or escaping path never leaves the root.
    private string? FullPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IsHex(parts[0]) || !IsHex(parts[1]) || parts[0].Length != 2)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, parts[0], parts[1]));
        return candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? candidate : null;
    }

    private static bool IsHex(string value) =>
        value.Length > 0 && value.All(Uri.IsHexDigit);

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
}
