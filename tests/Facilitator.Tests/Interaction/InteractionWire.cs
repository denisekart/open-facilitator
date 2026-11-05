using System.Text;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Interaction;

/// <summary>
/// Wire builders for the interactive leg: the @ofac/protocol fixtures
/// <c>valid/event.assignment.input.required.json</c> and
/// <c>valid/command.assignment.input.json</c>, re-keyed per test (fresh
/// envelope ids + a per-test requestId, so the globally-unique outbox
/// command ids <c>input-fanout:&lt;requestId&gt;</c> /
/// <c>input:&lt;requestId&gt;</c> never collide across classes).
/// </summary>
internal static class InteractionWire
{
    /// <summary>A fresh requestId in the SDK's mint shape ("inp-" + hex).</summary>
    internal static string NewRequestId() => "inp-" + Guid.NewGuid().ToString("N");

    /// <summary>The input-required event envelope over the fixture.</summary>
    internal static string InputRequiredEnvelope(
        Guid workId, Guid assignmentId, string requestId, int generation,
        string question, string[]? choices = null)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.input.required.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["workId"] = workId.ToString();
        data["assignmentId"] = assignmentId.ToString();
        data["requestId"] = requestId;
        data["executionGeneration"] = generation;
        data["question"] = question;
        if (choices is null)
        {
            data.Remove("choices");
        }
        else
        {
            var array = new JsonArray();
            foreach (var choice in choices)
            {
                array.Add(JsonValue.Create(choice));
            }

            data["choices"] = array;
        }

        return doc.ToJsonString();
    }

    /// <summary>The answer command envelope over the fixture (wire respond plane).</summary>
    internal static string InputCommandEnvelope(
        string requestId, string answer, string commandId, string actorEvidenceRef)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/command.assignment.input.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["commandId"] = commandId;
        data["requestId"] = requestId;
        data["answer"] = answer;
        data["actorEvidenceRef"] = actorEvidenceRef;
        return doc.ToJsonString();
    }

    internal static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");
}
