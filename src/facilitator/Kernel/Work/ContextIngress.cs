using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>Refused ⇒ a typed problem instead of an admission; the arm never
/// 500s on a context read.</summary>
public sealed record ContextArm(ProblemDetails? Problem, string Status, object? Data = null)
{
    public bool Refused => Problem is not null;
}

/// <summary>
/// The two context-plane arms of the link surface:
///
/// <list type="bullet">
///   <item><c>command.context.fetch.v1</c> — expand the refs
///   (<see cref="ContextFetchService"/>), answer the kernel-local items
///   INLINE on the admission data (the ack already carries handler data —
///   no new synchronous event), and route a
///   <c>command.context.fetch.v1</c> per unresolved <c>thread://</c> URI to
///   the SOURCE adapter's endpoint (deterministic
///   <c>context-fetch:&lt;fetchId&gt;</c> command id, ledger-idempotent like
///   <c>communication-reply:&lt;transitionEventId&gt;</c>). Budget law lives
///   in the service: ≤ 32 refs, ≤ 32 KB per item, truncation recorded, never
///   silent.</item>
///   <item><c>event.context.fetched.v1</c> — the adapter's asynchronous
///   answer: store the item set as a <c>context.history</c> artifact
///   (retention per policy, default 1d), record the event verbatim on the
///   audit leg (<c>context.fetched</c>, the builder's answer probe), resolve
///   the pending fetch by WAKING the parked routing intents (the dependency-
///   gate wake: mark the intent rows available).</item>
/// </list>
/// </summary>
public sealed class ContextIngress(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    ContextFetchService fetchService,
    ProviderRegistry providers,
    IArtifactStore store,
    AuditWriter audit,
    EnvelopeValidator validator,
    TimeProvider timeProvider)
{
    public const string FetchType = ContextFetchService.CommandType;
    public const string FetchedType = "org.facilitator.event.context.fetched.v1";

    /// <summary>Answered history is cheap to lose: one day by default.</summary>
    public const string HistoryRetention = "1d";

    /// <summary>Command arm: expand + answer inline + route the pending legs.</summary>
    public async Task<ContextArm> HandleFetchAsync(
        JsonElement root, LinkPrincipal principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var contextRefs = new List<string>();
        if (data.TryGetProperty("contextRefs", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } uri)
                {
                    contextRefs.Add(uri);
                }
            }
        }

        Guid? workId = Guid.TryParse(
            data.TryGetProperty("workId", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString()
                : null,
            out var parsed)
            ? parsed
            : null;
        var result = await fetchService.ExpandAsync(contextRefs, principal, workId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deadline = data.TryGetProperty("deadline", out var dl) && dl.ValueKind == JsonValueKind.String
            ? dl.GetString()!
            : now.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var targetProvider = data.TryGetProperty("targetId", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        // Route the thread:// legs to the source adapter's endpoint, one
        // command per URI (deterministic commandId ⇒ replay-safe). An
        // unknown provider answers an honest error item instead.
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            foreach (var pending in result.Pending)
            {
                var provider = providers.Find(pending.Provider);
                if (provider?.EndpointId is not { Length: > 0 } endpoint)
                {
                    continue;
                }

                var envelope = FetchCommand(
                    result.FetchId, pending.Uri, workId, pending.Provider, deadline, now);
                var json = envelope.ToJsonString();
                var verdict = validator.Validate(json);
                if (!verdict.Ok)
                {
                    continue;
                }

                db.Outbox.Add(new OutboxEntity
                {
                    OutboxId = Guid.NewGuid(),
                    RecipientEndpointId = $"link.{endpoint}",
                    AggregateType = "work",
                    AggregateId = workId,
                    EventType = FetchType,
                    CommandId = $"context-fetch:{result.FetchId}:{StableHash(pending.Uri)}",
                    Envelope = json,
                    WorkId = workId,
                    Status = "pending",
                    AvailableAt = now,
                    Attempts = 0,
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(ct);
            if (result.Pending.Count > 0)
            {
                await PostgresNotifications.NotifyOutboxAsync(db, (workId ?? Guid.Empty).ToString(), ct);
            }
        }

        _ = targetProvider;
        return new ContextArm(null, "accepted", new
        {
            fetchId = result.FetchId,
            items = result.Items.Select(item => new
            {
                uri = item.Uri,
                content = JsonNode.Parse(item.Content.ToJsonString()),
                truncated = item.Truncated,
            }),
            pending = result.Pending.Select(p => new { uri = p.Uri, fetchId = p.FetchId, provider = p.Provider }),
        });
    }

    /// <summary>Event arm: an adapter's asynchronous answer. The item set is
    /// stored as a <c>context.history</c> artifact (bytes stay in the store),
    /// the answer is audited verbatim, and parked intents wake. A malformed
    /// answer audits and accepts — the event plane never 500s.</summary>
    public async Task<ContextArm> HandleFetchedAsync(JsonElement root, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var responder = data.TryGetProperty("responder", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? "unknown"
            : "unknown";
        var workRaw = data.TryGetProperty("workId", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetString()
            : null;
        Guid? workId = Guid.TryParse(workRaw, out var parsed) ? parsed : null;

        string? teamSlug = null;
        if (workId is { } id)
        {
            await using var lookup = await contextFactory.CreateDbContextAsync(ct);
            teamSlug = await lookup.Works.AsNoTracking()
                .Where(wo => wo.WorkId == id).Select(wo => wo.Team!.Slug).FirstOrDefaultAsync(ct);
        }

        var bytes = Encoding.UTF8.GetBytes(data.GetRawText());
        var put = await store.PutAsync(new MemoryStream(bytes), ct);
        var digest = "sha256:" + put.DigestHex.ToLowerInvariant();
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            // Copy-on-forward provenance (router-team routing): when the
            // answered window came from a conversation whose binding names a
            // team — a thread the router flow signed over — the answered
            // bytes are a COPY and the ORIGINALS live on the router ledger.
            // Point at those rows so "where did this history come from" is a
            // query, not a guess.
            JsonNode? routerLedger = null;
            if (workId is { } ledgerWork)
            {
                var residence = await db.Works.AsNoTracking()
                    .Where(wo => wo.WorkId == ledgerWork && wo.ActiveWhere != null)
                    .Select(wo => wo.ActiveWhere).FirstOrDefaultAsync(ct);
                if (residence is { Length: > 0 })
                {
                    using var where = JsonDocument.Parse(residence);
                    var whereRoot = where.RootElement;
                    if (whereRoot.TryGetProperty("provider", out var p)
                        && whereRoot.TryGetProperty("channel", out var c)
                        && whereRoot.TryGetProperty("root", out var r0)
                        && p.ValueKind == JsonValueKind.String && c.ValueKind == JsonValueKind.String
                        && r0.ValueKind == JsonValueKind.String)
                    {
                        var provider = p.GetString()!;
                        var channel = c.GetString()!;
                        var rootMessage = r0.GetString()!;
                        var owned = await db.CommunicationBindings.AsNoTracking().AnyAsync(b =>
                            b.Provider == provider && b.Channel == channel
                            && b.RootMessageId == rootMessage && b.TeamSlug != null, ct);
                        if (owned)
                        {
                            // The ledger never moves; the window's originals
                            // are the provider rows whose reference names
                            // this exact conversation (bounded window: the
                            // newest rows for this provider, filtered in
                            // memory — provenance, not a hot path).
                            var originals = await db.ProviderEvents.AsNoTracking()
                                .Where(e => e.Provider == provider)
                                .OrderByDescending(e => e.ReceivedAt)
                                .Take(200)
                                .Select(e => new { e.ProviderEventId, e.BodySha256, e.Body })
                                .ToListAsync(ct);
                            var window = originals
                                .Where(e => CarriesReference(e.Body, channel, rootMessage))
                                .Reverse()
                                .ToList();
                            if (window.Count > 0)
                            {
                                routerLedger = new JsonObject
                                {
                                    ["provider"] = provider,
                                    ["eventIds"] = new JsonArray(window
                                        .Select(e => (JsonNode)JsonValue.Create(e.ProviderEventId.ToString())!).ToArray()),
                                    ["digests"] = new JsonArray(window
                                        .Select(e => (JsonNode)JsonValue.Create(e.BodySha256)!).ToArray()),
                                };
                            }
                        }
                    }
                }
            }

            // 5.2: the policy's retentionDays.context.history when set, else 1d.
            var retention = await MediaPolicy.RetentionForKindAsync(db, "context.history", 1, ct);
            var days = int.Parse(retention[..^1], System.Globalization.CultureInfo.InvariantCulture);
            db.Artifacts.Add(new ArtifactEntity
            {
                ArtifactId = Guid.NewGuid(),
                WorkId = workId,
                MediaType = "application/json",
                Kind = "context.history",
                Size = put.Bytes,
                Digest = digest,
                StorageUri = $"{store.StorageUriScheme}:{put.RelativePath}",
                State = "stored",
                Verification = "verified",
                Origin = "kernel",
                CreatedBy = $"link:{responder}",
                TeamSlug = teamSlug,
                CreatedAt = now,
                ExpiresAt = now.AddDays(days),
                StoredAt = now,
                RetentionPolicy = retention,
            });
            // The verbatim answer; when the window came off a signed-over
            // (team-owned) thread, the router-ledger provenance rides as one
            // added member — copy-on-forward made queryable.
            var detail = data.GetRawText();
            if (routerLedger is not null)
            {
                var detailNode = JsonNode.Parse(detail)!.AsObject();
                detailNode["routerLedger"] = routerLedger;
                detail = detailNode.ToJsonString();
            }

            audit.Append(db, new AuditRecord(
                Actor: $"link:{responder}",
                Action: "context.fetched",
                Resource: workId is { } w2 ? $"work/{w2}" : $"responder/{responder}",
                Decision: "allow",
                Reason: "answered",
                EvidenceRef: null,
                PolicyVersion: "context-v1",
                Detail: detail));
            await db.SaveChangesAsync(ct);

            // Wake the parked intents (the dependency-gate wake): a fetched
            // answer makes the second fold pass succeed immediately.
            if (workId is { } wake)
            {
                await DependencyGate.WakeGatedRoutingIntentsAsync(db, wake, now, ct);
            }
        }

        return new ContextArm(null, "accepted");
    }

    /// <summary>True when one ledger body's wire reference names exactly this
    /// conversation (the provenance join: ledger rows never move).</summary>
    private static bool CarriesReference(string body, string channel, string rootMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reference", out var reference)
                   && reference.TryGetProperty("channelOrResourceId", out var c)
                   && reference.TryGetProperty("rootThreadId", out var r)
                   && c.ValueKind == JsonValueKind.String && c.GetString() == channel
                   && r.ValueKind == JsonValueKind.String && r.GetString() == rootMessage;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonObject FetchCommand(
        string fetchId, string uri, Guid? workId, string provider, string deadline, DateTime now) =>
        new()
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = FetchType,
            ["subject"] = $"work/{workId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = $"context-fetch:{fetchId}",
                ["workId"] = (workId ?? Guid.Empty).ToString(),
                ["targetId"] = provider,
                ["actorEvidenceRef"] = $"kernel:context-fetch:{fetchId}",
                ["deadline"] = deadline,
                ["contextRefs"] = new JsonArray(uri),
            },
        };

    private static string StableHash(string uri) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(uri)))[..12].ToLowerInvariant();
}
