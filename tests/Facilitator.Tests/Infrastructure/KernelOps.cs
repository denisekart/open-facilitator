using System.Net;
using System.Text;
using System.Text.Json;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Infrastructure;

/// <summary>Shared HTTP + polling helpers for the kernel suites.</summary>
internal static class KernelOps
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>POST JSON and assert the status, returning the parsed body.</summary>
    internal static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string payload, HttpStatusCode expected)
    {
        using var response = await client.PostAsync(path, new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await JsonWire.ReadJsonAsync(response.Content);
        Assert.True(expected == response.StatusCode, $"{(int)expected} expected for POST {path}, got {(int)response.StatusCode}: {body.GetRawText()}");
        return body;
    }

    /// <summary>GET and assert the status, returning the parsed body.</summary>
    internal static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        var body = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal(expected, response.StatusCode);
        return body;
    }

    /// <summary>Create a Work via the API; returns (workId, version).</summary>
    internal static async Task<(Guid WorkId, long Version)> CreateWorkAsync(HttpClient client, string objective)
    {
        var created = await PostJsonAsync(client, "/api/works", JsonSerializer.Serialize(new { objective }), HttpStatusCode.Created);
        return (created.GetProperty("workId").GetGuid(), created.GetProperty("version").GetInt64());
    }

    /// <summary>POST /transitions with an explicit fence.</summary>
    internal static async Task<JsonElement> TransitionAsync(HttpClient client, Guid workId, string to, long expectedVersion, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var payload = JsonSerializer.Serialize(new { to, expectedVersion });
        return await PostJsonAsync(client, $"/api/works/{workId}/transitions", payload, expected);
    }

    /// <summary>Walk a fresh work open→planning→active→review→completed (4 transitions, +1 version each).</summary>
    internal static async Task WalkToCompletedAsync(HttpClient client, Guid workId, long currentVersion)
    {
        foreach (var to in new[] { "planning", "active", "review", "completed" })
        {
            currentVersion++;
            var result = await TransitionAsync(client, workId, to, currentVersion - 1);
            Assert.Equal(to, result.GetProperty("state").GetString());
            Assert.Equal(currentVersion, result.GetProperty("version").GetInt64());
        }
    }

    /// <summary>Poll a predicate until true or the timeout elapses.</summary>
    internal static async Task PollUntilAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"condition not met within {timeoutMs} ms: {because}");
    }

    /// <summary>Every kernel-emitted outbox envelope must pass the shared validator.</summary>
    internal static void AssertEnvelopesValid(IEnumerable<string> envelopes)
    {
        var validator = EnvelopeValidator.CreateDefault();
        foreach (var envelope in envelopes)
        {
            var verdict = validator.Validate(envelope);
            Assert.True(verdict.Ok, $"outbox envelope rejected: {verdict.ErrorClass} {verdict.Message}");
        }
    }
}
