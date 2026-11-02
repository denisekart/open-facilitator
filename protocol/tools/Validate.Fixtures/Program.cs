// The .NET conformance harness; also the kernel inbox's fixture source.
// Validates every fixture listed in <fixturesDir>/index.json against the
// normative schemas in protocol/json-schema + protocol/cloudevents-schemas.
// Semantics mirror src/protocol/test/fixtures.test.ts (vitest side); the
// shared canonical golden cross-checks the two RFC 8785 implementations.
//
// Usage: dotnet run --project protocol/tools/Validate.Fixtures -- [fixturesDir]
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Facilitator.Kernel.Shared;

using Jcs.Net;

using Json.Schema;

var fixturesDir = args.Length > 0 ? args[0] : "protocol/conformance-fixtures";
var schemaDirs = new[] { "protocol/json-schema", "protocol/cloudevents-schemas" };

// Clone the generated Converter.Settings (internal — enum/length converters
// are registered there) and match the JS fixture pipeline: JSON.stringify
// escaping, no pretty-printing, nulls omitted.
var generatedSettings = (JsonSerializerOptions)typeof(Ofac.Protocol.EnvelopeCommand)
    .Assembly.GetType("Ofac.Protocol.Converter")!
    .GetField("Settings")!.GetValue(null)!;
var wireOptions = new JsonSerializerOptions(generatedSettings)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// ---- load + compile schemas (registry keyed by $id; no HTTP fetch) ----
var schemas = new Dictionary<string, JsonSchema>();
var registryOptions = new EvaluationOptions { OutputFormat = OutputFormat.List };
foreach (var dir in schemaDirs)
{
    foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
    {
        var text = File.ReadAllText(file);
        var id = JsonNode.Parse(text)!["$id"]!.GetValue<string>();
        schemas[id] = JsonSchema.FromText(text);
    }
}
// $refs between schema files resolve through the registry by $id.
foreach (var s in schemas.Values)
{
    SchemaRegistry.Global.Register(s);
}

var strict = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

JsonSchema Schema(string id) =>
    schemas.TryGetValue(id, out var s) ? s : throw new InvalidOperationException($"missing schema {id}");

// Fail-closed command table derived from schema filenames (mirrors
// KNOWN_COMMAND_TYPES in @ofac/protocol).
var knownCommands = schemas.Keys
    .Where(id => id.Contains("/command."))
    .Select(id => $"org.facilitator.{Path.GetFileName(id)[..^".json".Length]}")
    .ToHashSet(StringComparer.Ordinal);

var envelopeCommand = Schema("https://schemas.openfacilitator.org/v1/envelope-command.json");
var envelopeEvent = Schema("https://schemas.openfacilitator.org/v1/envelope-event.json");
var receiptSchema = Schema("https://schemas.openfacilitator.org/v1/receipt.v1.json");
var controlSchema = Schema("https://schemas.openfacilitator.org/v1/control.v1.json");

// ---- index ----
var indexPath = Path.Combine(fixturesDir, "index.json");
using var indexDoc = JsonDocument.Parse(File.ReadAllText(indexPath));
var maxBytes = indexDoc.RootElement.GetProperty("limits").GetProperty("maxEnvelopeBytes").GetInt32();
var cases = indexDoc.RootElement.GetProperty("cases").EnumerateArray().ToList();

var failed = 0;
foreach (var c in cases)
{
    var file = c.GetProperty("file").GetString()!;
    var expect = c.GetProperty("expect").GetString()!;
    var errorClass = c.TryGetProperty("errorClass", out var ec) ? ec.GetString() : null;
    var schemaFile = c.TryGetProperty("schemaFile", out var sf) ? sf.GetString() : null;
    var observed = RunCase(Path.Combine(fixturesDir, file), c.GetProperty("class").GetString()!, schemaFile);

    var pass = expect == "valid" ? observed == null : observed == errorClass;
    if (pass && c.TryGetProperty("receipt", out var responseFixture))
    {
        // Behavior fixtures name an expected response (unsupported receipt /
        // goodbye frame) that must itself be a valid receipt/control frame.
        var rpath = Path.Combine(fixturesDir, responseFixture.GetString()!);
        var rtext = File.ReadAllText(rpath);
        var rvalid = responseFixture.GetString()!.Contains("/receipt.")
            ? receiptSchema.Evaluate(JsonDocument.Parse(rtext).RootElement, strict).IsValid
            : controlSchema.Evaluate(JsonDocument.Parse(rtext).RootElement, strict).IsValid;
        pass = rvalid;
    }
    if (pass && expect == "valid" && file.EndsWith(".json", StringComparison.Ordinal) && IsEnvelopeCase(c))
    {
        pass = DtoRoundTrip(Path.Combine(fixturesDir, file));
    }

    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {file} {expect}/{errorClass ?? string.Empty}" +
                      (observed is not null && !pass ? $" (observed: {observed})" : string.Empty));
    if (!pass) failed++;
}

