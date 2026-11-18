using System.Text.Json;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Protocol;

/// <summary>
/// Unit matrix (no DB): every control/receipt conformance fixture against the
/// embedded-schema validator the link server actually enforces — valid frames
/// pass, invalid frames fail with errorClass schema-violation, and the v9-only
/// hello is schema-VALID (rejection is negotiation, not validation). Plus the
/// edge cases the fixtures do not carry: non-object frames, the conditional
/// receipt rules per status, the control-frame numeric and enum laws, and the
/// capability-manifest shape.
/// </summary>
public sealed class ControlFrameValidatorTests
{
    private static readonly ControlFrameValidator Validator = ConformanceFixtures.Frames;

    [Theory]
    [InlineData("valid/control.hello.json")]
    [InlineData("valid/control.welcome.json")]
    [InlineData("valid/control.heartbeat.json")]
    [InlineData("valid/control.flow_control.json")]
    [InlineData("valid/control.goodbye.json")]
    [InlineData("valid/control.goodbye.incompatible.json")]
    [InlineData("invalid/control.hello.v9-only.json")] // schema-valid; negotiation empties separately
    public void ValidControlFramesPass(string fixture)
    {
        var verdict = Validator.ValidateControlFrame(Parse(fixture));
        Assert.True(verdict.Ok, $"{fixture}: {verdict.Message}");
        Assert.Null(verdict.ErrorClass);
    }

    [Theory]
    [InlineData("valid/receipt.accepted.json")]
    [InlineData("valid/receipt.duplicate.json")]
    [InlineData("valid/receipt.rejected.json")]
    [InlineData("valid/receipt.unsupported.json")]
    public void ValidReceiptFramesPass(string fixture)
    {
        var verdict = Validator.ValidateReceipt(Parse(fixture));
        Assert.True(verdict.Ok, $"{fixture}: {verdict.Message}");
    }

    [Theory]
    [InlineData("invalid/receipt.bad-status.json")]
    [InlineData("invalid/receipt.unsupported-no-reason.json")]
    [InlineData("invalid/link.receipt.no-processed-at.json")] // accepted without processedAt (conditional)
    public void InvalidReceiptFramesFailWithSchemaViolation(string fixture)
    {
        var verdict = Validator.ValidateReceipt(Parse(fixture));
        Assert.False(verdict.Ok, $"{fixture} must fail");
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("receipt", verdict.Message);
    }

    [Fact]
    public void CapabilityManifestMatrix()
    {
        var good = JsonDocument.Parse("""
            {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default",
             "capabilities":[{"id":"research.analyze","version":1,"execution":"asynchronous"}]}
            """).RootElement;
        Assert.True(Validator.ValidateCapabilityManifest(good).Ok);

        var badTeam = JsonDocument.Parse("""
            {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"",
             "capabilities":[]}
            """).RootElement;
        Assert.False(Validator.ValidateCapabilityManifest(badTeam).Ok);
    }

    /// <summary>Every frame entry point rejects a non-object payload before any
    /// schema runs — the guard a malformed socket message hits first.</summary>
    [Theory]
    [InlineData("[]", "control frame")]
    [InlineData("\"heartbeat\"", "control frame")]
    [InlineData("null", "control frame")]
    [InlineData("[]", "receipt")]
    [InlineData("42", "receipt")]
    [InlineData("[]", "capability manifest")]
    public void NonObjectFramesFailWithTheFrameNamed(string json, string kind)
    {
        using var doc = JsonDocument.Parse(json);
        var verdict = kind switch
        {
            "control frame" => Validator.ValidateControlFrame(doc.RootElement),
            "receipt" => Validator.ValidateReceipt(doc.RootElement),
            _ => Validator.ValidateCapabilityManifest(doc.RootElement),
        };
        Assert.False(verdict.Ok);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Equal($"{kind} must be a JSON object", verdict.Message);
    }

