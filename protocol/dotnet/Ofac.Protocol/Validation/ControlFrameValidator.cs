using System.Text;
using System.Text.Json;

using Json.Schema;

namespace Ofac.Protocol.Validation;

/// <summary>
/// Outcome of control/receipt frame validation. <paramref name="ErrorClass"/>
/// is <c>schema-violation</c> (frame violates its frozen schema) — the same
/// taxonomy the Plan 01 fixture index uses.
/// </summary>
/// <param name="Ok">True when the frame is contract-valid.</param>
/// <param name="ErrorClass">Error class when <paramref name="Ok"/> is false.</param>
/// <param name="Message">Human-readable diagnostics.</param>
public sealed record FrameValidationResult(bool Ok, string? ErrorClass, string? Message);

/// <summary>
/// Link-server frame validator: control frames against the embedded
/// <c>control.v1.json</c> (hello/welcome/heartbeat/flow_control/goodbye) and
/// receipts against <c>receipt.v1.json</c>. Schema loading mirrors
/// <see cref="EnvelopeValidator"/>: draft-2020-12 documents embedded as
/// resources of this assembly, registered globally so cross-file
/// <c>$ref</c>s resolve. Format enforcement is on (an RFC3339-shaped
/// <c>processedAt</c> that is not a real instant is a violation).
/// </summary>
public sealed class ControlFrameValidator
{
    private readonly JsonSchema _control;
    private readonly JsonSchema _receipt;
    private readonly JsonSchema _capabilityManifest;
    private readonly EvaluationOptions _strict = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

    private ControlFrameValidator(JsonSchema control, JsonSchema receipt, JsonSchema capabilityManifest)
    {
        _control = control;
        _receipt = receipt;
        _capabilityManifest = capabilityManifest;
    }

    private static readonly Lazy<ControlFrameValidator> Shared = new(Build);

    /// <summary>
    /// Validator over the schema set embedded in this assembly. Idempotent
    /// global registration (same pattern as <see cref="EnvelopeValidator.CreateDefault"/>),
    /// so several hosts in one process are safe.
    /// </summary>
    public static ControlFrameValidator CreateDefault() => Shared.Value;

    private static ControlFrameValidator Build()
    {
        // EnvelopeValidator owns the single idempotent global-registration
        // pass over the embedded schema set (FromText auto-registers by $id
        // and refuses to overwrite). Force it, then resolve the instances —
        // building our own here would double-register common.v1.json et al.
        EnvelopeValidator.CreateDefault();
        static JsonSchema Load(string file) =>
            SchemaRegistry.Global.Get(new Uri($"https://schemas.openfacilitator.org/v1/{file}")) is JsonSchema schema
                ? schema
                : throw new InvalidOperationException($"embedded schema '{file}' is missing");
        return new ControlFrameValidator(Load("control.v1.json"), Load("receipt.v1.json"), Load("capability-manifest.v1.json"));
    }

    /// <summary>Validate a control frame (hello/welcome/heartbeat/flow_control/goodbye).</summary>
    public FrameValidationResult ValidateControlFrame(JsonElement frame) =>
        Evaluate(_control, frame, "control frame");

    /// <summary>Validate a receipt frame (full frame, deliveryId included).</summary>
    public FrameValidationResult ValidateReceipt(JsonElement frame) =>
        Evaluate(_receipt, frame, "receipt");

    /// <summary>Validate a CapabilityManifestV1 document.</summary>
    public FrameValidationResult ValidateCapabilityManifest(JsonElement manifest) =>
        Evaluate(_capabilityManifest, manifest, "capability manifest");

    private FrameValidationResult Evaluate(JsonSchema schema, JsonElement frame, string what)
    {
        if (frame.ValueKind != JsonValueKind.Object)
        {
            return new FrameValidationResult(false, "schema-violation", $"{what} must be a JSON object");
        }

        var result = schema.Evaluate(frame, _strict);
        if (result.IsValid)
        {
            return new FrameValidationResult(true, null, null);
        }

        return new FrameValidationResult(false, "schema-violation", $"{what} violates its schema: {Describe(result)}");
    }

    private static string Describe(EvaluationResults node)
    {
        if (node.Errors is { Count: > 0 } errors)
        {
            return $"{node.SchemaLocation} @ {node.EvaluationPath}: " +
                   string.Join("; ", errors.Select(e => $"{e.Key} {e.Value}"));
        }

        // Failed leaf nodes with neither errors nor details (e.g. a violated
        // const inside if/then): fall back to the path alone.
        return node.Details is { Count: > 0 } details
            ? string.Join(" | ", details.Select(Describe))
            : $"{node.SchemaLocation} @ {node.EvaluationPath}: failed";
    }
}
