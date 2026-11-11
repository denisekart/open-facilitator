using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Integrations;

/// <summary>Ingress outcome of one provider-class event (already settled by the store).</summary>
/// <param name="Status">Admission status wire value ("accepted"|"duplicate") when admitted.</param>
/// <param name="InboxId">Inbox row identity, when one was written.</param>
/// <param name="Problem">Refusal, when the event was refused.</param>
public sealed record ProviderIngressOutcome(string? Status, Guid? InboxId, ProblemDetails? Problem, object? Data = null)
{
    /// <summary>The admission shape the link ingress returns for this outcome.</summary>
    public LinkAdmission ToAdmission() => Problem is not null
        ? new LinkAdmission(Problem, null, null)
        : new LinkAdmission(null, Status, InboxId, Data);
}

/// <summary>Outcome of a human-plane code redemption (deviation D2).</summary>
public enum RedeemOutcome
{
    /// <summary>Code valid: identity upserted, provenance + confirmation landed.</summary>
    Redeemed,

    /// <summary>No challenge carries this code hash.</summary>
    Unknown,

    /// <summary>Single-use fence: the code was consumed already.</summary>
    Consumed,

    /// <summary>The window elapsed.</summary>
    Expired,
}

/// <summary>Normalized reply target (conversationTarget schema shape).</summary>
public sealed record ConversationTarget(
    string Provider,
    string? Tenant,
    string? Channel,
    string? RootThreadId,
    string? ParentMessageId,
    string? SubjectRef,
    string ConversationKind,
    string? ActorProviderAccountId);

