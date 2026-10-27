namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// The canonical blob key law, shared by every adapter, the ledger, and the
/// endpoints: an object lives at <c>{sha256-hex[0..2]}/{sha256-hex}</c> on
/// EVERY backend, so content stays addressable independent of provider and a
/// row can migrate providers by re-pointing only its StorageUri scheme.
/// </summary>
public static class ArtifactKeys
{
    /// <summary>Canonical relative path for a hex digest ({hex[0..2]}/{hex}).</summary>
    public static string RelativePathOf(string digestHex) => $"{digestHex[..2]}/{digestHex}";

    /// <summary>The relative store key a kernel-hosted StorageUri points at (everything after the scheme). Only meaningful once a store <see cref="IArtifactStore.Owns"/> the URI.</summary>
    public static string StoreKeyOf(string storageUri)
    {
        var colon = storageUri.IndexOf(':');
        return colon < 0 ? storageUri : storageUri[(colon + 1)..];
    }
}
