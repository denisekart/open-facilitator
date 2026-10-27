namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Artifact storage configuration (<c>Storage</c> section). Providers:
/// <c>filesystem</c> (zero-dependency default) and <c>s3</c> (any
/// S3-wire-compatible endpoint; the kernel speaks only the S3 standard).
/// An unknown provider value fails startup — never a silent fallback, and an
/// s3 provider with an incomplete <c>S3</c> block fails the startup check.
/// </summary>
public sealed class ArtifactOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    /// <summary>Active provider; <c>filesystem</c> or <c>s3</c>. Read once at composition.</summary>
    public string Provider { get; set; } = "filesystem";

    /// <summary>Filesystem-backend settings.</summary>
    public FileSystemOptions FileSystem { get; set; } = new();

    /// <summary>Object-store backend settings (required when provider=s3).</summary>
    public S3Options S3 { get; set; } = new();

    /// <summary>
    /// Operator bindings that make an external artifact URL a trust claim
    /// (spec §15.4): a declaration whose <c>storageUrl</c> prefix-matches a
    /// binding waives the SSRF address screen (scheme/userinfo laws always
    /// apply); a clean screen stores the row <c>verified</c>. Empty (default)
    /// means every external ref stays <c>unverified</c>.
    /// </summary>
    public List<ExternalBinding> ExternalBindings { get; set; } = [];

    /// <summary>Retention-GC scheduling.</summary>
    public GcOptions Gc { get; set; } = new();

    /// <summary>Filesystem adapter options.</summary>
    public sealed class FileSystemOptions
    {
        /// <summary>Blob root; created at startup when missing. Required when provider=filesystem.</summary>
        public string? RootPath { get; set; }
    }

    /// <summary>S3-adapter options: any S3-wire endpoint (path-style by default).</summary>
    public sealed class S3Options
    {
        /// <summary>Service endpoint URL, e.g. <c>http://127.0.0.1:8333</c>. Required.</summary>
        public string? Endpoint { get; set; }

        /// <summary>Bucket the store owns. Required.</summary>
        public string? Bucket { get; set; }

        /// <summary>SigV4 signing region (path-style endpoints still demand one). Required.</summary>
        public string? Region { get; set; }

        /// <summary>Static access key. Required.</summary>
        public string? AccessKey { get; set; }

        /// <summary>Static secret key. Required.</summary>
        public string? SecretKey { get; set; }

        /// <summary>Address buckets as <c>{endpoint}/{bucket}/{key}</c> (what S3-compatible endpoints speak).</summary>
        public bool ForcePathStyle { get; set; } = true;
    }

    /// <summary>Retention-GC options (<c>Storage:Gc</c>).</summary>
    public sealed class GcOptions
    {
        /// <summary>Seconds between background passes.</summary>
        public int IntervalSeconds { get; set; } = 60;
    }
}

/// <summary>One operator external-ref binding (config <c>Storage:ExternalBindings</c>).</summary>
public sealed class ExternalBinding
{
    /// <summary>Binding id recorded in the audit detail on a screened pass.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Prefix a declaration's <c>storageUrl</c> must start with to bind.</summary>
    public string UrlPrefix { get; set; } = string.Empty;
}
