using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;

namespace Facilitator.Tests.Approvals;

/// <summary>
/// Plan 11 law D2: the kernel digest of the appendix §7.2 approval record must
/// equal the committed cross-language golden regardless of source key order or
/// unicode spelling (the TS mirror runs the same golden in the protocol suite,
/// and the .NET fixture harness compiles the same helper).
/// </summary>
public sealed class ApprovalDigestTests
{
    private static string Golden(string name) => File.ReadAllText(
        Path.Combine(PostgresFixture.FixtureRoot(), "canonical", name)).Trim();

    [Fact]
    public void GoldenRecordDigestsToTheCommittedValue()
    {
        var (id, action, actors, expiresAt, createdAt) = GoldenRecord("approval-record-1.json");
        Assert.Equal(Golden("approval-record-1.sha256"), ApprovalDigest.Compute(id, action, actors, expiresAt, createdAt));
    }

    [Fact]
    public void DigestIgnoresActionKeyOrderAndNfdSpelling()
    {
        var (id, action, actors, expiresAt, createdAt) = GoldenRecord("approval-record-1.json");
        var baseline = ApprovalDigest.Compute(id, action, actors, expiresAt, createdAt);

        // Same action, every object's keys reversed + every string re-spelled
        // in NFD (decomposed accents): the §7.1 law (NFC + sorted keys) makes
        // the canonical form — hence the digest — identical.
        var flipped = JsonDocument.Parse(Reverse(action).ToJsonString()).RootElement;
        Assert.Equal(baseline, ApprovalDigest.Compute(id, flipped, actors, expiresAt, createdAt));
    }

    /// <summary>
    /// Plan 12: the SECOND golden carries the exact action shape the Gateway
    /// records (capabilityId/resource/assignmentId/workId) — the kernel must
    /// recompute the Gateway's declared digest byte-for-byte.
    /// </summary>
    [Fact]
    public void GatewayShapedGoldenRecordDigestsToTheCommittedValue()
    {
        var (id, action, actors, expiresAt, createdAt) = GoldenRecord("approval-record-2.json");
        Assert.Equal(Golden("approval-record-2.sha256"), ApprovalDigest.Compute(id, action, actors, expiresAt, createdAt));
    }

    private static JsonNode Reverse(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject()
            .Reverse()
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, Reverse(p.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Reverse).ToArray()),
        JsonValueKind.String => JsonValue.Create(element.GetString()!.Normalize(NormalizationForm.FormD)),
        _ => JsonNode.Parse(element.GetRawText())!,
    };

    private static (string Id, JsonElement Action, string[] Actors, DateTime ExpiresAt, DateTime CreatedAt) GoldenRecord(string file)
    {
        var text = PostgresFixture.ReadFixture(Path.Combine("canonical", file));
        var root = JsonDocument.Parse(text).RootElement;
        return (
            root.GetProperty("approvalId").GetString()!,
            root.GetProperty("action"),
            root.GetProperty("eligibleActors").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            DateTime.Parse(root.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(root.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
    }
}
