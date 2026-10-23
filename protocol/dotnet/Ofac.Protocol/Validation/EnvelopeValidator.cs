using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

namespace Ofac.Protocol.Validation;

/// <summary>
/// Outcome of envelope validation. <paramref name="ErrorClass"/> is one of
/// <c>envelope-violation</c>, <c>schema-violation</c>, <c>payload-too-large</c>,
/// <c>unknown-command</c> — the same taxonomy as the Plan 01 fixture index.
/// </summary>
/// <param name="Ok">True when the payload is contract-valid.</param>
/// <param name="ErrorClass">Error class when <paramref name="Ok"/> is false.</param>
/// <param name="Message">Human-readable diagnostics.</param>
public sealed record EnvelopeValidationResult(bool Ok, string? ErrorClass, string? Message);

/// <summary>
/// Shared .NET envelope validator: CloudEvents envelope schema (by class from
/// <c>type</c>), then <c>data</c> against the advertised <c>dataschema</c>,
/// then the wire-byte size gate. Semantics mirror <c>RunCase</c> in
/// <c>protocol/tools/Validate.Fixtures/Program.cs</c> and
/// <c>validateEnvelope</c> in <c>src/protocol/src/validate.ts</c>: a failure
/// that only materialises under format enforcement is a
/// <c>schema-violation</c>; unknown <c>dataschema</c> ids and commands outside
/// <see cref="KnownCommands"/> fail closed.
/// </summary>
public sealed class EnvelopeValidator
{
    /// <summary>Wire-size limit (fixture index <c>limits.maxEnvelopeBytes</c>).</summary>
    public const int MaxEnvelopeBytes = 262144;

    private const string CommandPrefix = "org.facilitator.command.";
    private const string EventPrefix = "org.facilitator.event.";

    private static readonly string SchemaBaseUri = "https://schemas.openfacilitator.org/v1/";

    private readonly Dictionary<string, JsonSchema> _schemas;
    private readonly JsonSchema _envelopeCommand;
    private readonly JsonSchema _envelopeEvent;
    private readonly EvaluationOptions _strict = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
    private readonly EvaluationOptions _structural = new() { OutputFormat = OutputFormat.List };

    private EnvelopeValidator(Dictionary<string, JsonSchema> schemas)
    {
        _schemas = schemas;
        _envelopeCommand = Schema("envelope-command.json");
        _envelopeEvent = Schema("envelope-event.json");
    }

    private static readonly Lazy<Dictionary<string, JsonSchema>> SharedSchemas = new(BuildSchemas);

    /// <summary>
    /// Build a validator from the schema set embedded in this assembly
    /// (logical name = on-disk filename, identical for json-schema/ and
    /// cloudevents-schemas/ so <c>dataschema</c> resolves by the same
    /// filename map as the fixture harness's <c>schemaById</c>). Idempotent:
    /// global-schema registration runs exactly once per process, so multiple
    /// hosts in one process (tests, future in-proc replicas) are safe.
    /// </summary>
    public static EnvelopeValidator CreateDefault() => new(SharedSchemas.Value);

    private static Dictionary<string, JsonSchema> BuildSchemas()
    {
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var name in typeof(EnvelopeValidator).Assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = typeof(EnvelopeValidator).Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var id = JsonNode.Parse(text)?["$id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var schema = JsonSchema.FromText(text);
            schemas[id] = schema;
            SchemaRegistry.Global.Register(schema);
        }

        return schemas;
    }

    /// <summary>
    /// Validate a raw JSON payload; the UTF-8 byte count of the string is the
    /// wire size for the <c>payload-too-large</c> gate.
    /// </summary>
    public EnvelopeValidationResult Validate(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new EnvelopeValidationResult(false, "envelope-violation", $"payload is not valid JSON: {ex.Message}");
        }

        return Validate(root, Encoding.UTF8.GetByteCount(json));
    }

    /// <summary>
    /// Validate an already-parsed envelope with an explicit wire byte count
    /// (used by the kernel to self-check generated outbox envelopes).
    /// </summary>
    public EnvelopeValidationResult Validate(JsonElement root, int wireByteLength)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new EnvelopeValidationResult(false, "envelope-violation", "envelope must be a JSON object");
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is null)
        {
            return new EnvelopeValidationResult(false, "envelope-violation", "missing 'type'");
        }

        JsonSchema envelopeSchema;
        bool isCommand;
        if (type.StartsWith(CommandPrefix, StringComparison.Ordinal))
        {
            envelopeSchema = _envelopeCommand;
            isCommand = true;
        }
        else if (type.StartsWith(EventPrefix, StringComparison.Ordinal))
        {
            envelopeSchema = _envelopeEvent;
            isCommand = false;
        }
        else
        {
            return new EnvelopeValidationResult(false, "envelope-violation", $"unclassifiable envelope type '{type}'");
        }

        if (!envelopeSchema.Evaluate(root, _strict).IsValid)
        {
            var structural = envelopeSchema.Evaluate(root, _structural).IsValid;
            return structural
                ? new EnvelopeValidationResult(false, "schema-violation", $"envelope format violation for type '{type}'")
                : new EnvelopeValidationResult(false, "envelope-violation", $"envelope structural violation for type '{type}'");
        }

        if (isCommand && !KnownCommands.All.Contains(type))
        {
            return new EnvelopeValidationResult(false, "unknown-command", $"command type '{type}' is not in the v1 contract");
        }

        var dataschema = root.GetProperty("dataschema").GetString()!;
        var schemaKey = dataschema.StartsWith(SchemaBaseUri, StringComparison.Ordinal) ? dataschema : SchemaBaseUri + dataschema;
        if (!_schemas.TryGetValue(dataschema, out var dataSchema) && !_schemas.TryGetValue(schemaKey, out dataSchema))
        {
            return new EnvelopeValidationResult(false, "schema-violation", $"unadvertised dataschema '{dataschema}'");
        }

        if (!root.TryGetProperty("data", out var data) || !dataSchema.Evaluate(data, _strict).IsValid)
        {
            return new EnvelopeValidationResult(false, "schema-violation", $"data does not conform to '{dataschema}'");
        }

        if (wireByteLength > MaxEnvelopeBytes)
        {
            return new EnvelopeValidationResult(false, "payload-too-large", $"{wireByteLength} bytes exceeds the {MaxEnvelopeBytes} byte envelope limit");
        }

        return new EnvelopeValidationResult(true, null, null);
    }

    private JsonSchema Schema(string fileName) =>
        _schemas.TryGetValue(SchemaBaseUri + fileName, out var s)
            ? s
            : throw new InvalidOperationException($"embedded schema '{fileName}' is missing (available: {string.Join(", ", _schemas.Keys.OrderBy(k => k, StringComparer.Ordinal))})");
}