/// <summary>
/// The provider-ingest plane (locked decision 1): one transaction per
/// normalized provider event — insert-or-duplicate <c>provider_events</c>
/// (canonical-data sha), add the inbox row (wire-bytes sha + provider fields),
/// then the business-effect arms appending outbox rows, all before commit.
/// Duplicate on <c>(provider, provider_delivery_id)</c> answers
/// <c>duplicate</c> with zero new rows: delivery metadata is never business
/// completion. Echo law: events authored by the configured bot
/// id settle as the outbound-post ledger update only (binding
/// <c>parent_message_id</c>) — suppressed before the inbox, no arm effects.
/// Reaction-never-consent: the reaction arm writes the timeline note +
/// configured ack ONLY; it has no path to <c>approval.respond</c> or any Work
/// transition — the deny-list is structural (no transition call site exists in
/// this arm) and test-enforced.
/// </summary>
public sealed class ProviderEventStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    KernelMetrics metrics,
    ProviderRegistry registry,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CommandApplier applier,
    AiPolicyStore policyStore,
    AiOptions aiOptions)
{
    /// <summary>Per-team home for provider-created Work: the manifest
    /// ledger's team scope resolves Work → Project → Team, so a Work with no
    /// project cannot carry a result manifest. Find-or-create by fixed name.
    /// </summary>
    public const string ProviderProjectName = "provider-ingress";

    /// <summary>Emoji of the low-noise acknowledgement (notifications replyVsReaction=reaction).</summary>
    public const string AckEmoji = "white_check_mark";

    /// <summary>Acknowledgement emoji for ingested human reactions.</summary>
    public const string ReactionAckEmoji = "eye";

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Challenge lifetime (single-use, short).</summary>
    public static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(15);

    private const string InsertProviderEventSql = """
        INSERT INTO facilitator.provider_events
            (provider_event_id, provider, provider_delivery_id, body_sha256, body, verified_reference, received_at)
        VALUES
            (@id, @provider, @deliveryId, @bodySha, @body::jsonb, @verified, @receivedAt)
        ON CONFLICT DO NOTHING
        """;

    private static readonly HttpClient BotIdentityClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private string? resolvedBotId;

    /// <summary>Exact-match echo identity: the configured id wins; else
    /// resolve once from the provider via GET <see cref="ProviderOptions.BotIdentityProbeUrl"/>
    /// under the bot token (live bot ids are provider-generated, so compose
    /// cannot pin them). The probe is pure config data: providers that
    /// configure <see cref="ProviderOptions.BotUserId"/> (adapters running as
    /// the bot user) need no probe, and the kernel holds no vendor API shape.
    /// A failed resolve stays unresolved (retry next event) — echo events then
    /// fall to the message arm, which is side-effect-free for unbound
    /// threads.</summary>
    private async Task<string?> ResolveBotIdAsync(ProviderOptions options, CancellationToken ct)
    {
        if (options.BotUserId is { Length: > 0 } configured)
        {
            return configured;
        }

        if (options.BotIdentityProbeUrl is not { Length: > 0 } probeUrl)
        {
            return null;
        }

        if (Volatile.Read(ref resolvedBotId) is { Length: > 0 } cached)
        {
            return cached;
        }

        var token = options.BotToken;
        if (string.IsNullOrEmpty(token) && options.BotTokenFile is { Length: > 0 } tokenFile)
        {
            // Written post-mint by the compose provision one-shot; absence
            // simply keeps the resolver unresolved until it lands.
            try
            {
                token = File.ReadAllText(tokenFile).Trim();
            }
            catch (IOException)
            {
                token = null;
            }
            catch (UnauthorizedAccessException)
            {
                token = null;
            }
        }

        if (token is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            request.Headers.Add("Authorization", $"Bearer {token}");
            using var response = await BotIdentityClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var id = doc.RootElement.GetProperty("id").GetString();
            Volatile.Write(ref resolvedBotId, id);
            return id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Admit one provider-class event envelope end to end (dedupe + arms),
    /// owning its transaction. The caller (link ingress) has already screened
    /// the envelope, so it is contract-valid here.
    /// </summary>
    public async Task<ProviderIngressOutcome> IngestAsync(JsonElement root, string rawBody, LinkPrincipal workload, CancellationToken ct)
    {
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        // Defense-in-depth vs the admission gate: the provider token is data
        // (never the type), it must name a registered provider, the type must
        // be a central domain type, and data.provider must agree with
        // reference.provider.
        var data = root.GetProperty("data");
        var token = ProviderEventClass.ProviderOfPayload(root);
        if (token is not { Length: > 0 }
            || !registry.TryGet(token, out var options)
            || !ProviderEventClass.IsCentralType(type)
            || data.GetProperty("reference").GetProperty("provider").GetString() != token)
        {
            return new ProviderIngressOutcome(null, null, ProblemErrors.Unsupported($"provider event type '{type}' has no registered provider"));
        }

        var providerDeliveryId = data.GetProperty("providerDeliveryId").GetString()
            ?? throw new InvalidOperationException("provider event without providerDeliveryId");
        var provider = options.Provider;
        var body = data.GetRawText();
        var bodySha = "sha256:" + Convert.ToHexString(SHA256.HashData(ApprovalDigest.CanonicalBytes(data))).ToLowerInvariant();
        var wireSha = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var actor = data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty;
        var reference = ReferenceOf(data, provider);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var botId = await ResolveBotIdAsync(options, ct);

        var providerEventId = Guid.NewGuid();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. provider_events ledger: the (provider, delivery-id) unique is the
        // business-dedupe spine, independent of envelope-id resends.
        var affected = await db.Database.ExecuteSqlRawAsync(
            InsertProviderEventSql,
            [
                new Npgsql.NpgsqlParameter("@id", providerEventId),
                new Npgsql.NpgsqlParameter("@provider", provider),
                new Npgsql.NpgsqlParameter("@deliveryId", providerDeliveryId),
                new Npgsql.NpgsqlParameter("@bodySha", bodySha),
                new Npgsql.NpgsqlParameter("@body", body),
                new Npgsql.NpgsqlParameter("@verified", $"urn:ofac:provider:{provider}:{workload.TeamSlug}"),
                new Npgsql.NpgsqlParameter("@receivedAt", now),
            ], ct);
        if (affected == 0)
        {
            // Saved judgment (the arm decision the first delivery settled):
            // delivery metadata is never business completion, the retry sees
            // the same answer with zero new rows. The ack identity is the
            // existing ledger row (the hub Envelope ack dereferences it).
            var saved = await db.ProviderEvents.AsNoTracking()
                .Where(e => e.Provider == provider && e.ProviderDeliveryId == providerDeliveryId)
                .Select(e => new { e.ProviderEventId, e.VerifiedReference })
                .FirstOrDefaultAsync(ct);
            await tx.RollbackAsync(ct);
            metrics.LinkReceived();
            metrics.InboxProcessed();
            return new ProviderIngressOutcome(
                "duplicate", saved?.ProviderEventId, null,
                saved?.VerifiedReference is null ? null : new { judgment = saved.VerifiedReference });
        }

        // 2. Duplicate law (N11): a re-delivery of ONE message can carry a
        // fresh delivery id, so the (provider, delivery-id) spine alone would
        // process it twice. The wire body itself is the message identity: a
        // prior accepted row with the SAME body marks this delivery a
        // duplicate. The ledger row stands (the delivery happened), the
        // judgment records the duplicate, and the work the first delivery
        // landed on gets a visible note - never a silent second work.
        // The business identity of one message: the arm data WITHOUT the
        // delivery id — adapters may mint a fresh delivery id per attempt, but
        // the native reference (channel, thread, message id) and the payload
        // are the message itself, so only a genuine re-delivery of one native
        // message can match. Same provider, same identity, 24h window.
        var canonical = CanonicalizeBody(data);
        var priorEvent = (await db.ProviderEvents.AsNoTracking()
                .Where(e => e.Provider == provider && e.ProviderEventId != providerEventId
                            && e.ReceivedAt >= now.AddDays(-1))
                .OrderByDescending(e => e.ReceivedAt)
                .Select(e => new { e.ProviderEventId, e.Body, e.VerifiedReference })
                .Take(DuplicateWindowCandidates)
                .ToListAsync(ct))
            // Only a prior delivery that actually LANDED a work can note the
            // duplicate: an unbound first pass (e.g. an unlinked mention) is
            // not an ask, and a later linked one must create normally.
            .FirstOrDefault(e => SameMessage(e.Body, canonical) && JudgmentWorkId(e.VerifiedReference) is not null);
        var priorJudgment = priorEvent?.VerifiedReference;
        if (priorEvent is not null)
        {
            var priorWork = JudgmentWorkId(priorJudgment);
            if (priorWork is { } notedWork)
            {
                var priorVersion = await db.Works.Where(w => w.WorkId == notedWork)
                    .Select(w => w.Version).FirstOrDefaultAsync(ct);
                db.WorkEvents.Add(new WorkEventEntity
                {
                    EventId = Guid.NewGuid(),
                    WorkId = notedWork,
                    WorkVersion = priorVersion,
                    Kind = "provider.duplicate",
                    Actor = $"provider:{provider}",
                    Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                    {
                        ["note"] = "a second delivery of this message was seen - the ask was NOT duplicated",
                        ["deliveryId"] = providerDeliveryId,
                    }),
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.LinkReceived();
            metrics.InboxProcessed();
            return new ProviderIngressOutcome(
                "duplicate", providerEventId, null,
                new { judgment = $"duplicate-seen:{priorJudgment ?? "unbound"}" });
        }

        // 3. Echo law: a bot-authored event is the outbound-post ledger
        // write — suppress before the inbox, persist to the binding. One
        // exemption (mirrored in the adapter's normalizer): a bot's own
        // change_request opened/reopened is a load-bearing state change — the
        // head-branch resolver binds the parked `branch:<ref>` marker to the
        // minted PR number only if this event reaches the CR arm, and nothing
        // answers it, so no self-answering loop can form.
        var botStateChange = type == ProviderEventClass.ChangeRequest
            && data.TryGetProperty("action", out var actionEl)
            && actionEl.ValueKind == JsonValueKind.String
            && actionEl.GetString() is "opened" or "reopened";
        if (!botStateChange && botId is { Length: > 0 } bot && actor == bot)
        {
            var echoed = await db.CommunicationBindings
                .FirstOrDefaultAsync(b => b.Provider == provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
            if (echoed is not null && reference.MessageId is { Length: > 0 } mid)
            {
                echoed.ParentMessageId = mid;
                await db.SaveChangesAsync(ct);
            }

            await db.ProviderEvents.Where(e => e.ProviderEventId == providerEventId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.VerifiedReference, "echo:post-ledger"), ct);
            await tx.CommitAsync(ct);
            metrics.LinkReceived();
            metrics.InboxProcessed();
            return new ProviderIngressOutcome("accepted", providerEventId, null, new { judgment = "echo:post-ledger" });
        }

        // 4. Inbox row (provider path): provider fields populated, settled.
        var inbox = new InboxEntity
        {
            InboxId = Guid.NewGuid(),
            EventSource = EnvelopeReader.SourceOf(root),
            EventId = EnvelopeReader.EventIdOf(root),
            Provider = provider,
            ProviderDeliveryId = providerDeliveryId,
            Payload = rawBody,
            PayloadSha256 = wireSha,
            State = "processed",
            AvailableAt = now,
            Attempt = 1,
            ReceivedAt = now,
            ProcessedAt = now,
        };
        db.Inbox.Add(inbox);

        // B6/N2 (system-side half): a conversation that has already heard the
        // facilitator `maxExchangesPerConversation` times this window stops
        // - before any arm can post again. State-change classes are exempt:
        // they carry no reply and the echo law already exempts the bot's own.
        if (type is ProviderEventClass.Message or ProviderEventClass.Mention or ProviderEventClass.Reaction
            && await LoopBudgetSpentAsync(db, options, reference, now, ct) is { } spent)
        {
            // The stop settles on the ledger row (echo-law precedent): a
            // redelivered event reads the judgment and stays a duplicate -
            // a silent rollback would let the connector re-drive forever.
            var stopJudgment = $"loop-budget:stop:{spent}";
            await db.SaveChangesAsync(ct);
            await db.ProviderEvents.Where(e => e.ProviderEventId == providerEventId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.VerifiedReference, stopJudgment), ct);
            await tx.CommitAsync(ct);
            metrics.LinkReceived();
            metrics.InboxProcessed();
            return new ProviderIngressOutcome("accepted", providerEventId, null, new { judgment = stopJudgment });
        }

        // D1: cadence resolves channel -> team -> system, one truth (the
        // slash plane and the decode sink read the same resolver).
        var ack = await Facilitator.Kernel.Notifications.NotificationLedger.CadenceAsync(
            db, workload.TeamSlug, $"{reference.Provider}/{reference.Channel}", "reaction", ct);
        var effect = type switch
        {
            ProviderEventClass.Mention => await MentionArmAsync(db, workload, options, data, reference, providerDeliveryId, ack, now, ct),
            ProviderEventClass.Reaction => await ReactionArmAsync(db, options, data, reference, providerDeliveryId, ack, now, ct),
            ProviderEventClass.Issue => await IssueArmAsync(db, workload, options, data, reference, providerDeliveryId, now, ct, type),
            ProviderEventClass.ChangeRequest => await ChangeRequestArmAsync(db, provider, options, data, reference, providerDeliveryId, now, ct),
            ProviderEventClass.Pipeline => await PipelineArmAsync(db, data, reference, providerDeliveryId, now, ct),
            ProviderEventClass.Review => await ReviewArmAsync(db, provider, data, reference, providerDeliveryId, now, ct),
            ProviderEventClass.Message => await MessageArmAsync(db, workload, options, data, reference, providerDeliveryId, now, ct),
            _ => new ArmEffect(null, "observe:unhandled"),
        };
        inbox.WorkId = effect.Work;
        await db.ProviderEvents.Where(e => e.ProviderEventId == providerEventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.VerifiedReference, effect.Judgment), ct);

        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, effect.Work?.ToString(), ct);
        await tx.CommitAsync(ct);
        // Review steers ride ICoordinationStore in ITS OWN transaction (the
        // LinkIngress steer-arm precedent), so they run post-commit; the
        // judgment is already on the ledger row, keeping duplicate replay
        // honest even if the steer itself version-conflicts.
        if (effect.Steer is { } steer)
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Facilitator.Kernel.Work.ICoordinationStore>()
                .ApplySteerAsync(steer.WorkId, steer.Directive,
                    $"{provider}-review:{providerDeliveryId}", steer.ExpectedVersion, ct);
        }
        // Command effects riding their own transaction (approval respond via
        // ApprovalIngress) run post-commit in a fresh scope — the steer
        // precedent; the judgment is already on the ledger row.
        if (effect.PostCommit is { } postCommit)
        {
            using var postScope = scopeFactory.CreateScope();
            await postCommit(postScope.ServiceProvider);
        }
        metrics.LinkReceived();
        metrics.InboxProcessed();
        return new ProviderIngressOutcome("accepted", inbox.InboxId, null, new { judgment = effect.Judgment });
    }

    /// <summary>Redeem a linking code on the human plane (deviation D2): a
    /// code-hashed lookup + single-use fence, the provider_identities upsert,
    /// the audit row, and the confirmation reply — one transaction.</summary>
    public async Task<(RedeemOutcome Outcome, object? Detail)> RedeemAsync(string code, string issuer, string subject, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var codeSha = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()))).ToLowerInvariant();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var challenge = await db.ProviderLinkChallenges.FirstOrDefaultAsync(c => c.CodeSha256 == codeSha, ct);
        if (challenge is null)
        {
            await tx.RollbackAsync(ct);
            return (RedeemOutcome.Unknown, null);
        }

        if (challenge.RedeemedAt is not null)
        {
            await tx.RollbackAsync(ct);
            return (RedeemOutcome.Consumed, null);
        }

        if (now >= challenge.ExpiresAt)
        {
            await tx.RollbackAsync(ct);
            return (RedeemOutcome.Expired, null);
        }

        challenge.RedeemedAt = now;
        challenge.OidcIssuer = issuer;
        challenge.OidcSubject = subject;
        var identity = await db.ProviderIdentities.FirstOrDefaultAsync(i =>
            i.Provider == challenge.Provider && i.Tenant == challenge.Tenant && i.AccountId == challenge.AccountId, ct);
        if (identity is null)
        {
            db.ProviderIdentities.Add(new ProviderIdentityEntity
            {
                IdentityId = Guid.NewGuid(),
                Provider = challenge.Provider,
                Tenant = challenge.Tenant,
                AccountId = challenge.AccountId,
                OidcIssuer = issuer,
                OidcSubject = subject,
                ChallengeId = challenge.ChallengeId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            identity.OidcIssuer = issuer;
            identity.OidcSubject = subject;
            identity.ChallengeId = challenge.ChallengeId;
            identity.UpdatedAt = now;
        }

        db.Audit.Add(new AuditEventEntity
        {
            AuditId = Guid.NewGuid(),
            OccurredAt = now,
            Actor = $"oidc:{issuer}|{subject}",
            Action = "identity.provider-link",
            Resource = $"{challenge.Provider}/{challenge.Tenant}/{challenge.AccountId}",
            Decision = "allow",
            Reason = "challenge-redeemed",
            EvidenceRef = $"challenge:{challenge.ChallengeId}",
            PolicyVersion = "identity-v1",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["challengeId"] = challenge.ChallengeId.ToString("N"),
                ["accountId"] = challenge.AccountId,
            }),
        });
        // Confirmation rides the challenge's own provider endpoint (per-provider
        // addressing); a provider deregistered since the mint simply gets no reply.
        if (registry.Find(challenge.Provider) is { } challengeProvider)
        {
            AppendCommand(db, ReplyEnvelope(
                DmTarget(challenge.Provider, challenge.DeliverToAccountId),
                $"Account {challenge.AccountId} linked to a Facilitator identity.",
                $"link:{challenge.ChallengeId}:confirm",
                $"link/{challenge.ChallengeId}",
                now), challengeProvider, now, workId: null);
        }
        await db.SaveChangesAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, null, ct);
        await tx.CommitAsync(ct);
        return (RedeemOutcome.Redeemed, new
        {
            provider = challenge.Provider,
            tenant = challenge.Tenant,
            accountId = challenge.AccountId,
        });
    }

    // ---- arms ---------------------------------------------------------------

    // Mention: identity-gated, binding-idempotent. The slash plane parses
    // the merged command registry (synchronous, zero AI); live AiDeck
    // queues remaining non-command text for async decode; inert AiDeck keeps
    // the verbatim-Work behavior plus the no-AI hint. /link mints a challenge
    // and never a Work.
    private async Task<ArmEffect> MentionArmAsync(
        FacilitatorDbContext db, Facilitator.Kernel.Shared.Auth.LinkPrincipal workload, ProviderOptions options, JsonElement data, Reference reference, string deliveryId, string replyVsReaction, DateTime now, CancellationToken ct)
    {
        var text = data.GetProperty("text").GetString() ?? string.Empty;
        var actor = data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty;
        // Chat-plane label routing (opt-in per provider): a labeled chat picks
        // the rule's capability/endpoint/team; inbound media refs ride down to
        // Work creation and the timeline. Unlabeled ⇒ today's global pair.
        var rule = MatchChatLabelRule(options, data);
        var media = PayloadSourceRefs(data);

        if (text.TrimStart().StartsWith("/link", StringComparison.Ordinal))
        {
            await MintChallengeAsync(db, options, reference, actor, now, ct);
            return new ArmEffect(null, "link:challenged");
        }

        var linked = await db.ProviderIdentities.AnyAsync(i =>
            i.Provider == reference.Provider && i.Tenant == reference.Tenant && i.AccountId == actor, ct);
        if (!linked)
        {
            // The "how to link" copy is provider data (overlay config): no
            // text configured ⇒ audit only, no reply, no lie.
            if (options.LinkHintText is { Length: > 0 } hint)
            {
                AppendCommand(db, ReplyEnvelope(
                    TargetOf(reference),
                    hint,
                    $"mention:{deliveryId}:unlinked",
                    $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}",
                    now), options, now, workId: null);
            }

            return new ArmEffect(null, "mention:unlinked");
        }

        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is { } boundWork)
        {
            // A reply in a thread with a pending question ANSWERS it — the
            // interactive leg's chat surface (oldest pending request only),
            // instead of landing generic work input.
            if (await Facilitator.Kernel.Work.InteractionIngress.TryAutoRespondAsync(
                    db, envelopeValidator, boundWork, text, reference.Provider, deliveryId, actor, ct) is { } answered)
            {
                await AckAsync(db, options, reference, deliveryId, replyVsReaction, now, ct);
                return new ArmEffect(boundWork, $"input:{answered}:auto-respond");
            }

            boundWork = await AppendWorkInputAsync(db, reference.Provider, boundWork, InputNote(reference, deliveryId, text, media), now, ct, sourceRefs: media);
            await AckAsync(db, options, reference, deliveryId, replyVsReaction, now, ct);
            return new ArmEffect(boundWork, $"work/{boundWork}:input");
        }

        if (binding is not null)
        {
            // Bound conversation without a Work (e.g. a bot-DM channel): ack,
            // no Work.
            await AckAsync(db, options, reference, deliveryId, replyVsReaction, now, ct);
            return new ArmEffect(null, "mention:bound-no-work");
        }

        // Slash plane (deterministic, synchronous): a registry verb (or an
        // unknown "/x" — the applier answers with the /help pointer) runs the
        // shared CommandApplier in THIS transaction; effects riding their own
        // transaction (approval respond) return a PostCommit action, invoked
        // after commit exactly like the steer precedent in the caller.
        var commands = CommandRegistry.Merge(CommandRegistry.BuiltIns, await CommandsPayloadAsync(db, ct));
        var policy = await policyStore.LoadAsync(db, ct);
        if (CommandParser.TryParse(text, commands) is { } parsed)
        {
            var commandEffect = await applier.ApplyAsync(
                db, now, parsed, commands, reference, options, actor, workload.TeamId,
                deliveryId, replyVsReaction, policy, ct, rule: rule, sourceRefs: media);
            return new ArmEffect(commandEffect.WorkId, commandEffect.Judgment, PostCommit: commandEffect.PostCommit);
        }

        // Non-command text with a live decoder: queue the async decode
        // (deterministic command id => crash-replay idempotent) and
        // provisional-ack with a REACTION regardless of replyVsReaction —
        // the decode's own answer is the reply, a reply here would
        // double-talk; the reaction key stays out of the decode fallback's
        // mention:<delivery>:ack slot.
        if (policy.Enabled && aiOptions.HasEndpoints)
        {
            AppendDecodeRow(db, text, reference, actor, deliveryId, workload.TeamId, replyVsReaction, now, data);
            AppendCommand(db, ReactEnvelope(
                TargetOf(reference),
                AckEmoji,
                $"decode:{deliveryId}:ack",
                $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}",
                now), options, now, workId: null);
            return new ArmEffect(null, "decode:queued");
        }

        // Inert AiDeck (absent/disabled `llm` row or no endpoints): the
        // exact verbatim Work + ack the arm always created, plus the hint
        // line that the slash plane works without AI — the shared fallback
        // helper the decode sink uses too, ledgered basis=fallback.
        var verbatim = await applier.VerbatimWorkAsync(
            db, now, text, reference, options, actor, workload.TeamId, deliveryId, replyVsReaction, ct,
            rule: rule, sourceRefs: media);
        return new ArmEffect(verbatim.WorkId, verbatim.Judgment, PostCommit: verbatim.PostCommit);
    }

    // Reaction: timeline note + configured ack ONLY. There is
    // deliberately no approval/transition call in this arm — reaction never
    // feeds consent.
    private async Task<ArmEffect> ReactionArmAsync(
        FacilitatorDbContext db, ProviderOptions options, JsonElement data, Reference reference, string deliveryId, string replyVsReaction, DateTime now, CancellationToken ct)
    {
        var emoji = data.GetProperty("emojiName").GetString() ?? string.Empty;
        var actor = data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty;
        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is { } workId)
        {
            var version = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstOrDefaultAsync(ct);
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = version,
                Kind = "provider.reaction",
                Actor = $"link:{reference.Provider}:{actor}",
                Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["emoji"] = emoji,
                    ["postId"] = reference.MessageId,
                    ["consent"] = "never",
                }),
                CreatedAt = now,
            });
        }

        if (replyVsReaction == "reaction")
        {
            AppendCommand(db, ReactEnvelope(
                TargetOf(reference),
                ReactionAckEmoji,
                $"reaction:{deliveryId}:ack",
                $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}",
                now), options, now, workId: binding?.WorkId);
        }

        return new ArmEffect(binding?.WorkId, binding?.WorkId is { } w ? $"reaction:audited:work/{w}" : "reaction:audited:unbound");
    }

    // Message: /link anywhere (a DM is how the kernel delivers the challenge to an
    // unlinked account — DMs normalize message.v1, never mention.v1) mints a
    // challenge; a slash command anywhere runs the slash plane (a DM is how a
    // human drives /help or /status without a mention); a bound-thread/DM
    // follow-up feeds Work input; everything else is audited (the
    // provider_events + inbox rows) with no effect. Code redemption rides the
    // human plane (D2), so other DM text is never parsed.
    private async Task<ArmEffect> MessageArmAsync(
        FacilitatorDbContext db, Facilitator.Kernel.Shared.Auth.LinkPrincipal workload, ProviderOptions options, JsonElement data, Reference reference, string deliveryId, DateTime now, CancellationToken ct)
    {
        var text = data.GetProperty("text").GetString() ?? string.Empty;
        // Inbound media on a chat message rides Work input / creation the same
        // way the mention arm does; the slash plane here keeps the global
        // routing pair (rule: null).
        var media = PayloadSourceRefs(data);
        if (text.TrimStart().StartsWith("/link", StringComparison.Ordinal))
        {
            await MintChallengeAsync(db, options, reference,
                data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty, now, ct);
            return new ArmEffect(null, "link:challenged");
        }

        // Slash plane on the DM/unmentioned path: only an actual parse
        // (leading "/") enters the applier — free DM text keeps its
        // binding-input behavior below.
        if (text.TrimStart().StartsWith("/", StringComparison.Ordinal))
        {
            var commands = CommandRegistry.Merge(CommandRegistry.BuiltIns, await CommandsPayloadAsync(db, ct));
            if (CommandParser.TryParse(text, commands) is { } parsed)
            {
                var policy = await policyStore.LoadAsync(db, ct);
                var actor = data.GetProperty("actorProviderAccountId").GetString() ?? string.Empty;
                var teamGuid = workload.TeamId;
                var commandEffect = await applier.ApplyAsync(
                    db, now, parsed, commands, reference, options, actor, teamGuid,
                    deliveryId, "reply", policy, ct, sourceRefs: media);
                return new ArmEffect(commandEffect.WorkId, commandEffect.Judgment, PostCommit: commandEffect.PostCommit);
            }
        }

        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is not { } workId)
        {
            return new ArmEffect(null, "message:unbound");
        }

        var messageActor = data.TryGetProperty("actorProviderAccountId", out var acct) ? acct.GetString() ?? "unknown" : "unknown";
        if (await Facilitator.Kernel.Work.InteractionIngress.TryAutoRespondAsync(
                db, envelopeValidator, workId, text, reference.Provider, deliveryId, messageActor, ct) is { } answered)
        {
            return new ArmEffect(workId, $"input:{answered}:auto-respond");
        }

        workId = await AppendWorkInputAsync(db, reference.Provider, workId, InputNote(reference, deliveryId, text: null, media), now, ct, sourceRefs: media);
        return new ArmEffect(workId, $"work/{workId}:input");
    }

    // ---- forge-domain arms --------------------------------------------------
    // Two effects only — Work input and Work creation — zero forge workflow.
    // The ADAPTER decides the Work-trigger label policy (workTrigger flag);
    // the kernel just reads the flag (malleability law). A forge actor's
    // identity is audit-only here (judgment law: identity:not-required) —
    // linking stays chat-first, so no provider_identities gate on these arms.

    private async Task<ArmEffect> IssueArmAsync(
        FacilitatorDbContext db, Facilitator.Kernel.Shared.Auth.LinkPrincipal workload, ProviderOptions options, JsonElement data, Reference reference, string deliveryId, DateTime now, CancellationToken ct, string? wireType = null)
    {
        var action = data.GetProperty("action").GetString() ?? string.Empty;
        var workTrigger = data.TryGetProperty("workTrigger", out var t) && t.ValueKind == JsonValueKind.True;
        var text = data.TryGetProperty("text", out var x) ? x.GetString() : null;
        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);

        if (binding?.WorkId is { } boundWork && action is "opened" or "labeled" or "commented" or "closed")
        {
            boundWork = await AppendWorkInputAsync(db, reference.Provider, boundWork, InputNote(reference, deliveryId, text), now, ct);
            return new ArmEffect(boundWork, $"work/{boundWork}:input");
        }

        if (binding is null && action == "labeled" && workTrigger)
        {
            var objective = Trim(text ?? $"Issue {reference.Channel}#{reference.Root}");
            var rule = MatchAssignRule(options, data);
            var workId = await CreateWorkFromProviderAsync(db, workload, options, reference, objective, deliveryId,
                $"work created (issue #{reference.Root})", rule, now, ct, wireType: wireType,
                ownerActor: data.TryGetProperty("actorProviderAccountId", out var acct) && acct.ValueKind == JsonValueKind.String
                    ? $"provider:{reference.Provider}:{acct.GetString()}"
                    : null);
            // Ack comment effect (decision 4): the adapter posts it as a bot
            // comment; kind rides the reference ("comment").
            AppendCommand(db, ReplyEnvelope(TargetOf(reference), "On it.", $"issue:{deliveryId}:ack",
                $"provider/{reference.Provider}/{reference.Root}", now), options, now, workId);
            return new ArmEffect(workId, $"work/{workId}");
        }

        return new ArmEffect(null, $"observe:{action}");
    }

    private async Task<ArmEffect> ChangeRequestArmAsync(
        FacilitatorDbContext db, string provider, ProviderOptions options, JsonElement data, Reference reference, string deliveryId, DateTime now, CancellationToken ct)
    {
        var action = data.GetProperty("action").GetString() ?? string.Empty;
        var text = data.TryGetProperty("text", out var x) ? x.GetString() : null;
        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is not { } workId)
        {
            // Head-branch resolver: the PR-number binding cannot
            // exist at manifest time (the number is minted provider-side),
            // so the prRequest reader parked a marker binding keyed by the
            // head branch (`branch:<ref>`). An opened/reopened event whose
            // verbatim payload carries that head ref resolves the marker:
            // copy its WorkId onto a PR-number-keyed binding so every
            // later leg (reviewed/commented/merged) routes through the
            // plain lookup above. The marker row stands (harmless: a re-
            // opened PR re-runs this arm and the unique conversation index
            // skips the insert).
            if (action is "opened" or "reopened")
            {
                var headRef = PayloadHeadRef(data);
                var marker = headRef is { Length: > 0 }
                    ? await db.CommunicationBindings.FirstOrDefaultAsync(b =>
                        b.Provider == reference.Provider && b.Channel == reference.Channel
                        && b.RootMessageId == "branch:" + headRef, ct)
                    : null;
                if (marker?.WorkId is { } resolved)
                {
                    // ON CONFLICT DO NOTHING: a redelivery of the same
                    // opened event (or a binding row that raced us here)
                    // reconciles to the first writer's row.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         INSERT INTO facilitator.communication_bindings
                             (binding_id, provider, channel, root_message_id, parent_message_id, work_id, idempotency_key, created_at)
                         VALUES (gen_random_uuid(), {reference.Provider}, {reference.Channel}, {reference.Root},
                                 {marker.ParentMessageId}, {resolved}, {"pr-binding:" + deliveryId}, {now})
                         ON CONFLICT DO NOTHING
                         """, ct);
                    return new ArmEffect(resolved, $"pr-bound:{reference.Root}");
                }
            }

            return new ArmEffect(null, "observe:unbound");
        }

        if (action is "reviewed" or "commented")
        {
            // Steer rides ICoordinationStore in its own tx → post-commit (the
            // LinkIngress steer-arm precedent). Judgment recorded pre-commit.
            var version = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstOrDefaultAsync(ct);
            var directive = Trim(text ?? $"{provider} {action} on change request #{reference.Root}");

            // K4/K6: a review on the change request is a timeline decision.
            await AppendReviewDecisionAsync(db, provider, workId, version, data, reference, action, text, now, ct);
            return new ArmEffect(workId, $"steered:{workId}", (workId, directive, version));
        }

        if (action is "merged" or "closed")
        {
            workId = await AppendWorkInputAsync(db, reference.Provider, workId,
                InputNote(reference, deliveryId, $"Change request #{reference.Root} {action}"), now, ct);
            return new ArmEffect(workId, $"work/{workId}:input");
        }

        return new ArmEffect(null, $"observe:{action}");
    }

    // Review (E1/E4): a formal verdict on the change request - the decision
    // class the forge-native `reviewed` action was too weak to carry. Same
    // decision row as the reviewed action, plus the steer; a review on a
    // COMPLETED item forks through AppendWorkInputAsync's terminal law (the
    // original is untouched, the successor carries the review as input).
    private async Task<ArmEffect> ReviewArmAsync(
        FacilitatorDbContext db, string provider, JsonElement data, Reference reference, string deliveryId, DateTime now, CancellationToken ct)
    {
        var action = data.GetProperty("action").GetString() ?? string.Empty;
        var state = data.GetProperty("state").GetString() ?? string.Empty;
        var text = data.TryGetProperty("text", out var t_) && t_.ValueKind == JsonValueKind.String
            ? t_.GetString()
            : string.Empty;
        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is not { } boundWork)
        {
            return new ArmEffect(null, "review:unbound");
        }

        var workId = boundWork;
        var work = await db.Works.Where(w => w.WorkId == workId)
            .Select(w => new { w.Version, w.State }).FirstAsync(ct);

        // E4: a review on a TERMINAL item never reopens it - it forks a
        // successor with the review as input (AppendWorkInputAsync's terminal
        // law), the original keeps its history and just records the decision.
        if (work.State is "completed" or "cancelled" or "failed")
        {
            await AppendReviewDecisionAsync(
                db, provider, workId, work.Version, data, reference, $"{action}:{state}", text, now, ct);
            var forked = await AppendWorkInputAsync(db, provider, workId,
                InputNote(reference, deliveryId, $"{state} review on change request #{reference.Root}: {text}"), now, ct);
            return new ArmEffect(forked, $"work/{forked}:input");
        }

        await AppendReviewDecisionAsync(
            db, provider, workId, work.Version, data, reference,
            action == "submitted" ? $"submitted:{state}" : action, text, now, ct);
        if (action == "submitted")
        {
            var directive = Trim($"{state} review on change request #{reference.Root}: {text}");
            return new ArmEffect(workId, $"steered:{workId}", (workId, directive, work.Version));
        }

        // changed/dismissed on a live item: the timeline row above is the
        // record; no steer - the verdict did not change.
        return new ArmEffect(workId, $"review:{action}");
    }

    /// <summary>The K4/K6 law, one writer for both review shapes: the kernel
    /// says what a review is worth. A reviewer whose provider account is
    /// linked to a human identity is a HUMAN decision on the item; anyone else
    /// (the contributor's own "checks passed", an unlinked bot) is recorded as
    /// an unverified claim. Either way the accountable person is the work
    /// owner, named on the row (L2).</summary>
    private static async Task AppendReviewDecisionAsync(
        FacilitatorDbContext db, string provider, Guid workId, long workVersion, JsonElement data,
        Reference reference, string action, string? text, DateTime now, CancellationToken ct)
    {
        var _ = ct;
        var reviewer = data.TryGetProperty("actorProviderAccountId", out var acct)
            && acct.ValueKind == JsonValueKind.String ? acct.GetString() ?? string.Empty : string.Empty;
        var humanSubject = await db.ProviderIdentities.AsNoTracking()
            .Where(i => i.Provider == provider && i.AccountId == reviewer)
            .Select(i => $"{i.OidcIssuer}|{i.OidcSubject}").FirstOrDefaultAsync(ct);
        var ownerActor = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => w.OwnerActor).FirstOrDefaultAsync(ct);
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = workVersion,
            Kind = "review.decision",
            Actor = humanSubject is { Length: > 0 } subject ? $"oidc:{subject}" : $"provider:{provider}:{reviewer}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["changeRequest"] = $"{reference.Provider}/{reference.Root}",
                ["action"] = action,
                ["verdict"] = Trim(text ?? string.Empty),
                ["verified"] = humanSubject is { Length: > 0 } ? "human" : "unverified-claim",
                ["accountable"] = ownerActor,
            }),
            CreatedAt = now,
        });
    }

    /// <summary>The PR head branch of a change-request event: the adapter-
    /// normalized top-level <c>headRef</c> — the kernel never digs vendor
    /// payload shapes. Null when absent.</summary>
    internal static string? PayloadHeadRef(JsonElement data)
    {
        return data.TryGetProperty("headRef", out var normalized)
            && normalized.ValueKind == JsonValueKind.String
            && normalized.GetString() is { Length: > 0 } headRef
            ? headRef
            : null;
    }

    /// <summary>Inbound media of a chat event: the adapter-normalized top-level
    /// <c>sourceRefs[]</c> (frozen <c>artifactRef</c> items already stored as
    /// kernel artifacts) — the kernel never inspects item shapes. Null when
    /// absent or empty; carried verbatim into Work source refs.</summary>
    internal static JsonArray? PayloadSourceRefs(JsonElement data)
    {
        return data.TryGetProperty("sourceRefs", out var normalized)
            && normalized.ValueKind == JsonValueKind.Array
            && normalized.GetArrayLength() > 0
            ? JsonNode.Parse(normalized.GetRawText())!.AsArray()
            : null;
    }

    /// <summary>The canonical jsonb text of one message's business identity:
    /// the arm data minus delivery bookkeeping. Key order is jsonb-normalized
    /// in the database, so a semantic match is exact.</summary>
    /// <summary>Ceiling of ledger rows scanned for a business-identity match.</summary>
    private const int DuplicateWindowCandidates = 50;

    internal static string CanonicalizeBody(JsonElement data)
    {
        var node = StripBookkeeping(JsonNode.Parse(data.GetRawText())!.AsObject());
        return node.ToJsonString();
    }

    private static JsonObject StripBookkeeping(JsonObject data)
    {
        data.Remove("providerDeliveryId");
        data.Remove("actorProviderAccountId");
        if (data["reference"]?.AsObject() is { } reference)
        {
            reference.Remove("authorProviderAccountId");
        }

        return data;
    }

    /// <summary>Business identity match: two stored bodies agree once delivery
    /// bookkeeping is stripped (jsonb deep equality, key order irrelevant).</summary>
    internal static bool SameMessage(string storedBody, string canonical)
    {
        try
        {
            // The ledger body IS the arm data document.
            return JsonNode.DeepEquals(JsonNode.Parse(canonical), StripBookkeeping(JsonNode.Parse(storedBody)!.AsObject()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The work a ledger judgment points at ("work/{id}..."), null when it points at none.</summary>
    internal static Guid? JudgmentWorkId(string? judgment) =>
        judgment is { Length: > 5 } && judgment.StartsWith("work/", StringComparison.Ordinal)
        && Guid.TryParse(judgment[5..].Split(':', '/')[0], out var id) ? id : null;

    /// <summary>The jsonb residence document of a reference: where the work
    /// currently lives, as observed on the wire (never inferred).</summary>
    internal static string ActiveWhereOf(Reference reference) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["provider"] = reference.Provider,
            ["channel"] = reference.Channel,
            ["root"] = reference.Root,
            ["kind"] = reference.Kind,
        }.ToJsonString();

    private async Task<ArmEffect> PipelineArmAsync(
        FacilitatorDbContext db, JsonElement data, Reference reference, string deliveryId, DateTime now, CancellationToken ct)
    {
        var action = data.GetProperty("action").GetString() ?? string.Empty;
        var description = data.TryGetProperty("description", out var d) ? d.GetString() : null;
        var binding = await db.CommunicationBindings.FirstOrDefaultAsync(b =>
            b.Provider == reference.Provider && b.Channel == reference.Channel && b.RootMessageId == reference.Root, ct);
        if (binding?.WorkId is not { } workId)
        {
            return new ArmEffect(null, "observe:unbound");
        }

        // Pipeline verdict lands in the progress text: the note carries the
        // verdict token verbatim ("failure" etc.).
        var note = $"provider input ({reference.Provider} status {deliveryId}): {action}" +
                   (description is { Length: > 0 } ? $" — {description}" : string.Empty);
        workId = await AppendWorkInputAsync(db, reference.Provider, workId, note, now, ct);
        return new ArmEffect(workId, $"work/{workId}:input");
    }

    // Shared Work-creation block (provider arms): project find-or-create +
    // Work + created event + binding + interim assign, all in the arm's open
    // transaction.
    private async Task<Guid> CreateWorkFromProviderAsync(
        FacilitatorDbContext db, Facilitator.Kernel.Shared.Auth.LinkPrincipal workload, ProviderOptions options, Reference reference, string objective, string deliveryId, string progressNote, AssignRule? rule, DateTime now, CancellationToken ct, string? wireType = null, string? ownerActor = null)
    {
        // A rule-matched lane may name the OWNING team (the endpoint's
        // fleet): the manifest ledger team-scopes a work's artifacts, so
        // the work lives on the coding team or its worker could never
        // record a manifest. Unrostered team ⇒ the workload's own team
        // (fail-open: a provider config typo must not drop the Work).
        var teamId = workload.TeamId;
        if (rule?.Team is { Length: > 0 } teamSlug
            && await db.Teams.AsNoTracking().Where(t => t.Slug == teamSlug)
                .Select(t => t.TeamId).FirstOrDefaultAsync(ct) is { } rostered && rostered != default)
        {
            // Fail-open: an unrostered team keeps the workload's own team —
            // a provider config typo must not drop the Work.
            teamId = rostered;
        }
        var workId = Guid.NewGuid();
        var project = await db.Projects.FirstOrDefaultAsync(
            p => p.TeamId == teamId && p.Name == ProviderProjectName, ct);
        if (project is null)
        {
            project = new ProjectEntity
            {
                ProjectId = Guid.NewGuid(),
                TeamId = teamId,
                Name = ProviderProjectName,
                Config = "{}",
                CreatedAt = now,
            };
            db.Projects.Add(project);
        }


        db.Works.Add(new WorkEntity
        {
            WorkId = workId,
            ProjectId = project.ProjectId,
            Objective = objective,
            State = "open",
            Version = 1,
            TeamId = teamId,
            Origin = wireType,
            OwnerActor = ownerActor,
            ActiveWhere = ActiveWhereOf(reference),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = 1,
            Kind = "created",
            Actor = $"link:{reference.Provider}:{options.BotUserId ?? "provider"}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["objective"] = objective }),
            CreatedAt = now,
        });
        AppendProgressRow(db, options, workId, progressNote, now);
        db.CommunicationBindings.Add(new CommunicationBindingEntity
        {
            BindingId = Guid.NewGuid(),
            Provider = reference.Provider,
            Channel = reference.Channel,
            RootMessageId = reference.Root,
            ParentMessageId = reference.MessageId,
            WorkId = workId,
            IdempotencyKey = deliveryId,
            CreatedAt = now,
        });
        AppendRoutingIntentForWork(db, options, workId, objective, reference, rule, teamId, now);
        return workId;
    }

    /// <summary>Arm outcome: the Work it touched (inbox stamp), the judgment
    /// recorded on the provider_events ledger, and an optional deferred steer
    /// (work, directive, expected version) the caller applies post-commit via
    /// ICoordinationStore — review steers cannot join the arm's tx.</summary>
    internal sealed record ArmEffect(
        Guid? Work,
        string Judgment,
        (Guid WorkId, string Directive, long ExpectedVersion)? Steer = null,
        Func<IServiceProvider, Task>? PostCommit = null);

    // ---- helpers ------------------------------------------------------------

    private static string InputNote(Reference reference, string deliveryId, string? text, JsonArray? sourceRefs = null)
    {
        var note = text is { Length: > 0 }
            ? $"provider input ({reference.Provider} post {reference.MessageId ?? deliveryId}): {Trim(text)}"
            : $"provider input ({reference.Provider} post {reference.MessageId ?? deliveryId})";
        if (sourceRefs is { Count: > 0 })
        {
            // One media line per ref, built from the frozen artifactRef item's
            // mediaType/artifactId only — no vendor shapes.
            foreach (var item in sourceRefs)
            {
                var mediaType = item?["mediaType"]?.GetValue<string>() ?? "application/octet-stream";
                var artifactId = item?["artifactId"]?.GetValue<string>() ?? "unknown";
                note += $"\nmedia: {mediaType}@{artifactId}";
            }
        }

        return note;
    }

    private Task MintChallengeAsync(FacilitatorDbContext db, ProviderOptions options, Reference reference, string actor, DateTime now, CancellationToken ct)
    {
        var _ = ct;
        var code = RandomCode();
        db.ProviderLinkChallenges.Add(new ProviderLinkChallengeEntity
        {
            ChallengeId = Guid.NewGuid(),
            CodeSha256 = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant(),
            Provider = reference.Provider,
            Tenant = reference.Tenant,
            AccountId = actor,
            DeliverToAccountId = actor,
            CreatedAt = now,
            ExpiresAt = now.Add(ChallengeTtl),
        });
        AppendCommand(db, ReplyEnvelope(
            DmTarget(reference.Provider, actor),
            $"Your Facilitator link code: {code} (single use, {ChallengeTtl.TotalMinutes:0} minutes). Redeem it via POST /api/link-identities/redeem.",
            $"link:{reference.MessageId ?? actor}:dm",
            $"provider/{reference.Provider}/{reference.MessageId ?? actor}",
            now), options, now, workId: null);
        return Task.CompletedTask;
    }

    private Task AckAsync(FacilitatorDbContext db, ProviderOptions options, Reference reference, string deliveryId, string replyVsReaction, DateTime now, CancellationToken ct)
    {
        var _ = ct;
        var envelope = replyVsReaction == "reply"
            ? ReplyEnvelope(TargetOf(reference), "On it.", $"mention:{deliveryId}:ack", $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now)
            : ReactEnvelope(TargetOf(reference), AckEmoji, $"mention:{deliveryId}:ack", $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}", now);
        AppendCommand(db, envelope, options, now, workId: null);
        return Task.CompletedTask;
    }

    private async Task<Guid> AppendWorkInputAsync(FacilitatorDbContext db, string provider, Guid workId, string note, DateTime now, CancellationToken ct, JsonArray? sourceRefs = null)
    {
        // Terminal means terminal (A7): input arriving on a finished work
        // never reopens it. It forks a successor work, links it, moves the
        // conversation binding onto it, and says so in the thread - the
        // follow-up is a new item with its own history, not a resurrected one.
        workId = await ForkTerminalAsync(db, provider, workId, note, now, ct);
        var version = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstOrDefaultAsync(ct);
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version,
            Kind = "provider.input",
            Actor = $"provider:{provider}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["note"] = note }),
            CreatedAt = now,
        });
        if (sourceRefs is { Count: > 0 })
        {
            // Auditable media row: the refs ride the timeline, not just the
            // note text, so a later reader dereferences them without parsing
            // the note.
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = version,
                Kind = "provider.attachment",
                Actor = $"provider:{provider}",
                Detail = new JsonObject
                {
                    ["note"] = note,
                    ["sourceRefs"] = (JsonNode)sourceRefs.DeepClone(),
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        AppendProgressRow(db, registry.Find(provider), workId, Trim(note), now);
        return workId;
    }

    /// <summary>The successor fork: a terminal target becomes the predecessor.
    /// Returns the work the input actually lands on (the successor when the
    /// target was terminal, the target otherwise).</summary>
    private async Task<Guid> ForkTerminalAsync(FacilitatorDbContext db, string provider, Guid workId, string note, DateTime now, CancellationToken ct)
    {
        var target = await db.Works.FirstOrDefaultAsync(w => w.WorkId == workId, ct);
        if (target is null || target.State is not ("completed" or "cancelled" or "failed"))
        {
            return workId;
        }

        var successor = new WorkEntity
        {
            WorkId = Guid.NewGuid(),
            ProjectId = target.ProjectId,
            Objective = $"Follow-up to {target.WorkId.ToString("N")[..8]}: {note.Split('\n')[0]}",
            State = "open",
            Version = 1,
            TeamId = target.TeamId,
            OwnerActor = target.OwnerActor,
            Origin = target.Origin,
            ActiveWhere = target.ActiveWhere,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Works.Add(successor);
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = successor.WorkId,
            WorkVersion = 1,
            Kind = "created",
            Actor = $"provider:{provider}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["objective"] = successor.Objective,
                ["successorOf"] = target.WorkId.ToString(),
            }),
            CreatedAt = now,
        });
        db.WorkLinks.Add(new WorkLinkEntity
        {
            LinkId = Guid.NewGuid(),
            WorkId = target.WorkId,
            RefWorkId = successor.WorkId,
            Kind = "successor",
            Note = "input arrived after the work closed",
            CreatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = target.WorkId,
            WorkVersion = target.Version,
            Kind = "work.linked",
            Actor = $"provider:{provider}",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["refWorkId"] = successor.WorkId.ToString(),
                ["kind"] = "successor",
                ["note"] = "input arrived after the work closed",
            }),
            CreatedAt = now,
        });

        // The conversation follows the live work: the binding (and any
        // per-message children) repoint to the successor.
        await db.CommunicationBindings
            .Where(b => b.WorkId == workId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.WorkId, successor.WorkId), ct);

        // Say so, in the place the input came from (name the new item).
        var binding = await db.CommunicationBindings.AsNoTracking().FirstOrDefaultAsync(
            b => b.WorkId == successor.WorkId, ct);
        if (binding is not null && registry.Find(provider) is { EndpointId: { Length: > 0 } })
        {
            var target0 = new ConversationTarget(
                binding.Provider, null, binding.Channel, binding.RootMessageId, binding.ParentMessageId,
                null, "thread", null);
            AppendCommand(db, ReplyEnvelope(target0,
                $"Work {target.WorkId.ToString("N")[..8]} is closed - this continues as work {successor.WorkId.ToString("N")[..8]}.",
                $"successor:{successor.WorkId:N}", $"work/{successor.WorkId}", now),
                registry.Find(provider), now, successor.WorkId);
        }

        return successor.WorkId;
    }

    private void AppendProgressRow(FacilitatorDbContext db, ProviderOptions? options, Guid workId, string note, DateTime now)
    {
        AppendCommand(db, ProviderEnvelopes.Progress(workId, note, now), options, now, workId);
    }

    /// <summary>The provider arm's routing intent: a work-created
    /// event contributes <c>{team, capability, inputs, commandIdBase}</c> to
    /// the funnel instead of addressing an endpoint — the funnel resolves the
    /// candidate (rule/global <c>AssignEndpointId</c> survives as an optional
    /// preference, audited <c>preference-satisfied|preference-ignored</c>).
    /// Label rules win over the global pair when the triggering issue event's
    /// verbatim <c>payload</c> carries the rule's label (label policy lives in
    /// the adapter, the kernel just reads what it set — malleability law); a
    /// rule-matched intent carries the rule's capability and gains
    /// <c>repo</c>/<c>issue</c> inputs (the coding worker's re-hydration
    /// coordinates) and rides the <c>work-assign-coding:</c> command-id base
    /// (the re-dispatch arm reads the original envelope back by it).</summary>
    private void AppendRoutingIntentForWork(FacilitatorDbContext db, ProviderOptions options, Guid workId, string objective, Reference reference, AssignRule? rule, Guid teamId, DateTime now)
    {
        var capabilityId = rule?.CapabilityId is { Length: > 0 } ruleCapability
            ? ruleCapability
            : options.AssignCapabilityId;
        if (capabilityId is not { Length: > 0 })
        {
            return;
        }

        var targetId = rule?.TargetId ?? options.AssignTargetId ?? options.Provider + "-interim";
        var extraInputs = rule is null
            ? null
            : new JsonObject { ["repo"] = reference.Channel, ["issue"] = reference.Root };
        var teamSlug = db.Teams.AsNoTracking().Where(t => t.TeamId == teamId)
            .Select(t => t.Slug).FirstOrDefault();
        var intent = new Facilitator.Kernel.Work.RoutingIntent(
            workId,
            capabilityId,
            teamSlug,
            PreferenceEndpointId: rule?.EndpointId is { Length: > 0 } ruleEndpoint ? ruleEndpoint : options.AssignEndpointId,
            TargetId: targetId,
            CommandIdBase: rule is null ? "work-assign" : "work-assign-coding",
            EffectKey: workId.ToString("N"),
            Objective: objective,
            SourceRefsJson: "[]",
            ExtraInputsJson: extraInputs?.ToJsonString(),
            ActorEvidenceRef: $"kernel:provider-ingest:{workId}");
        db.Outbox.Add(Facilitator.Kernel.Work.RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
    }

    /// <summary>Chat-plane rule match: the issue-arm matcher gated on the
    /// provider's <see cref="ProviderOptions.ChatLabelRouting"/> opt-in — the
    /// chat Work-creation seams call only this, so a forge provider (flag
    /// false) keeps a byte-identical code path. Null when the flag is off or
    /// no rule matches.</summary>
    internal static AssignRule? MatchChatLabelRule(ProviderOptions options, JsonElement? triggerEvent)
        => options.ChatLabelRouting ? MatchAssignRule(options, triggerEvent) : null;

    /// <summary>First assign rule whose label appears (case-insensitive) in the
    /// event payload's label set, else null.</summary>
    internal static AssignRule? MatchAssignRule(ProviderOptions options, JsonElement? triggerEvent)
    {
        if (options.AssignRules.Count == 0 || triggerEvent is not { } data)
        {
            return null;
        }

        var labels = PayloadLabels(data);
        if (labels.Count == 0)
        {
            return null;
        }

        return options.AssignRules.FirstOrDefault(rule =>
            rule.Label is { Length: > 0 } label && labels.Contains(label.ToLowerInvariant()));
    }

    /// <summary>Label set of a provider event: the adapter-normalized
    /// top-level <c>labels[]</c> — the kernel never digs vendor payload
    /// shapes. Lowercased set.</summary>
    internal static HashSet<string> PayloadLabels(JsonElement data)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("labels", out var normalized) && normalized.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in normalized.EnumerateArray())
            {
                if (label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } labelName)
                {
                    labels.Add(labelName);
                }
            }
        }

        return labels;
    }

    private void AppendCommand(
        FacilitatorDbContext db,
        JsonObject envelope,
        ProviderOptions? options,
        DateTime now,
        Guid? workId,
        string? recipient = null,
        string? commandId = null)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            // Kernel-emitted envelopes must be contract-valid; fail closed at
            // insert rather than poison the outbox.
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = commandId ?? envelope["data"]?["idempotencyKey"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            RecipientEndpointId = recipient ?? $"link.{(options ?? throw new InvalidOperationException("AppendCommand needs the owning provider for default addressing")).EndpointId}",
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    /// <summary>The custom command row's payload (admin_config
    /// <c>commands</c>), null when absent — merged over the built-ins by
    /// every slash-plane entry point.</summary>
    internal static async Task<JsonElement?> CommandsPayloadAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "commands")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is not { Length: > 0 } text)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Queue one async decode: an internal-channel outbox row
    /// (recipient <c>decode.kernel</c> — like <c>kernel.log</c>, an internal
    /// payload with no wire schema, so it bypasses the command-envelope
    /// validator; every row the sink emits back IS validator-gated). The row
    /// is internal-channel, not wire-validated: it may carry fields no schema
    /// knows about. The deterministic <c>decode:&lt;deliveryId&gt;</c> command
    /// id makes a crash-replayed arm idempotent on the outbox unique
    /// constraint. <c>labels</c>/<c>sourceRefs</c> ride along so the sink's
    /// Work creation routes by chat labels and attaches inbound media exactly
    /// like the synchronous arms.</summary>
    private static void AppendDecodeRow(
        FacilitatorDbContext db, string text, Reference reference, string actor,
        string deliveryId, Guid teamId, string replyVsReaction, DateTime now, JsonElement? triggerEvent = null)
    {
        JsonArray labels = new(), sourceRefs = new();
        if (triggerEvent is { } data)
        {
            if (data.TryGetProperty("labels", out var labelsNode) && labelsNode.ValueKind == JsonValueKind.Array)
            {
                labels = (JsonArray)JsonNode.Parse(labelsNode.GetRawText())!;
            }

            if (PayloadSourceRefs(data) is { } refs)
            {
                sourceRefs = refs;
            }
        }

        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = "urn:facilitator:kernel",
            ["type"] = DecodeSink.RequestType,
            ["subject"] = $"provider/{reference.Provider}/{reference.MessageId ?? deliveryId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["data"] = new JsonObject
            {
                ["text"] = text,
                ["reference"] = TargetNode(TargetOf(reference)),
                ["actorProviderAccountId"] = actor,
                ["providerDeliveryId"] = deliveryId,
                ["teamId"] = teamId.ToString(),
                ["replyVsReaction"] = replyVsReaction,
                ["workId"] = null,
                ["labels"] = labels,
                ["sourceRefs"] = sourceRefs,
            },
        };
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = $"decode:{deliveryId}",
            RecipientEndpointId = "decode.kernel",
            AggregateType = "work",
            AggregateId = null,
            EventType = DecodeSink.RequestType,
            Envelope = envelope.ToJsonString(),
            WorkId = null,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    private static string RandomCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        var chars = new char[6];
        for (var i = 0; i < 6; i++)
        {
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        }

        return new string(chars);
    }

    private static string Trim(string text) =>
        text.Length <= 2000 ? text : text[..2000];

    public sealed record Reference(string Provider, string Tenant, string Channel, string Root, string? MessageId, string Kind);

    /// <summary>The system-side loop budget (B6, N2), consumed in the
    /// provider-ingress arms: the <c>loop-budget</c> admin_config row caps
    /// how many replies the facilitator itself posts into one conversation
    /// per window - the echo-law ledger rows ARE that count (no second
    /// state). Over the cap the conversation arm STOPS (judgment
    /// <c>loop-budget:stop</c>): two bots answering each other go silent at
    /// the cap instead of looping, and the judgment on the ledger row makes
    /// the silence auditable. No row, or a state-change event (load-bearing,
    /// echo-exempt): never gated. The window itself is the human reset: a
    /// quiet period clears the exchange count.</summary>
    internal async Task<string?> LoopBudgetSpentAsync(
        FacilitatorDbContext db, ProviderOptions options, Reference reference, DateTime now, CancellationToken ct)
    {
        if (options.BotUserId is not { Length: > 0 })
        {
            return null;
        }

        var policy = await db.AdminConfig.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Resource == "loop-budget", ct);
        if (policy is null)
        {
            return null;
        }

        int max;
        int windowMinutes;
        try
        {
            using var doc = JsonDocument.Parse(policy.Payload);
            max = doc.RootElement.TryGetProperty("maxExchangesPerConversation", out var m) ? m.GetInt32() : 8;
            windowMinutes = doc.RootElement.TryGetProperty("windowMinutes", out var w) ? w.GetInt32() : 30;
        }
        catch (JsonException)
        {
            return null;
        }

        var cutoff = now.AddMinutes(-windowMinutes);
        // Conversation-scoped through the body jsonb (cast to text - PG has
        // no LIKE on jsonb): a plain provider+window count would let one
        // busy channel mute another.
        var ownPosts = await db.ProviderEvents
            .FromSql($"""
             SELECT * FROM facilitator.provider_events
             WHERE provider = {reference.Provider} AND received_at >= {cutoff}
               AND verified_reference = 'echo:post-ledger'
               AND body -> 'reference' ->> 'channelOrResourceId' = {reference.Channel}
             """)
            .CountAsync(ct);
        return ownPosts >= max ? reference.Provider : null;
    }

    internal static Reference ReferenceOf(JsonElement data, string provider)
    {
        var reference = data.GetProperty("reference");
        var channel = reference.GetProperty("channelOrResourceId").GetString() ?? string.Empty;
        var kind = reference.TryGetProperty("conversationKind", out var k) ? k.GetString() ?? "thread" : "thread";
        var root = reference.TryGetProperty("rootThreadId", out var r) ? r.GetString() : null;
        var messageId = reference.TryGetProperty("messageId", out var m) ? m.GetString() : null;
        var tenant = reference.TryGetProperty("tenantOrOrganizationId", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        return new Reference(provider, tenant, channel, root ?? messageId ?? channel, messageId, kind);
    }

    internal static ConversationTarget TargetOf(Reference reference) => new(
        reference.Provider, reference.Tenant, reference.Channel, reference.Root, reference.MessageId, null, reference.Kind, null);

    internal static ConversationTarget DmTarget(string provider, string accountId) => new(
        provider, null, null, null, null, null, "dm", accountId);

    internal static JsonObject ReplyEnvelope(ConversationTarget target, string text, string idempotencyKey, string subject, DateTime now) =>
        CommunicationEnvelope("org.facilitator.command.communication.reply.v1", "command.communication.reply.v1.json", target, subject, now, idempotencyKey,
            new JsonObject { ["text"] = text });

    internal static JsonObject ReactEnvelope(ConversationTarget target, string emoji, string idempotencyKey, string subject, DateTime now) =>
        CommunicationEnvelope("org.facilitator.command.communication.react.v1", "command.communication.react.v1.json", target, subject, now, idempotencyKey,
            new JsonObject { ["emojiName"] = emoji });

    internal static JsonObject CommunicationEnvelope(string type, string schemaFile, ConversationTarget target, string subject, DateTime now, string idempotencyKey, JsonObject dataTail)
    {
        var data = new JsonObject
        {
            ["commandId"] = idempotencyKey,
            ["target"] = TargetNode(target),
            ["idempotencyKey"] = idempotencyKey,
        };
        foreach (var property in dataTail.AsObject())
        {
            data[property.Key] = property.Value?.DeepClone();
        }

        return new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = "urn:facilitator:kernel",
            ["type"] = type,
            ["subject"] = subject,
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = $"https://schemas.openfacilitator.org/v1/{schemaFile}",
            ["data"] = data,
        };
    }

    internal static JsonObject TargetNode(ConversationTarget target)
    {
        var node = new JsonObject { ["provider"] = target.Provider, ["conversationKind"] = target.ConversationKind };
        if (target.Tenant is { Length: > 0 } tenant)
        {
            node["tenantOrOrganizationId"] = tenant;
        }

        if (target.Channel is { Length: > 0 } channel)
        {
            node["channelOrResourceId"] = channel;
        }

        if (target.RootThreadId is { Length: > 0 } root)
        {
            node["rootThreadId"] = root;
        }

        if (target.ParentMessageId is { Length: > 0 } parent)
        {
            node["parentMessageId"] = parent;
        }

        if (target.SubjectRef is { Length: > 0 } subjectRef)
        {
            node["subjectRef"] = subjectRef;
        }

        if (target.ActorProviderAccountId is { Length: > 0 } account)
        {
            node["actorProviderAccountId"] = account;
        }

        return node;
    }
}