    /// <summary>Receipt law per status: <c>accepted</c>/<c>duplicate</c> carry
    /// <c>processedAt</c>, <c>rejected</c>/<c>unsupported</c> carry
    /// <c>reason</c>. The fixtures cover three of the four pairings; the
    /// <c>duplicate</c> pairing is completed here.</summary>
    [Theory]
    [InlineData("""{"deliveryId":"del-1","status":"duplicate","reason":"replay","processedAt":"2026-10-22T18:01:00Z"}""", true)]
    [InlineData("""{"deliveryId":"del-1","status":"duplicate"}""", false)]
    [InlineData("""{"deliveryId":"del-1","status":"accepted"}""", false)]
    [InlineData("""{"deliveryId":"del-1","status":"rejected"}""", false)]
    [InlineData("""{"deliveryId":"del-1","status":"unsupported","reason":"no capability","processedAt":"2026-10-22T18:01:00Z","executionGeneration":7,"detail":{"why":"policy"}}""", true)]
    [InlineData("""{"deliveryId":"del-1","status":"accepted","processedAt":"2026-10-22T18:01:00Z","executionGeneration":0}""", false)]
    [InlineData("""{"deliveryId":"del-1","status":"accepted","processedAt":"2026-13-22T18:01:00Z"}""", false)]
    [InlineData("""{"deliveryId":"","status":"accepted","processedAt":"2026-10-22T18:01:00Z"}""", false)]
    [InlineData("""{"status":"accepted","processedAt":"2026-10-22T18:01:00Z"}""", false)]
    [InlineData("""{"deliveryId":"del-1","status":"accepted","processedAt":"2026-10-22T18:01:00Z","enqueuedAt":"2026-10-22T18:00:00Z"}""", false)]
    public void ReceiptConditionalRulesAreEnforced(string json, bool expectedOk)
    {
        var verdict = Validator.ValidateReceipt(JsonDocument.Parse(json).RootElement);
        Assert.Equal(expectedOk, verdict.Ok);
        if (!expectedOk)
        {
            Assert.Equal("schema-violation", verdict.ErrorClass);
        }
    }

    /// <summary>A frame that matches two control variants at once (or none)
    /// violates <c>oneOf</c>; a numeric or enum law inside a variant is a plain
    /// schema violation.</summary>
    [Theory]
    [InlineData("""{"kind":"heartbeat","sessionId":"s-1","leaseEpoch":1,"maxInFlight":3}""")] // heartbeat + flow_control shape
    [InlineData("""{"kind":"noop","sessionId":"s-1","leaseEpoch":1}""")] // no variant
    [InlineData("""{"kind":"welcome","sessionId":"s-1","leaseEpoch":1,"maxInFlight":8,"heartbeatSeconds":1,"protocolVersion":"v1"}""")] // heartbeat floor
    [InlineData("""{"kind":"welcome","sessionId":"s-1","leaseEpoch":1,"maxInFlight":8,"heartbeatSeconds":900,"protocolVersion":"v1"}""")] // heartbeat ceiling
    [InlineData("""{"kind":"welcome","sessionId":"s-1","leaseEpoch":1,"maxInFlight":8,"heartbeatSeconds":30,"protocolVersion":"1"}""")] // version pattern
    [InlineData("""{"kind":"hello","protocolVersions":[],"supportedTransports":["signalr"],"endpointId":"e","instanceId":"i","capabilityManifestDigest":"sha256:abababababababababababababababababababababababababababababababab"}""")] // empty version offer
    [InlineData("""{"kind":"hello","protocolVersions":["v1"],"supportedTransports":["carrier-pigeon"],"endpointId":"e","instanceId":"i","capabilityManifestDigest":"sha256:abababababababababababababababababababababababababababababababab"}""")] // unknown transport
    [InlineData("""{"kind":"hello","protocolVersions":["v1"],"supportedTransports":["signalr"],"endpointId":"e","instanceId":"i","capabilityManifestDigest":"sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"}""")] // digest law
    [InlineData("""{"kind":"goodbye","sessionId":"s-1","reason":""}""")] // reason must be non-empty
    [InlineData("""{"kind":"heartbeat","sessionId":"s-1","leaseEpoch":0}""")] // epoch floor
    public void ControlFrameLawsAreEnforced(string json)
    {
        var verdict = Validator.ValidateControlFrame(JsonDocument.Parse(json).RootElement);
        Assert.False(verdict.Ok, json);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("control frame violates its schema", verdict.Message);
    }

