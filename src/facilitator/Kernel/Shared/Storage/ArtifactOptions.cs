namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Artifact storage configuration (<c>Storage</c> section). Only the
/// filesystem provider ships; the port keeps a later object-store adapter a
/// drop-in. An unknown provider value fails startup — never a silent fallback.
/// </summary>
public sealed class ArtifactOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    /// <summary>Active provider; <c>filesystem</c> is the only shipped value.</summary>
    public string Provider { get; set; } = "filesystem";

    /// <summary>Filesystem-backend settings.</summary>
    public FileSystemOptions FileSystem { get; set; } = new();

    /// <summary>Filesystem adapter options.</summary>
    public sealed class FileSystemOptions
    {
        /// <summary>Blob root; created at startup when missing. Required when provider=filesystem.</summary>
        public string? RootPath { get; set; }
    }
}
