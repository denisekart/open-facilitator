using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// The media policy decision cores, unit-first: the manifest <c>media</c>
/// facet parser (every shape problem answers the honest text-only default),
/// the MIME-glob matcher, the outbound selection law (every rejected
/// candidate names a machine reason, never a silent drop; facet, wire
/// ceiling and team policy narrow but never widen) — plus the DB-backed
/// admin-policy readers <see cref="MediaPolicy.RetentionForKindAsync"/> and
/// <see cref="MediaPolicy.ForTeamAsync"/> against the shared container
/// (a mistyped policy row degrades to the narrower default, never throws;
/// the row is purged on the way out, same law as MediaReplyTests).
/// </summary>
[Collection("postgres:work")]
public sealed class MediaReplyPolicyTests(PostgresFixture fixture)
{
    private const string PolicyResource = "media-policy";

    private static string Manifest(string capabilityId, string facetJson) =>
        $$"""
        {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"team-alpha",
         "capabilities":[{"id":"{{capabilityId}}","version":1,"execution":"synchronous",{{facetJson}}}]}
        """;

    // ---- MediaFacet.FromManifest -------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"capabilities\":{}}")] // capabilities not an array
    [InlineData("{\"capabilities\":[]}")] // no descriptor
    [InlineData("""{"capabilities":[{"id":"other.cap"}]}""")] // other capability
    [InlineData("""{"capabilities":[{"id":"cap","media":"text"}]}""")] // facet not an object
    [InlineData("""{"capabilities":[{"id":"cap","media":{"directions":[]}}]}""")] // no directions
    [InlineData("""{"capabilities":[{"id":"cap","media":{"directions":"out"}}]}""")] // directions not an array
    public void MalformedManifestsAnswerTextOnly(string? manifestJson) =>
        Assert.Equal(MediaFacet.TextOnly, MediaFacet.FromManifest(manifestJson, "cap"));

