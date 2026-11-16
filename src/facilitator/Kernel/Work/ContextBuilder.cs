using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Memory;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>What a fold attempt produced: <c>Ready</c> carries the finished
/// document (stored as a <c>context.dispatch</c> artifact, digest stamped);
/// <c>NotReady</c> means a <c>thread://</c> source parked as a pending fetch
/// — the intent requeues under the dependency-gate law and the second pass
/// finds the answer in the audit trail. <c>Note</c> names a degraded build.</summary>
public sealed record ContextBuildResult(
    bool Ready,
    JsonObject? Document,
    Guid? ArtifactId,
    string? Digest,
    string? Note = null);

/// <summary>
/// The dispatch-time context fold: a route whose rule/provider declares a
/// <c>context</c> block gets, beside today's assign envelope, a
/// <c>context.dispatch.v1</c> document — every source resolved in block
/// order, the whole thing stored as a derived artifact (retention default
/// 7d, <c>Origin = "kernel"</c>) and referenced from the assign's
/// <c>inputs.context</c>. "What exactly did the harness see" is evidence:
/// the digest is what the harness got, the artifact is the bytes, one audit
/// row names both. Routes without a block get today's envelope verbatim,
/// forever (parity law).
///
/// Templates resolve under a closed vocabulary (no raw-string templating):
/// <c>$binding.reference</c>/<c>$event.reference</c> (the work's bound
/// conversation as a <c>thread://</c> URI), <c>$work.team</c>,
/// <c>$project.tags</c>, and the media source's <c>$event.data.sourceRefs</c>
/// (the intent's own refs). Anything else is a literal.
///
/// A <c>thread://</c> source probes the fetched-answer audit leg first (the
/// 3.2 ingress records <c>event.context.fetched.v1</c> verbatim); an
/// unanswered one parks the fetch (audit <c>context.fetch</c> /
/// <c>pendingFetches</c> detail, deadline five minutes out — the same
/// deadline the routed fetch command carries) and reports
/// not-ready — the same requeue posture as the dependency gate. A parked
/// fetch past its deadline answers an honest <c>{error: "deadline"}</c>
/// item instead of parking again.
/// </summary>
public sealed class ContextBuilder
{
    // ContextBuilder is captured by the singleton routing sink; every
    // dependency it holds is a singleton, so the broker is constructed
    // directly here (the DecodeContextBuilder precedent: a singleton sink may
    // not capture the scoped MemoryBroker registration).
    private readonly IArtifactStore store;
    private readonly AuditWriter audit;
    private readonly InteractionOptions interaction;
    private readonly TimeProvider timeProvider;
    private readonly Facilitator.Kernel.Integrations.ProviderRegistry providers;
    private readonly MemoryBroker memory;

    public ContextBuilder(
        IDbContextFactory<FacilitatorDbContext> contextFactory,
        IArtifactStore store,
        AuditWriter audit,
        IMemoryProvider memoryProvider,
        Facilitator.Kernel.Shared.Dispatch.KernelMetrics metrics,
        MemoryOptions memoryOptions,
        InteractionOptions interaction,
        TimeProvider timeProvider,
        Facilitator.Kernel.Integrations.ProviderRegistry providers)
    {
        this.store = store;
        this.audit = audit;
        this.interaction = interaction;
        this.timeProvider = timeProvider;
        this.providers = providers;
        memory = new MemoryBroker(contextFactory, audit, memoryProvider, metrics, timeProvider, memoryOptions);
    }

    /// <summary>The document schema label the harness reads.</summary>
    public const string Schema = "context.dispatch.v1";

    /// <summary>Derived-and-disposable: dispatch context docs expire in a week.</summary>
    public const string RetentionPolicy = "7d";

