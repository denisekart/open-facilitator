using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;
namespace Facilitator.Tests.Results;

/// <summary>
/// Shared builders for the result-manifest suites: canonical manifest documents
/// (bytes serialized THROUGH the kernel canonicalizer, digest computed via
/// <see cref="ApprovalDigest"/>) and completed-event envelopes rewritten per
/// test (fresh envelope id — unique across the shared container).
/// </summary>
internal static class ResultManifestTestKit
{
    /// <summary>Factory settings adding the mode-(b) binding on a manifest-suite endpoint id.</summary>
    internal static readonly IReadOnlyDictionary<string, string?> WorkloadBinding =
        new Dictionary<string, string?>
        {
            ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
            ["Link:WorkloadIdentities:2:EndpointId"] = "man.wk1-fs",
            ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
            ["Link:WorkloadIdentities:2:Role"] = "child",
            ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "true",
        };

    internal static readonly byte[] ReportBytes =
        "# research report\nmanifest output pin proved here\n"u8.ToArray();

    /// <summary>Canonical manifest bytes + the digest law computes for them.</summary>
    internal static (byte[] Bytes, string Digest) ManifestBytes(JsonNode document)
    {
        using var parsed = JsonDocument.Parse(document.ToJsonString());
        return (ApprovalDigest.CanonicalBytes(parsed.RootElement), ApprovalDigest.CanonicalDocument(parsed.RootElement));
    }

    /// <summary>A minimal manifest document; <paramref name="outputs"/> entries ride verbatim.</summary>
    internal static JsonObject ManifestDoc(string manifestId, JsonArray? outputs = null, JsonObject? structuredResult = null, string? revisionOf = null)
    {
        var doc = new JsonObject
        {
            ["manifestId"] = manifestId,
            ["outputs"] = outputs ?? [],
            ["notes"] = "plan 15 vertical",
            ["createdAt"] = "2026-10-27T00:00:00Z",
        };
        if (structuredResult is not null)
        {
            doc["structuredResult"] = structuredResult;
        }

        if (revisionOf is not null)
        {
            doc["revisionOf"] = revisionOf;
        }

        return doc;
    }

    /// <summary>One schema-valid outputs[] entry (artifactRef shape).</summary>
    internal static JsonObject Output(Guid artifactId, string digest, long size = 1024) => new()
    {
        ["artifactId"] = artifactId.ToString(),
        ["mediaType"] = "application/octet-stream",
        ["size"] = size,
        ["digest"] = digest,
        ["storageUrl"] = $"external:blob/{artifactId}",
        ["createdBy"] = "plan15",
        ["createdAt"] = "2026-10-27T00:00:00Z",
    };

    /// <summary>The frozen completed-with-ref fixture rewritten: fresh ids, ref + digest pointed at one artifact.</summary>
    internal static string CompletedEnvelope(
        Guid workId, Guid assignmentId, string manifestId, Guid manifestArtifactId, string resultDigest)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.completed.with-manifest-ref.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["subject"] = $"work/{workId}/assignment/{assignmentId}";
        var data = envelope["data"]!.AsObject();
        data["workId"] = workId.ToString();
        data["assignmentId"] = assignmentId.ToString();
        data["executionId"] = "exec-" + manifestId;
        data["resultManifest"]!.AsObject()["manifestId"] = manifestId;
        data["resultManifestRef"] = $"artifact://{workId}/{assignmentId}/{manifestArtifactId}";
        data["resultDigest"] = resultDigest;
        return envelope.ToJsonString();
    }

    /// <summary>The legacy inline-only completed fixture rewritten with a fresh envelope id.</summary>
    internal static string LegacyCompletedEnvelope()
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.completed.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        return envelope.ToJsonString();
    }

    /// <summary>Upload manifest bytes through the link plane: request_upload ⇒ ack ticket ⇒ PUT (auto-confirm).</summary>
    internal static async Task<Guid> UploadManifestAsync(
        HttpClient link, Guid? workId, byte[] bytes)
    {
        var commandId = "cmd-man-" + Guid.NewGuid().ToString("N");
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            Facilitator.Tests.Artifacts.ArtifactTestKit.RequestUploadEnvelope(
                commandId, bytes, await Facilitator.Tests.Artifacts.ArtifactTestKit.AliceEvidenceAsync(), workId),
            HttpStatusCode.Accepted);
        var data = ack.GetProperty("data");
        var artifactId = Guid.Parse(data.GetProperty("artifactId").GetString()!);
        var put = await Facilitator.Tests.Artifacts.ArtifactTestKit.PutBytesAsync(
            link, data.GetProperty("uploadLocation").GetString()!, bytes);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return artifactId;
    }

    internal static string ProblemType(HttpResponseMessage response) =>
        JsonDocument.Parse(response.Content.ReadAsStringAsync().Result)
            .RootElement.GetProperty("type").GetString()!;
}
