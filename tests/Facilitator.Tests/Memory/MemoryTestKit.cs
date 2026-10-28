using System.Text.Json;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Memory;

/// <summary>
/// Shared builders for the memory suites: the frozen retain/recall/candidate/used
/// fixtures rewritten per test (fresh envelope ids + commandIds — unique across
/// the shared container), and the mode-(b) workload binding the link-plane legs
/// enroll under.
/// </summary>
internal static class MemoryTestKit
{
    /// <summary>Factory settings adding a mode-(b) team-alpha workload on a memory-suite endpoint id.</summary>
    internal static readonly IReadOnlyDictionary<string, string?> WorkloadBinding =
        new Dictionary<string, string?>
        {
            ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
            ["Link:WorkloadIdentities:2:EndpointId"] = "mem.wk1",
            ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
            ["Link:WorkloadIdentities:2:Role"] = "child",
            ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "true",
        };

    /// <summary>The broker's content-hash law (sha256 hex, prefixed).</summary>
    internal static string Sha256Of(string content) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>The frozen retain fixture rewritten: fresh ids, selector, content.</summary>
    internal static string RetainEnvelope(
        string commandId, string scopeSelector, string content, string evidenceRef)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/command.memory.retain.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["commandId"] = commandId;
        data["scopeSelector"] = scopeSelector;
        data["content"] = content;
        data["actorEvidenceRef"] = evidenceRef;
        data.AsObject().Remove("contentSummary");
        // The fixture's provenance workId is a wire token, not a live Work;
        // tests that bind a real Work re-add it.
        data["provenance"]!.AsObject().Remove("workId");
        return envelope.ToJsonString();
    }

    /// <summary>The frozen recall fixture rewritten for one query (selector hint optional).</summary>
    internal static string RecallEnvelope(string commandId, string query, string? scopeSelector = null)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/command.memory.recall.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["commandId"] = commandId;
        data["query"] = query;
        data.AsObject().Remove("scopeSelectors");
        data.AsObject().Remove("workId");
        data.AsObject().Remove("maxItems");
        data.AsObject().Remove("maxTokens");
        data.AsObject().Remove("includeSuperseded");
        if (scopeSelector is not null)
        {
            data["scopeSelectors"] = new JsonArray(scopeSelector);
        }

        return envelope.ToJsonString();
    }

    /// <summary>The frozen candidate fixture rewritten: scope, summary, provenance (optional live workId + subject).</summary>
    internal static string CandidateEnvelope(
        string scope, string contentSummary, string? workId = null, string? policyLabel = null)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.memory.candidate.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["scope"] = scope;
        data["contentSummary"] = contentSummary;
        if (workId is not null)
        {
            // The frozen schema's provenance is four fields, so the live Work
            // rides the envelope subject (`work/<id>/...`) — the arm's law.
            envelope["subject"] = $"work/{workId}/assignment/memory-1";
        }
        else
        {
            envelope["subject"] = $"memory/{Guid.NewGuid():N}";
        }

        if (policyLabel is null)
        {
            data.AsObject().Remove("policyLabel");
        }

        return envelope.ToJsonString();
    }

    /// <summary>The frozen used-attestation fixture rewritten for one memoryId.</summary>
    internal static string UsedEnvelope(string memoryId, string consumer = "workspace:ws-01")
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.memory.used.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["data"]!["memoryId"] = memoryId;
        envelope["data"]!["consumer"] = consumer;
        return envelope.ToJsonString();
    }

    /// <summary>A REST retain body (the kernel parses it manually, same field law as the command).</summary>
    internal static string RetainBody(string scopeSelector, string content, string type = "observed_fact") =>
        new JsonObject
        {
            ["scopeSelector"] = scopeSelector,
            ["type"] = type,
            ["content"] = content,
            ["provenance"] = new JsonObject
            {
                ["source"] = "workspace:rest-test",
                ["evidenceRef"] = "identity:evidence/rest-1",
                ["createdBy"] = "oidc:test|human",
                ["createdAt"] = "2026-10-28T10:00:00Z",
            },
        }.ToJsonString();

    /// <summary>A REST recall body for one query.</summary>
    internal static string RecallBody(string query, bool includeSuperseded = false) =>
        new JsonObject
        {
            ["query"] = query,
            ["purpose"] = "assignment.context",
            ["includeSuperseded"] = includeSuperseded,
        }.ToJsonString();

    internal static string ProblemType(HttpResponseMessage response) =>
        JsonDocument.Parse(response.Content.ReadAsStringAsync().Result)
            .RootElement.GetProperty("type").GetString()!;
}