// ---- canonicalization golden (RFC 8785 cross-check) ----
{
    var goldenPath = Path.Combine(fixturesDir, "canonical", "approval-prehash.sha256");
    var payload = File.ReadAllText(Path.Combine(fixturesDir, "canonical", "approval-prehash.json"));
    var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonCanonicalizer.CanonicalizeToUtf8(payload))).ToLowerInvariant();
    var golden = File.ReadAllText(goldenPath).Trim();
    var pass = digest == golden;
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] canonical/approval-prehash.json valid/schema-violation");
    if (!pass)
    {
        Console.WriteLine($"  digest {digest} != golden {golden}");
        failed++;
    }
}

// ---- approval-record golden (record digest via the kernel helper) ----
{
    var recordGolden = File.ReadAllText(Path.Combine(fixturesDir, "canonical", "approval-record-1.sha256")).Trim();
    using var recordDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixturesDir, "canonical", "approval-record-1.json")));
    var record = recordDoc.RootElement;
    var actors = record.GetProperty("eligibleActors").EnumerateArray().Select(x => x.GetString()!).ToArray();
    var expiresAt = DateTime.Parse(record.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    var createdAt = DateTime.Parse(record.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    var digest = ApprovalDigest.Compute(record.GetProperty("approvalId").GetString()!, record.GetProperty("action"), actors, expiresAt, createdAt);

    // Property twin: action keys reversed + strings NFD-encoded — the digest
    // must not depend on source key order or unicode spelling.
    static JsonNode Flip(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject()
            .Reverse()
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name.Normalize(NormalizationForm.FormD), Flip(p.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Flip).ToArray()),
        JsonValueKind.String => JsonValue.Create(element.GetString()!.Normalize(NormalizationForm.FormD)),
        _ => JsonNode.Parse(element.GetRawText())!,
    };

    using var flippedDoc = JsonDocument.Parse(Flip(record.GetProperty("action")).ToJsonString());
    var reordered = ApprovalDigest.Compute(record.GetProperty("approvalId").GetString()!, flippedDoc.RootElement, actors, expiresAt, createdAt);
    var recordPass = digest == recordGolden && reordered == recordGolden;
    Console.WriteLine($"[{(recordPass ? "PASS" : "FAIL")}] canonical/approval-record-1.json valid/schema-violation");
    if (!recordPass)
    {
        Console.WriteLine($"  digest {digest} / reordered {reordered} != golden {recordGolden}");
        failed++;
    }
}

// ---- result-manifest golden (RFC 8785 manifest digest + canonical-bytes law) ----
{
    var manifestGolden = File.ReadAllText(Path.Combine(fixturesDir, "canonical", "result-manifest-1.sha256")).Trim();
    var manifestRaw = File.ReadAllText(Path.Combine(fixturesDir, "canonical", "result-manifest-1.json"));
    using var manifestDoc = JsonDocument.Parse(manifestRaw);
    var manifestDigest = ApprovalDigest.CanonicalDocument(manifestDoc.RootElement);
    // The kernel rejects non-canonical submissions: the stored golden must BE
    // the canonical form, byte-for-byte (UTF-8, no trailing newline).
    var canonicalBytes = ApprovalDigest.CanonicalBytes(manifestDoc.RootElement);
    var bytesMatch = canonicalBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(manifestRaw));
    var manifestPass = manifestDigest == manifestGolden && bytesMatch;
    Console.WriteLine($"[{(manifestPass ? "PASS" : "FAIL")}] canonical/result-manifest-1.json valid/schema-violation");
    if (!manifestPass)
    {
        Console.WriteLine($"  digest {manifestDigest} != golden {manifestGolden}; canonical-bytes match: {bytesMatch}");
        failed++;
    }
}