    /// <summary>Build (or park) the context document for one dispatch, on the
    /// routing sink's ambient db (same transaction as the assign envelope).</summary>
    public async Task<ContextBuildResult> BuildAsync(
        FacilitatorDbContext db,
        IReadOnlyList<Integrations.ContextSource> block,
        RoutingIntent intent,
        string? commandId,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == intent.WorkId)
            .Select(w => new
            {
                w.WorkId,
                w.Objective,
                w.State,
                w.Version,
                w.Origin,
                w.CreatedAt,
                TeamSlug = w.Team!.Slug,
                ProjectConfig = w.Project!.Config,
            })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return new ContextBuildResult(false, null, null, null, "work-not-found");
        }

        var binding = await db.CommunicationBindings.AsNoTracking()
            .Where(b => b.WorkId == intent.WorkId)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var threadUri = binding is null
            ? null
            : $"thread://{binding.Provider}/{binding.Channel}/{binding.RootMessageId}";
        var sourceRefs = IntentSourceRefs(intent);

        // The team's media policy (5.1): its history switch caps what a
        // harness may be shown of the team's conversation. Absent row = the
        // system default (20 messages).
        var policy = await MediaPolicy.ForTeamAsync(db, work.TeamSlug, null, ct);

        var budget = block.FirstOrDefault(row => row.Budget is not null)?.Budget;
        var totalBytes = budget?.TotalBytes ?? 32768;
        var sources = new JsonArray();
        var truncated = false;
        Dictionary<string, ArtifactEntity>? storedRows = null;

        for (var index = 0; index < block.Count; index++)
        {
            var row = block[index];
            var path = $"context[{index}]";
            switch (row)
            {
                case { Envelope: { } envelopeMode }:
                    sources.Add(new JsonObject
                    {
                        ["path"] = path,
                        ["key"] = "envelope",
                        ["required"] = envelopeMode == "required",
                        ["work"] = new JsonObject
                        {
                            ["workId"] = work.WorkId.ToString("N"),
                            ["objective"] = work.Objective,
                            ["state"] = work.State,
                            ["origin"] = work.Origin,
                            ["createdAt"] = work.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                System.Globalization.CultureInfo.InvariantCulture),
                        },
                    });
                    break;

                case { Work: { } workSource }:
                    var fields = new JsonObject();
                    foreach (var field in workSource.Fields)
                    {
                        switch (field)
                        {
                            case "objective":
                                fields["objective"] = work.Objective;
                                break;
                            case "state":
                                fields["state"] = work.State;
                                break;
                            case "version":
                                fields["version"] = work.Version;
                                break;
                            case "team":
                                fields["team"] = work.TeamSlug;
                                break;
                            default:
                                fields[field] = null;
                                break;
                        }
                    }

                    sources.Add(new JsonObject
                    {
                        ["path"] = path,
                        ["key"] = "work",
                        ["fields"] = fields,
                    });
                    break;

                case { Timeline: { } timeline }:
                    var lastEvents = await db.WorkEvents.AsNoTracking()
                        .Where(e => e.WorkId == intent.WorkId)
                        .OrderByDescending(e => e.WorkVersion)
                        .Take(Math.Clamp(timeline.Last, 1, 100))
                        .ToListAsync(ct);
                    var timelineArray = new JsonArray();
                    foreach (var workEvent in lastEvents)
                    {
                        timelineArray.Add((JsonNode)new JsonObject
                        {
                            ["version"] = workEvent.WorkVersion,
                            ["kind"] = workEvent.Kind,
                            ["actor"] = workEvent.Actor,
                            ["detail"] = TryJson(workEvent.Detail),
                        });
                    }

                    sources.Add(new JsonObject
                    {
                        ["path"] = path,
                        ["key"] = "timeline",
                        ["events"] = timelineArray,
                    });
                    break;

                case { Memory: { } memorySource }:
                    sources.Add(await RecallSourceAsync(path, memorySource, work.TeamSlug, intent, ct));
                    break;

                case { Media: { } mediaSource }:
                    storedRows ??= await StoredRefsByTeamAsync(db, intent.WorkId, work.TeamSlug, ct);
                    sources.Add(MediaSource(path, mediaSource, sourceRefs, storedRows));
                    break;

                case { History: { } history }:
                    var uri = ResolveTemplate(history.Thread, threadUri, work.TeamSlug, work.ProjectConfig)
                        is { Length: > 0 } resolved
                        ? resolved
                        : threadUri;
                    if (uri is null)
                    {
                        sources.Add(new JsonObject
                        {
                            ["path"] = path,
                            ["key"] = "history",
                            ["error"] = "no-bound-thread",
                        });
                        break;
                    }

                    var answer = await FetchedItemAsync(db, intent.WorkId, uri, ct);
                    if (answer is null)
                    {
                        var expired = AwaitingPastDeadline(db, intent.WorkId, uri, now);
                        if (!expired)
                        {
                            await ParkPendingFetchAsync(
                                db, intent, commandId, uri, uri["thread://".Length..].Split('/')[0], now, ct);
                            return new ContextBuildResult(false, null, null, null, "pending-thread-fetch");
                        }

                        sources.Add(new JsonObject
                        {
                            ["path"] = path,
                            ["key"] = "history",
                            ["uri"] = uri,
                            ["error"] = "deadline",
                        });
                        break;
                    }

                    var window = answer.DeepClone().AsObject();
                    // The team's federation law caps the window a harness may
                    // see (policy.HistoryMaxMessages); the block's own `last`
                    // may only narrow it further.
                    var harnessCap = Math.Min(
                        policy.HistoryMaxMessages,
                        history.Last > 0 ? history.Last : policy.HistoryMaxMessages);
                    if (window["content"]?.AsObject() is { } content
                        && content["messages"] is JsonArray messages
                        && messages.Count > harnessCap)
                    {
                        var clipped = new JsonArray();
                        foreach (var message in messages.Take(harnessCap))
                        {
                            clipped.Add(message?.DeepClone());
                        }

                        content["messages"] = clipped;
                        window["truncated"] = true;
                    }

                    sources.Add(new JsonObject
                    {
                        ["path"] = path,
                        ["key"] = "history",
                        ["uri"] = uri,
                        ["order"] = history.Order ?? "asc",
                        ["answer"] = window,
                    });
                    break;
            }

            // Budget law: over the block's byte budget ⇒ trim the oldest
            // NON-envelope/work source and say so on the document.
            if (sources.ToJsonString().Length > totalBytes)
            {
                truncated = true;
                var firstOptional = sources.FirstOrDefault(node =>
                    node?["key"]?.GetValue<string>() is not ("envelope" or "work"));
                if (firstOptional is not null)
                {
                    sources.Remove(firstOptional);
                }
            }
        }

        var document = new JsonObject
        {
            ["schema"] = Schema,
            ["workId"] = intent.WorkId.ToString("N"),
            ["sources"] = sources,
            ["truncated"] = truncated,
            ["budgetBytes"] = totalBytes,
        };
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        document["digest"] = digest;

        // Store the document as a derived artifact on the sink's ambient
        // transaction: the kernel hashed the bytes itself ⇒ verified, and the
        // row is born `stored` (no upload leg for a kernel document).
        var put = await store.PutAsync(new MemoryStream(bytes), ct);
        var artifactId = Guid.NewGuid();
        // 5.2: the policy's retentionDays.context.dispatch when set, else 7d.
        var docRetention = await MediaPolicy.RetentionForKindAsync(db, "context.dispatch", 7, ct);
        var docDays = int.Parse(docRetention[..^1], System.Globalization.CultureInfo.InvariantCulture);
        db.Artifacts.Add(new ArtifactEntity
        {
            ArtifactId = artifactId,
            WorkId = intent.WorkId,
            MediaType = "application/json",
            Size = put.Bytes,
            Digest = digest,
            StorageUri = $"{store.StorageUriScheme}:{put.RelativePath}",
            State = "stored",
            Verification = "verified",
            Origin = "kernel",
            CreatedBy = "kernel",
            TeamSlug = work.TeamSlug,
            CommandId = commandId is null ? null : $"context-dispatch:{commandId}",
            Kind = "context.dispatch",
            CreatedAt = now,
            ExpiresAt = now.AddDays(docDays),
            StoredAt = now,
            RetentionPolicy = docRetention,
        });
        audit.Append(db, new AuditRecord(
            Actor: "kernel",
            Action: "context.dispatch",
            Resource: $"work/{intent.WorkId}",
            Decision: "allow",
            Reason: truncated ? "truncated" : "built",
            EvidenceRef: $"artifact/{artifactId}",
            PolicyVersion: Schema,
            Detail: JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["digest"] = digest,
                ["sources"] = sources.Count.ToString(),
                ["bytes"] = bytes.Length.ToString(),
            })));

        return new ContextBuildResult(true, document, artifactId, digest,
            truncated ? "budget-truncated" : null);
    }

    /// <summary>The memory source: a broker recall scoped to the route team,
    /// plus one usage attestation per hit (justification = the block's index
    /// path — the event.memory.used law).</summary>
    private async Task<JsonObject> RecallSourceAsync(
        string path,
        Integrations.ContextMemory source,
        string? teamSlug,
        RoutingIntent intent,
        CancellationToken ct)
    {
        var query = ResolveTemplate(source.Recall, null, teamSlug, null) is { Length: > 0 } template
            ? template
            : intent.Objective;
        var selectors = source.Scopes
            .Select(selector => ResolveTemplate(selector, null, teamSlug, null) ?? selector)
            .ToList();
        var access = new MemoryAccessContext(
            "kernel:context-builder", teamSlug,
            new HashSet<string>(StringComparer.Ordinal), IsWorkload: true);
        var response = await memory.RecallAsync(
            access,
            new MemoryRecallRequest(query, "assignment.context", intent.WorkId.ToString(), selectors,
                Math.Clamp(source.Limit, 1, 32)),
            ct);
        var entries = new JsonArray();
        foreach (var entry in response.Entries)
        {
            entries.Add((JsonNode)new JsonObject
            {
                ["entryId"] = entry.EntryId.ToString("N"),
                ["type"] = entry.Type,
                ["content"] = entry.Content,
                ["retrievalReason"] = entry.RetrievalReason,
            });
            await memory.RecordUsageAsync(access, entry.EntryId.ToString("N"), "kernel:context-builder", path, ct);
        }

        return new JsonObject
        {
            ["path"] = path,
            ["key"] = "memory",
            ["entries"] = entries,
            ["truncated"] = response.Truncated,
        };
    }

    /// <summary>The media source: the intent's own sourceRefs, enriched with
    /// the stored-row facts (digest, brokered content URL) and size-capped
    /// by the block; drops name their reason (never silent).</summary>
    private JsonObject MediaSource(
        string path,
        Integrations.ContextMedia source,
        JsonArray? refs,
        Dictionary<string, ArtifactEntity> stored)
    {
        var from = source.From ?? "$event.data.sourceRefs";
        var items = new JsonArray();
        var dropped = new JsonArray();
        var publicBase = interaction.PublicBaseUrl?.TrimEnd('/');
        if (from == "$event.data.sourceRefs" && refs is not null)
        {
            foreach (var node in refs)
            {
                var artifactId = node?["artifactId"]?.GetValue<string>();
                if (artifactId is null)
                {
                    continue;
                }

                var normalized = artifactId.Replace("-", string.Empty).ToLowerInvariant();
                if (!stored.TryGetValue(normalized, out var row))
                {
                    dropped.Add((JsonNode)new JsonObject { ["artifactId"] = artifactId, ["reason"] = "not-stored" });
                    continue;
                }

                if (row.Size > source.MaxBytes)
                {
                    dropped.Add((JsonNode)new JsonObject { ["artifactId"] = artifactId, ["reason"] = "size-cap" });
                    continue;
                }

                items.Add((JsonNode)new JsonObject
                {
                    ["artifactId"] = row.ArtifactId.ToString("N"),
                    ["mediaType"] = row.MediaType,
                    ["size"] = row.Size,
                    ["digest"] = row.Digest,
                    ["contentUrl"] = string.IsNullOrWhiteSpace(publicBase)
                        ? $"/api/artifacts/{row.ArtifactId:N}/content"
                        : $"{publicBase}/api/artifacts/{row.ArtifactId:N}/content",
                });
            }
        }

        return new JsonObject
        {
            ["path"] = path,
            ["key"] = "media",
            ["from"] = from,
            ["items"] = items,
            ["dropped"] = dropped,
        };
    }

    private static async Task<Dictionary<string, ArtifactEntity>> StoredRefsByTeamAsync(
        FacilitatorDbContext db, Guid workId, string? teamSlug, CancellationToken ct)
    {
        var rows = await db.Artifacts.AsNoTracking()
            .Where(a => (a.WorkId == workId || a.TeamSlug == teamSlug) && a.State == "stored")
            .ToListAsync(ct);
        var map = new Dictionary<string, ArtifactEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            map[row.ArtifactId.ToString("N")] = row;
        }

        return map;
    }

    /// <summary>The fetched-answer probe: the 3.2 ingress records one audit
    /// row per <c>event.context.fetched.v1</c> (action <c>context.fetched</c>,
    /// subject <c>work/{id}</c>, detail = the event data verbatim). The item
    /// whose <c>uri</c> matches answers the history source; absent ⇒ null.</summary>
    private static async Task<JsonObject?> FetchedItemAsync(
        FacilitatorDbContext db, Guid workId, string uri, CancellationToken ct)
    {
        var details = await db.Audit.AsNoTracking()
            .Where(a => a.Action == "context.fetched" && a.Resource == $"work/{workId}")
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => a.Detail)
            .ToListAsync(ct);
        foreach (var detail in details)
        {
            try
            {
                using var doc = JsonDocument.Parse(detail);
                if (!doc.RootElement.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (string.Equals(item.GetProperty("uri").GetString(), uri, StringComparison.Ordinal))
                    {
                        return JsonSerializer.SerializeToNode(item.Deserialize<JsonElement>())?.AsObject();
                    }
                }
            }
            catch (JsonException)
            {
                // a malformed answer is no answer
            }
        }

        return null;
    }

    /// <summary>True when this URI sits in a parked <c>context.fetch</c> audit
    /// whose deadline has passed: the second pass then answers the deadline
    /// error instead of parking forever.</summary>
    private static bool AwaitingPastDeadline(
        FacilitatorDbContext db, Guid workId, string uri, DateTime now)
    {
        var details = db.Audit.AsNoTracking()
            .Where(a => a.Action == "context.fetch" && a.Resource == $"work/{workId}")
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => a.Detail)
            .ToList();
        var cutoff = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var detail in details)
        {
            try
            {
                using var doc = JsonDocument.Parse(detail);
                if (!doc.RootElement.TryGetProperty("pendingFetches", out var pending)
                    || pending.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in pending.EnumerateArray())
                {
                    if (string.Equals(entry.GetProperty("uri").GetString(), uri, StringComparison.Ordinal)
                        && entry.TryGetProperty("deadline", out var deadline)
                        && string.CompareOrdinal(deadline.GetString(), cutoff) < 0)
                    {
                        return true;
                    }
                }
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        return false;
    }

    /// <summary>Park the pending fetch (the 3.3 record: an audit row whose
    /// detail names <c>pendingFetches:[{uri,fetchId,deadline}]</c>) AND route
    /// the read (the 3.2 leg): a <c>command.context.fetch.v1</c> rides the
    /// SOURCE adapter's endpoint outbox — deterministic
    /// <c>context-fetch:&lt;fetchId&gt;</c> command id (ledger-idempotent like
    /// <c>communication-reply:</c>), the same envelope the link-plane
    /// <see cref="ContextIngress"/> answers inline. The caller reports
    /// not-ready; the routing sink parks the intent as pending, the adapter
    /// answers with <c>event.context.fetched.v1</c>, and the answer's arrival
    /// wakes the intent (3.2 ingress).</summary>
    private async Task ParkPendingFetchAsync(
        FacilitatorDbContext db,
        RoutingIntent intent,
        string? commandId,
        string uri,
        string? provider,
        DateTime now,
        CancellationToken ct)
    {
        var fetchId = FetchIdFor(intent.EffectCommandId, uri);
        var deadline = now.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var record = new JsonObject
        {
            ["pendingFetches"] = new JsonArray(new JsonObject
            {
                ["uri"] = uri,
                ["fetchId"] = fetchId,
                ["deadline"] = deadline,
            }),
        };
        audit.Append(db, new AuditRecord(
            Actor: "kernel",
            Action: "context.fetch",
            Resource: $"work/{intent.WorkId}",
            Decision: "allow",
            Reason: "pending-thread",
            EvidenceRef: commandId is null ? null : $"routing/{commandId}",
            PolicyVersion: "context-v1",
            Detail: record.ToJsonString()));

        // Route the read to the source adapter when its endpoint is known;
        // an unknown provider parks honestly and dies on the deadline.
        var endpoint = provider is null ? null : providers.Find(provider)?.EndpointId;
        if (endpoint is { Length: > 0 })
        {
            var envelope = new JsonObject
            {
                ["specversion"] = "1.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["source"] = EnvelopeFactory.KernelSource,
                ["type"] = ContextFetchService.CommandType,
                ["subject"] = $"work/{intent.WorkId}",
                ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["datacontenttype"] = "application/json",
                ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.context.fetch.v1.json",
                ["data"] = new JsonObject
                {
                    ["commandId"] = $"context-fetch:{fetchId}",
                    ["workId"] = intent.WorkId.ToString(),
                    ["targetId"] = provider,
                    ["actorEvidenceRef"] = $"kernel:context-fetch:{fetchId}",
                    ["deadline"] = deadline,
                    ["contextRefs"] = new JsonArray(uri),
                },
            };
            db.Outbox.Add(new OutboxEntity
            {
                OutboxId = Guid.NewGuid(),
                RecipientEndpointId = $"link.{endpoint}",
                AggregateType = "work",
                AggregateId = intent.WorkId,
                EventType = ContextFetchService.CommandType,
                CommandId = $"context-fetch:{fetchId}",
                Envelope = envelope.ToJsonString(),
                WorkId = intent.WorkId,
                Status = "pending",
                AvailableAt = now,
                Attempts = 0,
                CreatedAt = now,
            });
            await PostgresNotifications.NotifyOutboxAsync(db, intent.WorkId.ToString(), ct);
        }
    }

    /// <summary>The deterministic fetch id: the routed fetch command and the
    /// answer both name this id, derived from the effect command + URI.</summary>
    public static string FetchIdFor(string effectCommandId, string uri) =>
        Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes($"facilitator:context-fetch:{effectCommandId}|{uri}"))).ToLowerInvariant();

    /// <summary>Resolve one <c>$…</c> template (closed vocabulary); a
    /// non-template rides verbatim, an unknown template resolves to null.</summary>
    internal static string? ResolveTemplate(
        string? template, string? threadUri, string? teamSlug, string? projectConfigJson)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        if (!template.StartsWith('$'))
        {
            return template;
        }

        return template switch
        {
            "$binding.reference" or "$event.reference" => threadUri,
            "$work.team" => teamSlug,
            "$project.tags" => ProjectTags(projectConfigJson),
            _ => null,
        };

        static string? ProjectTags(string? configJson)
        {
            if (configJson is null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(configJson);
                return doc.RootElement.TryGetProperty("tags", out var tags)
                       && tags.ValueKind == JsonValueKind.Array
                    ? string.Join(' ', tags.EnumerateArray()
                        .Where(tag => tag.ValueKind == JsonValueKind.String)
                        .Select(tag => tag.GetString()))
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static JsonNode? TryJson(string detail)
    {
        try
        {
            return JsonNode.Parse(detail);
        }
        catch (JsonException)
        {
            return JsonValue.Create(detail);
        }
    }

    private static JsonArray? IntentSourceRefs(RoutingIntent intent)
    {
        var json = intent.SourceRefsJson;
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json)?.AsArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