    /// <summary>Legal counterparts of the laws above, so a passing case proves
    /// the rejection is the law and not a broken probe.</summary>
    [Theory]
    [InlineData("""{"kind":"flow_control","sessionId":"s-1","maxInFlight":0}""")] // pause: zero in flight is legal
    [InlineData("""{"kind":"welcome","sessionId":"s-1","leaseEpoch":1,"maxInFlight":8,"heartbeatSeconds":5,"protocolVersion":"v2","sessionKeyId":"kid-1","seq":0}""")] // additive 2.0.0 fields
    [InlineData("""{"kind":"goodbye","sessionId":"s-1","reason":"no-compatible-protocol-version"}""")]
    public void LegalControlFramesPass(string json) =>
        Assert.True(Validator.ValidateControlFrame(JsonDocument.Parse(json).RootElement).Ok, json);

    /// <summary>The manifest law is a closed document with a non-empty
    /// capability list: every deviation is a schema violation with a message
    /// naming the document kind.</summary>
    [Theory]
    [InlineData("""{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[]}""")] // minItems
    [InlineData("""{"apiVersion":"facilitator.framework/v2","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"x","version":1,"execution":"synchronous"}]}""")] // apiVersion const
    [InlineData("""{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"x","version":0,"execution":"synchronous"}]}""")] // version floor
    [InlineData("""{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"x","version":1,"execution":"whenever"}]}""")] // execution enum
    [InlineData("""{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"x","version":1,"execution":"synchronous","owner":"me"}]}""")] // closed items
    [InlineData("""{"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default","capabilities":[{"id":"x","version":1,"execution":"synchronous"}],"note":"free text"}""")] // closed document
    [InlineData("""{"apiVersion":"facilitator.framework/v1","ownerTeam":"default","capabilities":[{"id":"x","version":1,"execution":"synchronous"}]}""")] // missing kind
    public void CapabilityManifestLawIsEnforced(string json)
    {
        var verdict = Validator.ValidateCapabilityManifest(JsonDocument.Parse(json).RootElement);
        Assert.False(verdict.Ok, json);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("capability manifest violates its schema", verdict.Message);
    }

    [Fact]
    public void RichManifestPasses()
    {
        var verdict = Validator.ValidateCapabilityManifest(JsonDocument.Parse("""
            {"apiVersion":"facilitator.framework/v1","kind":"CapabilityManifest","ownerTeam":"default",
             "capabilities":[{"id":"research.analyze","version":3,"execution":"asynchronous",
                              "inputSchema":"https://example.test/schemas/analyze.json",
                              "acceptedContexts":["inline-text","artifact-reference"],
                              "outputKinds":["analysis.report"],"outputMediaTypes":["text/markdown"],
                              "supportsSteering":true,"supportsCancellation":true,"supportsNudge":false}]}
            """).RootElement);
        Assert.True(verdict.Ok, verdict.Message);
    }

    /// <summary>Diagnostics carry a schema location, so a rejected frame can be
    /// traced to the clause that rejected it.</summary>
    [Fact]
    public void DiagnosticsPointAtTheRejectingClause()
    {
        var verdict = Validator.ValidateReceipt(JsonDocument.Parse("""{"deliveryId":"del-1","status":"shrug"}""").RootElement);
        Assert.False(verdict.Ok);
        Assert.Contains("schema-violation", verdict.ErrorClass);
        Assert.Contains("@", verdict.Message);
    }

    /// <summary>Construction is idempotent, and the shared instance is the
    /// process-wide one the kernel registers as a singleton.</summary>
    [Fact]
    public void CreateDefaultIsStableAcrossCalls() =>
        Assert.Same(Validator, ControlFrameValidator.CreateDefault());

    private static JsonElement Parse(string fixture) => JsonDocument.Parse(ConformanceFixtures.ReadText(fixture)).RootElement;
}
