using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Protocol;

/// <summary>
/// Edge cases the shared fixtures cannot express: the fail-closed doors in front
/// of schema evaluation (non-JSON, non-object, missing/unclassifiable
/// <c>type</c>), the classification split between envelope and schema
/// violations, the size gate's boundary, the definition-plane
/// <c>ValidateAgainstSchema</c> path, and the <see cref="KnownCommands"/> table
/// cross-checked against the shipped schemas. No DB, no network — the schemas
/// come from the <c>Ofac.Protocol</c> assembly.
/// </summary>
public sealed class EnvelopeValidatorTests
{
    private const string Assign = "valid/command.work.assign.json";
    private const string Accepted = "valid/event.work.accepted.json";
    private const string WorkflowSchema = "https://schemas.openfacilitator.org/v1/workflow-definition.v1.json";

    private static readonly EnvelopeValidator Validator = ConformanceFixtures.Envelopes;

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"specversion": "1.0",""")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("17")]
    [InlineData("null")]
    public void MalformedOrNonObjectPayloadsAreEnvelopeViolations(string payload)
    {
        var verdict = Validator.Validate(payload);
        Assert.False(verdict.Ok);
        Assert.Equal("envelope-violation", verdict.ErrorClass);
    }

    [Fact]
    public void ArrayRootIsRejectedBeforeAnySchemaRuns()
    {
        using var doc = JsonDocument.Parse("[]");
        var verdict = Validator.Validate(doc.RootElement, 2);
        Assert.Equal("envelope-violation", verdict.ErrorClass);
        Assert.Equal("envelope must be a JSON object", verdict.Message);
    }

    [Fact]
    public void MissingTypeIsAnEnvelopeViolation()
    {
        var verdict = Validate(Mutation(root => root.Remove("type")));
        Assert.Equal("envelope-violation", verdict.ErrorClass);
        Assert.Equal("missing 'type'", verdict.Message);
    }

    [Fact]
    public void TypeOutsideBothPrefixesIsUnclassifiable()
    {
        var verdict = Wire(Mutation(root => root["type"] = "com.example.command.late.v1")).Validate();
        Assert.Equal("envelope-violation", verdict.ErrorClass);
        Assert.Contains("unclassifiable envelope type", verdict.Message);
    }

    /// <summary>Structural failures (const/pattern/required/length) are the
    /// envelope's fault. Both harnesses must classify them the same way — the TS
    /// side does it in its <c>classify</c> helper.</summary>
    [Theory]
    [InlineData("invalid/envelope.specversion.json")]
    [InlineData("invalid/envelope.missing-subject.json")]
    public void StructuralEnvelopeFailuresAreClassifiedAsEnvelopeViolations(string file)
    {
        var verdict = Validator.Validate(ConformanceFixtures.Read(file), 512);
        Assert.Equal("envelope-violation", verdict.ErrorClass);
        Assert.Contains("structural violation", verdict.Message);
    }

    /// <summary>An id beyond the 128-char identity law is structural, not a
    /// format problem: the envelope is malformed, whatever the payload says.</summary>
    [Fact]
    public void OverlongIdIsAnEnvelopeViolation()
    {
        var verdict = Wire(Mutation(root => root["id"] = new string('x', 129))).Validate();
        Assert.Equal("envelope-violation", verdict.ErrorClass);
    }

    [Theory]
    [InlineData("invalid/event.bad-time.json")]
    public void FormatOnlyFailuresAreClassifiedAsSchemaViolations(string file)
    {
        var verdict = Validator.Validate(ConformanceFixtures.Read(file), 512);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("format violation", verdict.Message);
    }

    /// <summary>An RFC 3339-shaped <c>time</c> that is not a real instant is a
    /// payload violation, not an envelope violation — the same split the fixture
    /// above proves, with the day rolled by hand.</summary>
    [Fact]
    public void ImpossibleInstantIsASchemaViolationNotAnEnvelopeViolation()
    {
        var verdict = Wire(Mutation(root => root["time"] = "2026-02-30T18:00:00Z", Accepted)).Validate();
        Assert.Equal("schema-violation", verdict.ErrorClass);
    }

    [Fact]
    public void CommandThatShapeShiftsIntoAnUnknownTypeFailsClosed()
    {
        // Envelope-legal (`command.` prefix, known dataschema, conforming data)
        // but outside the contract's command table: fail closed, do not route.
        var verdict = Wire(Mutation(root => root["type"] = "org.facilitator.command.work.postpone.v1")).Validate();
        Assert.Equal("unknown-command", verdict.ErrorClass);
        Assert.Contains("not in the v1 contract", verdict.Message);
    }

    /// <summary>Every schema-derived command type the fixtures advertise is in
    /// the table, so the fail-closed door never blocks a contract command.</summary>
    [Fact]
    public void EveryCommandFixtureTypeIsKnown()
    {
        var unknown = ConformanceFixtures.Cases
            .Where(c => c.Class == "command" && c.Type is not null)
            .Select(c => c.Type!)
            .Where(t => !KnownCommands.All.Contains(t))
            .ToArray();
        Assert.Empty(unknown);
    }

    /// <summary>The table must equal the set derived from the shipped schema
    /// filenames — how <c>Validate.Fixtures</c> and <c>@ofac/protocol</c> derive
    /// it — so the three copies cannot drift.</summary>
    [Fact]
    public void TableMatchesTheSchemaDerivedCommandSet()
    {
        var derived = Validator.SchemaIds
            .Where(id => id.Contains("/command.", StringComparison.Ordinal))
            .Select(id => $"org.facilitator.{Path.GetFileName(id)[..^".json".Length]}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(derived.Order(StringComparer.Ordinal), KnownCommands.All.Order(StringComparer.Ordinal));
        Assert.Equal(21, KnownCommands.All.Count);
    }

    [Fact]
    public void UnknownDataschemaFailsClosedAsSchemaViolation()
    {
        var verdict = Wire(Mutation(root =>
            root["dataschema"] = "https://schemas.openfacilitator.org/v1/command.work.teleport.v1.json")).Validate();
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("unadvertised dataschema", verdict.Message);
    }

    /// <summary>The advertised id has to be the one the shipped set carries,
    /// verbatim: an envelope pointing at a different contract version is
    /// envelope-legal but unresolvable, so it is refused rather than validated
    /// against a near match.</summary>
    [Fact]
    public void DataschemaFromAnotherContractVersionFailsClosed()
    {
        var verdict = Wire(Mutation(root =>
            root["dataschema"] = "https://schemas.openfacilitator.org/v2/command.work.assign.v1.json")).Validate();
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("unadvertised dataschema", verdict.Message);
    }

    /// <summary>CloudEvents makes <c>data</c> optional; the contract does not —
    /// a command with no payload cannot be executed.</summary>
    [Fact]
    public void EnvelopeWithoutDataFailsClosed()
    {
        var verdict = Validate(Mutation(root => root.Remove("data")));
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("data does not conform", verdict.Message);
    }

    [Fact]
    public void NonConformingDataReportsTheAdvertisedSchema()
    {
        var verdict = Wire(Mutation(root => root["data"]!.AsObject().Remove("commandId"))).Validate();
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("command.work.assign.v1.json", verdict.Message);
    }

    /// <summary>Law of the closed data document: an unexpected field inside
    /// <c>data</c> is rejected even though it is envelope-legal.</summary>
    [Fact]
    public void UnexpectedDataFieldFailsClosed()
    {
        Assert.Equal("schema-violation", Wire(Mutation(root => root["data"]!.AsObject()["surprise"] = true)).Validate().ErrorClass);
    }

    /// <summary>… while the envelope layer itself is additive: a field the
    /// schema does not name at the root is tolerated, so a newer producer's
    /// extra envelope property is never a hard failure.</summary>
    [Fact]
    public void UnknownEnvelopeFieldIsTolerated()
    {
        Assert.True(Wire(Mutation(root => root["priority"] = 3)).Validate().Ok);
    }

    /// <summary>The size gate is a boundary, not a suggestion: the limit passes,
    /// one byte over is rejected.</summary>
    [Theory]
    [InlineData(EnvelopeValidator.MaxEnvelopeBytes, true)]
    [InlineData(EnvelopeValidator.MaxEnvelopeBytes + 1, false)]
    public void SizeGateIsInclusiveAtTheLimit(int wireBytes, bool expectedOk)
    {
        var verdict = Validator.Validate(ConformanceFixtures.Read(Assign), wireBytes);
        Assert.Equal(expectedOk, verdict.Ok);
        if (!expectedOk)
        {
            Assert.Equal("payload-too-large", verdict.ErrorClass);
            Assert.Contains($"{wireBytes} bytes exceeds", verdict.Message);
        }
    }

    /// <summary>The size law applies to the wire form, not the pretty-printed
    /// file: the large fixture sits inside the limit once whitespace is gone —
    /// which is why the index declares it valid.</summary>
    [Fact]
    public void LargeEnvelopePassesOnItsWireSize()
    {
        var root = ConformanceFixtures.Read("valid/command.work.assign.large.json");
        var bytes = ConformanceFixtures.WireBytes(root);
        Assert.True(Validator.Validate(root, bytes).Ok);
        Assert.InRange(bytes, 1, EnvelopeValidator.MaxEnvelopeBytes - 1);
        Assert.True(File.Exists(Path.Combine(ConformanceFixtures.Root(), "invalid/payload.too-large.json")));
    }

    [Fact]
    public void StringOverloadMeasuresThePayloadItIsGiven()
    {
        var text = ConformanceFixtures.ReadText(Assign);
        Assert.True(Validator.Validate(text).Ok);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, EnvelopeValidator.MaxEnvelopeBytes);
    }

    [Fact]
    public void SchemaRegistrySurfaceIsComplete()
    {
        Assert.Contains("https://schemas.openfacilitator.org/v1/envelope-command.json", Validator.SchemaIds);
        Assert.Contains("https://schemas.openfacilitator.org/v1/receipt.v1.json", Validator.SchemaIds);
        Assert.True(Validator.KnowsSchema("https://schemas.openfacilitator.org/v1/control.v1.json"));
        Assert.False(Validator.KnowsSchema("https://schemas.openfacilitator.org/v1/envelope-pony.json"));
        Assert.All(Validator.SchemaIds, id => Assert.True(Uri.IsWellFormedUriString(id, UriKind.Absolute), id));
    }

    /// <summary>Definition-plane validation: a bare document against one $id,
    /// with the same strict options the envelope path uses.</summary>
    [Fact]
    public void ValidateAgainstSchemaAcceptsAWorkflowDefinition()
    {
        using var doc = JsonDocument.Parse(Workflow().ToJsonString());
        var verdict = Validator.ValidateAgainstSchema(WorkflowSchema, doc.RootElement);
        Assert.True(verdict.Ok, verdict.Message);
    }

    [Fact]
    public void ValidateAgainstSchemaRejectsAnUnadvertisedId()
    {
        using var doc = JsonDocument.Parse("{}");
        var verdict = Validator.ValidateAgainstSchema("https://example.test/v1/nope.json", doc.RootElement);
        Assert.False(verdict.Ok);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("unadvertised dataschema", verdict.Message);
    }

    /// <summary>Diagnostics must name the failing keyword and the properties it
    /// blames, so a rejected definition is fixable from the error alone (and
    /// never report an empty location).</summary>
    [Theory]
    [InlineData("""{"version":1,"title":"t"}""", "at 'required'", "\"description\"")]
    [InlineData("""{"id":"wf.x","version":0,"title":"t","description":"d","on":{"event":"org.facilitator.event.work.accepted.v1"},"steps":[{"id":"a","title":"A","timer":{"for":"PT30M"}}]}""", "at 'properties'", "\"version\"")]
    [InlineData("""{"id":"wf.x","version":1,"title":"t","description":"d","on":{"event":"org.facilitator.event.work.accepted.v1"},"steps":[]}""", "at 'properties'", "\"steps\"")]
    public void ValidateAgainstSchemaDiagnosticsNameTheFailingKeyword(
        string document,
        string expectedKeyword,
        string expectedBlame)
    {
        using var parsed = JsonDocument.Parse(document);
        var verdict = Validator.ValidateAgainstSchema(WorkflowSchema, parsed.RootElement);
        Assert.False(verdict.Ok, document);
        Assert.Equal("schema-violation", verdict.ErrorClass);
        Assert.Contains("document does not conform", verdict.Message);
        Assert.Contains(expectedKeyword, verdict.Message);
        Assert.Contains(expectedBlame, verdict.Message);
        Assert.DoesNotContain(" at ''", verdict.Message);
    }

    /// <summary>Construction is idempotent: the kernel registers the embedded set
    /// once per process, so a second host in the same process is safe.</summary>
    [Fact]
    public void SecondValidatorSharesTheSameSchemaSet()
    {
        var again = EnvelopeValidator.CreateDefault();
        Assert.Equal(Validator.SchemaIds.Order(StringComparer.Ordinal), again.SchemaIds.Order(StringComparer.Ordinal));
        Assert.True(again.Validate(ConformanceFixtures.Read(Assign), 4096).Ok);
    }

    /// <summary>Guard for the test kit: the mutation helpers produce envelopes
    /// that stay valid until a mutation is applied.</summary>
    [Fact]
    public void UntouchedFixtureStillValidatesThroughTheHelpers()
    {
        Assert.True(Wire(Mutation(root => root["subject"] = "work/work-123/assignment/analysis-1")).Validate().Ok);
    }

    private static EnvelopeValidationResult Validate(JsonObject envelope) => Wire(envelope).Validate();

    /// <summary>A minimal schema-legal workflow definition (shape borrowed from
    /// the shipped gallery definitions).</summary>
    private static JsonObject Workflow() => JsonNode.Parse("""
        {"id":"wf.test","version":1,"title":"Test flow","description":"One timer step",
         "enabled":false,
         "on":{"event":"org.facilitator.event.work.progress.v1"},
         "steps":[{"id":"first-pause","title":"Quiet interval","on":"always","timer":{"for":"PT30M"}}]}
        """)!.AsObject();

    /// <summary>A copy of <paramref name="file"/> with <paramref name="mutate"/>
    /// applied.</summary>
    private static JsonObject Mutation(Action<JsonObject> mutate, string file = Assign)
    {
        var root = JsonNode.Parse(ConformanceFixtures.ReadText(file))!.AsObject();
        mutate(root);
        return root;
    }

    /// <summary>Parsed wire form of a mutation, with a wire byte count far
    /// below the limit so only the rule under test can reject it. The document
    /// stays alive with the element (a JSON tree keeps its document alive).</summary>
    private static WireEnvelope Wire(JsonObject envelope) => new(JsonDocument.Parse(envelope.ToJsonString()));

    /// <summary>A mutated payload ready for the validator.</summary>
    private sealed class WireEnvelope(JsonDocument document) : IDisposable
    {
        private readonly JsonDocument _document = document;

        /// <summary>Validate with an explicit byte count below the size limit.</summary>
        public EnvelopeValidationResult Validate(int wireBytes = 4096) =>
            Validator.Validate(_document.RootElement, wireBytes);

        public void Dispose() => _document.Dispose();
    }
}
