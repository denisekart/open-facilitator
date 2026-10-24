using System.Text.Json;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests;

/// <summary>
/// Unit matrix (no DB): every control/receipt conformance fixture against the
/// embedded-schema validator the link server actually enforces — valid frames
/// pass, invalid frames fail with errorClass schema-violation, and the v9-only
/// hello is schema-VALID (rejection is negotiation, not validation).
/// </summary>
public sealed class ControlFrameValidatorTests
{
    private static readonly ControlFrameValidator Validator = ControlFrameValidator.CreateDefault();

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

    private static JsonElement Parse(string fixture) =>
        JsonDocument.Parse(PostgresFixture.ReadFixture(fixture)).RootElement;
}
