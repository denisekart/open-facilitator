using System.Security.Cryptography;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The kernel's session-JWT signing key, kept in the shared
/// <c>admin_config.link-session-key</c> row: any replica verifies a session
/// JWT from that row with no external CA and no per-request DB read (the
/// material caches in memory per replica). Minted on first demand under an
/// insert-if-absent write — two replicas minting concurrently both produce a
/// key, exactly one row wins, and the loser re-reads.
/// </summary>
/// <param name="Kid">Key id (header <c>kid</c>), so a future rotation can
/// coexist with tokens still in flight.</param>
/// <param name="PrivateKeySeed">32-byte Ed25519 seed (base64url, no padding).</param>
/// <param name="PublicKey">Raw public key (base64url, no padding).</param>
public sealed record LinkSessionKey(string Kid, string PrivateKeySeed, string PublicKey);

/// <summary>
/// The kernel's session-key surface: <see cref="CurrentAsync"/> hands out the
/// signing material (minting it once), <see cref="PublicKeysAsync"/> hands out
/// what a verifier needs. Payloads are <c>{"kid","privateKey","publicKey","createdAt"}</c>.
/// </summary>
public sealed class LinkSessionKeys(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider)
{
    /// <summary>The admin_config resource name (registered, so PATCH validates it).</summary>
    public const string Resource = "link-session-key";

    private readonly SemaphoreSlim gate = new(1, 1);
    private LinkSessionKey? cached;

    /// <summary>The kernel's signing key, minted into <c>admin_config</c> on first demand.</summary>
    public async Task<LinkSessionKey> CurrentAsync(CancellationToken ct)
    {
        var current = cached;
        if (current is not null)
        {
            return current;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null)
            {
                return cached;
            }

            var existing = await ReadAsync(ct);
            if (existing is not null)
            {
                return cached = existing;
            }

            var minted = Mint();
            await using (var db = await contextFactory.CreateDbContextAsync(ct))
            {
                var row = new AdminConfigEntity
                {
                    Resource = Resource,
                    Payload = Serialize(minted),
                    RowVersion = 1,
                    Author = "kernel:link-session-key",
                    Reason = "minted on first session issuance",
                    UpdatedAt = timeProvider.GetUtcNow().UtcDateTime,
                };
                db.AdminConfig.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // Insert-if-absent: another replica minted first — theirs is the truth.
                    var raced = await ReadAsync(ct) ?? throw new InvalidOperationException(
                        "link-session-key row vanished mid-mint");
                    return cached = raced;
                }
            }

            return cached = minted;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The current public key + kid (verifier side; no private material read).</summary>
    public async Task<(string Kid, string PublicKey)?> PublicKeyAsync(CancellationToken ct)
    {
        var key = await CurrentAsync(ct);
        return (key.Kid, key.PublicKey);
    }

    /// <summary>Forget the cached material (tests, rotation).</summary>
    public void Invalidate() => cached = null;

    private async Task<LinkSessionKey?> ReadAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == Resource)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is null)
        {
            return null;
        }

        try
        {
            var doc = JsonDocument.Parse(payload).RootElement;
            var kid = doc.GetProperty("kid").GetString();
            var privateKey = doc.GetProperty("privateKey").GetString();
            var publicKey = doc.GetProperty("publicKey").GetString();
            if (kid is null || privateKey is null || publicKey is null
                || !LinkKeys.TryDecodeBase64Url(privateKey, out var seed) || seed.Length != 32
                || !LinkKeys.TryParsePublicKey(publicKey, out _))
            {
                return null;
            }

            return new LinkSessionKey(kid, privateKey, publicKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinkSessionKey Mint()
    {
        Span<byte> seed = stackalloc byte[32];
        RandomNumberGenerator.Fill(seed);
        var seedBytes = seed.ToArray();
        var publicKey = LinkKeys.PublicKeyFromSeed(seedBytes);
        return new LinkSessionKey(
            "ksk-" + LinkKeys.Base64Url(seedBytes.AsSpan(0, 8)),
            LinkKeys.Base64Url(seedBytes),
            LinkKeys.Base64Url(publicKey));
    }

    private static string Serialize(LinkSessionKey key) =>
        JsonSerializer.Serialize(new
        {
            kid = key.Kid,
            privateKey = key.PrivateKeySeed,
            publicKey = key.PublicKey,
            createdAt = DateTime.UtcNow.ToString("O"),
        });
}