    [Fact]
    public void DeclaredFacetParsesDirectionsWithCapsAndEmptyKinds()
    {
        var facet = MediaFacet.FromManifest(
            Manifest("cap", @"""media"":{""directions"":[""out"",""in""],""kinds"":[""image/*""],""maxCount"":3,""maxBytesPerItem"":4096}"),
            "cap");
        Assert.True(facet.AcceptsOut);
        Assert.Equal(["in", "out"], facet.Directions.OrderBy(d => d, StringComparer.Ordinal));
        Assert.Single(facet.Kinds);
        Assert.Equal(3, facet.MaxCount);
        Assert.Equal(4096, facet.MaxBytesPerItem);
    }

    [Fact]
    public void InboundOnlyFacetDoesNotAcceptOut()
    {
        var facet = MediaFacet.FromManifest(
            Manifest("cap", @"""media"":{""directions"":[""in""],""kinds"":[""image/*""]}"), "cap");
        Assert.False(facet.AcceptsOut);
    }

    [Fact]
    public void NonIntegerCapsReadAsNull()
    {
        var facet = MediaFacet.FromManifest(
            Manifest("cap", @"""media"":{""directions"":[""out""],""kinds"":[],""maxCount"":""three"",""maxBytesPerItem"":1.5}"),
            "cap");
        Assert.Null(facet.MaxCount);
        Assert.Null(facet.MaxBytesPerItem);
    }

    // ---- KindAllowed / KindOf ----------------------------------------------

    [Theory]
    [InlineData("image/png", new[] { "image/*" }, true)]
    [InlineData("image/png", new[] { "IMAGE/PNG" }, true)] // exact is case-insensitive
    [InlineData("application/pdf", new[] { "application/pdf" }, true)]
    [InlineData("image/png", new[] { "application/pdf" }, false)]
    [InlineData("image/png", new string[0], false)] // empty list allows nothing (text-only)
    [InlineData("image/png", new[] { "image/" }, false)] // a bare prefix is NOT a wildcard
    [InlineData("image/", new[] { "image/*" }, false)] // the type must have a subtype
    public void KindGlobLaws(string mediaType, string[] patterns, bool expected) =>
        Assert.Equal(expected, MediaReplyPolicy.KindAllowed(mediaType, patterns));

    [Theory]
    [InlineData("image/png", "result.image")]
    [InlineData("IMAGE/gif", "result.image")]
    [InlineData("application/pdf", "result.report")]
    [InlineData("text/plain", "result.report")]
    public void KindOfTaxonomy(string mediaType, string expected) =>
        Assert.Equal(expected, MediaReplyPolicy.KindOf(mediaType));

    // ---- Select -------------------------------------------------------------

    private static ArtifactEntity Row(string mediaType, long size) => new()
    {
        ArtifactId = Guid.NewGuid(),
        MediaType = mediaType,
        Size = size,
        Digest = "sha256:" + new string('a', 64),
        StorageUri = "kernel-fs:x/y",
        Origin = "link",
        CreatedBy = "seed",
    };

    private static readonly MediaFacet ImageFacet =
        new(["out"], ["image/*"], null, null);

    [Fact]
    public void TextOnlyFacetDegradesEverythingAsKindNotAllowed()
    {
        var (attach, degraded) = MediaReplyPolicy.Select(
            [Row("image/png", 10)], MediaFacet.TextOnly, 1024);
        Assert.Empty(attach);
        Assert.Single(degraded);
        Assert.Equal("kind-not-allowed", degraded[0].Reason);
    }

    [Fact]
    public void FacetWithoutOutDegradesEvenAnAllowedKind()
    {
        var (attach, degraded) = MediaReplyPolicy.Select(
            [Row("image/png", 10)], new MediaFacet(["in"], ["image/*"], null, null), 1024);
        Assert.Empty(attach);
        Assert.Equal("kind-not-allowed", Assert.Single(degraded).Reason);
    }

    [Fact]
    public void OversizedRowDegradesWithSizeCapAndTheRestStillAttach()
    {
        var big = Row("image/png", 2048);
        var small = Row("image/jpeg", 1024); // exactly at the cap attaches
        var (attach, degraded) = MediaReplyPolicy.Select([big, small], ImageFacet, 1024);
        Assert.Equal([small.ArtifactId], attach.Select(a => a.Row.ArtifactId));
        Assert.Equal("size-cap", Assert.Single(degraded).Reason);
    }

    [Fact]
    public void CountCapIsTheNarrowestOfFacetWireCeilingAndPolicy()
    {
        var rows = new[] { Row("image/png", 10), Row("image/png", 10), Row("image/png", 10) };

        // Facet cap 2 narrows the wire ceiling of 8.
        var (facetAttach, facetDegraded) = MediaReplyPolicy.Select(
            rows, new MediaFacet(["out"], ["image/*"], 2, null), 1024);
        Assert.Equal(2, facetAttach.Count);
        Assert.Equal("count-cap", Assert.Single(facetDegraded).Reason);

        // A team policy cap of 1 narrows further, below the facet.
        var (policyAttach, policyDegraded) = MediaReplyPolicy.Select(
            rows, ImageFacet, 1024,
            policy: new MediaPolicy(8 * 1024 * 1024, 1, null, null, 20, false));
        Assert.Single(policyAttach);
        Assert.Equal(2, policyDegraded.Count);
        Assert.All(policyDegraded, d => Assert.Equal("count-cap", d.Reason));
    }

    [Fact]
    public void TeamPolicyAllowAndForbidListsNarrowTheFacet()
    {
        var image = Row("image/png", 10);
        var pdf = Row("application/pdf", 10);

        // allow-list misses the facet-allowed image ⇒ kind-not-allowed.
        var (_, byAllow) = MediaReplyPolicy.Select(
            [image], ImageFacet, 1024,
            policy: new MediaPolicy(8 * 1024 * 1024, 8, ["application/pdf"], null, 20, false));
        Assert.Equal("kind-not-allowed", Assert.Single(byAllow).Reason);

        // forbid-list vetoes it ⇒ kind-forbidden.
        var (_, byForbid) = MediaReplyPolicy.Select(
            [image], ImageFacet, 1024,
            policy: new MediaPolicy(8 * 1024 * 1024, 8, null, ["image/*"], 20, false));
        Assert.Equal("kind-forbidden", Assert.Single(byForbid).Reason);

        // The facet's own kinds still gate: a pdf misses image/*.
        var (attach, _) = MediaReplyPolicy.Select([pdf], ImageFacet, 1024);
        Assert.Empty(attach);
    }

    [Fact]
    public void FacetByteBudgetSpeaksButATeamPolicyStillNarrowsIt()
    {
        var row = Row("image/png", 2048);

        // Declared facet budget governs the per-item cap (over the caller's
        // fallback): 2048 fits under 4096.
        var (facetWide, facetNarrow) = MediaReplyPolicy.Select(
            [row], new MediaFacet(["out"], ["image/*"], null, 4096), 1024);
        Assert.Single(facetWide);
        Assert.Empty(facetNarrow);

        var (tight, degraded) = MediaReplyPolicy.Select(
            [row], new MediaFacet(["out"], ["image/*"], null, 4096), 1024,
            policy: new MediaPolicy(1024, 8, null, null, 20, false));
        Assert.Empty(tight);
        Assert.Equal("size-cap", Assert.Single(degraded).Reason);
    }

    // ---- admin policy readers over the shared container --------------------

    private async Task<FacilitatorDbContext> DbAsync() =>
        await new KernelTestFactory(fixture, noDispatcher: true)
            .Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>()
            .CreateDbContextAsync();

    [Fact]
    public async Task RetentionForKindReadsThePolicyRowAndDegradesHonestly()
    {
        await using var db = await DbAsync();
        var team = "mrp-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedTeam(team);

        try
        {
            // No row (the sibling suites keep this resource's lifecycle
            // honest; the purge below guarantees the absent-row leg at the
            // end): the caller's default rules.
            await fixture.Db.DeleteAdminConfig(PolicyResource);
            Assert.Equal("7d", await MediaPolicy.RetentionForKindAsync(db, "context.dispatch", 7, default));

            await fixture.Db.SeedAdminConfig(PolicyResource,
                """{"defaults":{"retentionDays":{"media.attachment":2,"context.dispatch":0}}}""");
            Assert.Equal("2d", await MediaPolicy.RetentionForKindAsync(db, "media.attachment", 30, default));

            // Unlisted kind ⇒ caller default; zero/out-of-band days ⇒ default.
            Assert.Equal("30d", await MediaPolicy.RetentionForKindAsync(db, "unlisted.kind", 30, default));
            Assert.Equal("7d", await MediaPolicy.RetentionForKindAsync(db, "context.dispatch", 7, default));

            // As-built quirk pinned: a NON-JSON-NUMBER days value escapes
            // the JsonException-only catch as InvalidOperationException
            // (TryGetInt32 on a string throws, it does not return false) —
            // operators get the type error, a malformed JSON document still
            // degrades quietly (covered by the out-of-band legs above).
            await fixture.Db.SeedAdminConfig(PolicyResource,
                """{"defaults":{"retentionDays":{"media.attachment":"2"}}}""");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => MediaPolicy.RetentionForKindAsync(db, "media.attachment", 30, default));
        }
        finally
        {
            await fixture.Db.DeleteAdminConfig(PolicyResource);
        }
    }

    [Fact]
    public async Task ForTeamNarrowsOnlyAndNeverThrowsOnAMistypedRow()
    {
        await using var db = await DbAsync();
        var team = "mrp-t-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedTeam(team);

        try
        {
            await fixture.Db.DeleteAdminConfig(PolicyResource);
            var absent = await MediaPolicy.ForTeamAsync(db, team, null, default);
            Assert.Equal(MediaPolicy.System.MaxBytesPerItem, absent.MaxBytesPerItem);
            Assert.Equal(MediaPolicy.System.MaxCount, absent.MaxCount);
            Assert.Equal(20, absent.HistoryMaxMessages);

            await fixture.Db.SeedAdminConfig(PolicyResource,
                """
                {"defaults":{"maxBytesPerItem":1000,"maxCount":4},
                 "teams":[{"team":"TEAM","maxCount":2,"forbidKinds":["image/*"],
                           "history":{"toHarnessMaxMessages":5}}]}
                """.Replace("TEAM", team));
            var policy = await MediaPolicy.ForTeamAsync(db, team, null, default);
            Assert.Equal(1000, policy.MaxBytesPerItem); // defaults narrow the system cap
            Assert.Equal(2, policy.MaxCount); // the team entry narrows further
            Assert.Equal(["image/*"], policy.ForbidKinds);
            Assert.Null(policy.AllowKinds);
            Assert.Equal(5, policy.HistoryMaxMessages);
            Assert.True(policy.ForwardToForeignTeam); // same-plane call: nothing to withhold

            // A foreign destination without the history.toForeignTeam
            // switch vetoes forwarding.
            var foreign = await MediaPolicy.ForTeamAsync(db, team, "other-" + team, default);
            Assert.False(foreign.ForwardToForeignTeam);

            // A policy-malformed (but jsonb-valid) row behaves like an
            // absent one, never an exception.
            await fixture.Db.SeedAdminConfig(PolicyResource, """{"defaults":5,"teams":{"not":"an array"}}""");
            var malformed = await MediaPolicy.ForTeamAsync(db, team, null, default);
            Assert.Equal(MediaPolicy.System.MaxBytesPerItem, malformed.MaxBytesPerItem);
            Assert.Equal(MediaPolicy.System.MaxCount, malformed.MaxCount);
            Assert.Equal(20, malformed.HistoryMaxMessages);
        }
        finally
        {
            await fixture.Db.DeleteAdminConfig(PolicyResource);
        }
    }
}
