using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;


namespace Facilitator.Tests.Artifacts;

/// <summary>
/// Shared builders for the artifact suites: the frozen request-upload fixture
/// rewritten per test (fresh envelope id + commandId — unique across the
/// shared container), mode-(b) workload enrollment on a test-owned binding,
/// and the byte/digest math the assertions ride on.
/// </summary>
internal static class ArtifactTestKit
{
    /// <summary>Factory settings adding a test-only mode-(b) binding: the gateway-alpha client,
    /// endpoint <c>art.wk1-fs</c>, team-alpha, accepting delegated claims. Endpoint ids carry a
    /// leg tag: both matrix legs share one database, and a re-enroll of the same endpoint id
    /// fences the other leg\u2019s live token.</summary>
    internal static readonly IReadOnlyDictionary<string, string?> WorkloadBinding =
        new Dictionary<string, string?>
        {
            ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
            ["Link:WorkloadIdentities:2:EndpointId"] = "art.wk1-fs",
            ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
            ["Link:WorkloadIdentities:2:Role"] = "child",
            ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "true",
        };

    /// <summary>The mode-(b) binding plus the s3 provider pointed at the shared SeaweedFS
    /// container (every s3 matrix leg). A computed property, NOT a field: evaluating it
    /// boots the container, so it must only fire on an s3 leg's factory construction.</summary>
    internal static IReadOnlyDictionary<string, string?> S3Settings => new Dictionary<string, string?>
    {
        ["Link:WorkloadIdentities:2:ClientId"] = "ofac-gateway-alpha",
        ["Link:WorkloadIdentities:2:EndpointId"] = "art.wk1-s3",
        ["Link:WorkloadIdentities:2:Team"] = "team-alpha",
        ["Link:WorkloadIdentities:2:Role"] = "child",
        ["Link:WorkloadIdentities:2:AcceptsDelegatedClaims"] = "true",
        ["Storage:Provider"] = "s3",
        ["Storage:S3:Endpoint"] = SeaweedFsFixture.Endpoint,
        ["Storage:S3:Bucket"] = SeaweedFsFixture.Bucket,
        ["Storage:S3:Region"] = "us-east-1",
        ["Storage:S3:AccessKey"] = SeaweedFsFixture.AccessKey,
        ["Storage:S3:SecretKey"] = SeaweedFsFixture.SecretKey,
        ["Storage:S3:ForcePathStyle"] = "true",
    };

    internal static readonly byte[] ReportBytes =
        "# research report\nkernel-boundary digest law proved here\n"u8.ToArray();

    internal static string DigestOf(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Mode-(b) enrollment (OIDC client-credentials token bound to the test workload identity).</summary>
    internal static async Task<string> EnrollModeBAsync(HttpClient kernel, string endpointId, string clientId, string secret)
    {
        var cc = await KeycloakFixture.TokenForClientAsync(clientId, secret);
        var body = JsonSerializer.Serialize(new { token = cc, endpointId, instanceId = "inst-1", trustMode = "isolated-executor" });
        var json = await KernelOps.PostJsonAsync(kernel, "/api/link/v1/enroll", body, HttpStatusCode.Created);
        return json.GetProperty("linkToken").GetString()!;
    }

    /// <summary>alice-minted evidence token (delegation claim present on the ofac-evidence client).</summary>
    internal static async Task<string> AliceEvidenceAsync() =>
        "oidc:" + await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");

    /// <summary>The frozen request-upload fixture rewritten: fresh ids, declared bytes, optional work binding.</summary>
    internal static string RequestUploadEnvelope(
        string commandId, byte[] bytes, string evidenceRef, Guid? workId = null, long? declaredSize = null)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/command.artifact.request-upload.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["commandId"] = commandId;
        data["digest"] = DigestOf(bytes);
        data["size"] = declaredSize ?? bytes.LongLength;
        data["actorEvidenceRef"] = evidenceRef;
        if (workId is { } w)
        {
            data["workId"] = w.ToString();
        }
        else
        {
            data.AsObject().Remove("workId");
        }

        data.AsObject().Remove("assignmentId");
        return envelope.ToJsonString();
    }

    /// <summary>The frozen artifact.available fixture rewritten for one wire artifactId + digest (optional foreign URL / work).</summary>
    internal static string AvailableEnvelope(string wireArtifactId, string digest, string? storageUrl = null, Guid? workId = null, long size = 10240)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.artifact.available.json"))!;
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = envelope["data"]!.AsObject();
        data["artifactId"] = wireArtifactId;
        data["digest"] = digest;
        data["size"] = size;
        if (storageUrl is not null)
        {
            data["storageUrl"] = storageUrl;
        }

        if (workId is { } w)
        {
            data["workId"] = w.ToString();
        }
        else
        {
            data.AsObject().Remove("workId");
        }

        return envelope.ToJsonString();
    }

    internal static async Task<HttpClient> UserClientAsync(KernelTestFactory factory, string user)
    {
        // Auto-redirect OFF: the content route's 302 target is the object
        // store, and the factory client would re-dispatch it against the
        // TestServer instead of letting the test see the redirect.
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await KeycloakFixture.TokenForUserAsync(user, user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    internal static async Task<HttpResponseMessage> PutBytesAsync(
        HttpClient client, string path, byte[] bytes) =>
        await client.PutAsync(path, new ByteArrayContent(bytes));

    /// <summary>A valid digest nothing has ever been uploaded under (fresh random
    /// bytes): allocations that must NOT collide on the shared content-addressed
    /// key (one bucket across the whole s3 matrix) declare one.</summary>
    internal static string FreshDigest() => DigestOf(Guid.NewGuid().ToByteArray());

    /// <summary>GET a content route, following a 302 to the store over the REAL
    /// network (the factory client auto-redirects into the TestServer, which
    /// does not speak S3 paths).</summary>
    internal static async Task<HttpResponseMessage> GetContentAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is { } location)
        {
            var target = location.ToString();
            response.Dispose();
            using var network = new HttpClient();
            return await network.GetAsync(target);
        }

        return response;
    }

    internal static string ProblemType(HttpResponseMessage response) =>
        JsonDocument.Parse(response.Content.ReadAsStringAsync().Result)
            .RootElement.GetProperty("type").GetString()!;

    internal static string B64(Guid value) => value.ToString("N");
}