Console.WriteLine(failed == 0 ? $"ALL {cases.Count} FIXTURE CASES PASS" : $"{failed} FAILURE(S)");
return failed == 0 ? 0 : 1;

// Returns the observed error class, or null when the payload is valid.
string? RunCase(string path, string messageClass, string? schemaFile = null)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;

    // Non-envelope wire objects addressed by schema filename (enroll/delivery
    // fixture classes): validate against the by-$id registry, strict. Adapter
    // lifecycle frames ride the same law — one frame, a $def profile of the
    // shipped 06 child wire (protocol/json-schema/adapter.v1.json).
    if (messageClass is "enroll" or "delivery" or "adapter")
    {
        return Schema($"https://schemas.openfacilitator.org/v1/{schemaFile}")
            .Evaluate(root, strict).IsValid
            ? null
            : "schema-violation";
    }

    if (messageClass is "receipt")
    {
        return receiptSchema.Evaluate(root, strict).IsValid ? null : "schema-violation";
    }
    if (messageClass is "control")
    {
        if (!controlSchema.Evaluate(root, strict).IsValid)
        {
            return "schema-violation";
        }

        // Version negotiation is observable on hello frames.
        if (root.GetProperty("kind").GetString() == "hello")
        {
            var offered = root.GetProperty("protocolVersions").EnumerateArray()
                .Select(v => v.GetString()!).ToHashSet();
            if (!offered.Contains("v1"))
            {
                return "no-common-version";
            }
        }

        return null;
    }

    // Envelopes (command/event): structural envelope check first. A failure
    // that only materialises under format enforcement is a payload/schema
    // violation (mirrors the ajv `classify` helper on the TS side).
    var envelopeSchema = messageClass == "command" ? envelopeCommand : envelopeEvent;
    if (!envelopeSchema.Evaluate(root, strict).IsValid)
    {
        var structural = envelopeSchema.Evaluate(root, registryOptions).IsValid;
        return structural ? "schema-violation" : "envelope-violation";
    }

    var type = root.GetProperty("type").GetString()!;
    if (messageClass == "command" && !knownCommands.Contains(type))
    {
        return "unknown-command";
    }

    var dataschema = root.GetProperty("dataschema").GetString()!;
    if (!schemas.TryGetValue(dataschema, out var dataSchema))
    {
        return "schema-violation"; // fail closed on unadvertised schemas
    }

    if (!dataSchema.Evaluate(root.GetProperty("data"), strict).IsValid)
    {
        return "schema-violation";
    }

    if (WireBytes(root) > maxBytes)
    {
        return "payload-too-large";
    }

    return null;
}

int WireBytes(JsonElement root) =>
    JsonSerializer.SerializeToUtf8Bytes(root, wireOptions).Length;

// Generated-DTO round-trip: deserialize with the committed Ofac.Protocol DTOs,
// re-serialize, compare DOMs. Proves the DTOs reproduce the wire form
// property-for-property (wire casing is verbatim, camelCase policy OFF).
bool DtoRoundTrip(string path)
{
    var text = File.ReadAllText(path);
    using var doc = JsonDocument.Parse(text);
    var type = doc.RootElement.GetProperty("type").GetString()!;
    var envelopeSchema = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal)
        ? typeof(Ofac.Protocol.EnvelopeCommand)
        : typeof(Ofac.Protocol.EnvelopeEvent);
    try
    {
        var dto = JsonSerializer.Deserialize(text, envelopeSchema, wireOptions);
        if (dto is null)
        {
            return false;
        }

        var reserialized = JsonSerializer.SerializeToNode(dto, envelopeSchema, wireOptions)!;
        return JsonNode.DeepEquals(JsonNode.Parse(text), reserialized);
    }
    catch (Exception e)
    {
        Console.WriteLine($"  dto round-trip failed for {path}: {e.GetType().Name}: {e.Message}");
        return false;
    }
}

static bool IsEnvelopeCase(JsonElement c) =>
    c.GetProperty("class").GetString() is "command" or "event";
