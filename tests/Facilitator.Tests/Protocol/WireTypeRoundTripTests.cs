using System.Text.Json;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Protocol;

/// <summary>
/// Generated-DTO round-trip over the shared fixtures — the unit-tier mirror of
/// <c>DtoRoundTrip</c> in <c>protocol/tools/Validate.Fixtures</c>: deserialize
/// with the committed <c>Ofac.Protocol</c> DTOs, re-serialize, compare DOMs.
/// Proves the DTOs reproduce the wire form property-for-property (wire casing
/// verbatim, camelCase policy off), which is what makes the kernel's outbox
/// payloads contract-identical to the fixtures. Also the unit-tier share of the
/// generated wire types: every DTO the contract's wire surface needs is
/// exercised here. No DB, no network.
/// </summary>
public sealed class WireTypeRoundTripTests
{
    /// <summary>Generated DTO for a fixture: the envelope class chosen by its
    /// <c>type</c> prefix (the harness's rule), or the named wire object for the
    /// link plane's non-envelope classes.</summary>
    private static Type DtoTypeFor(ConformanceFixtures.Case c) => c.Class switch
    {
        "receipt" => typeof(Ofac.Protocol.ReceiptV1),
        "control" => typeof(Ofac.Protocol.ControlFrame),
        "enroll" => c.SchemaFile == "enroll-response.v1.json"
            ? typeof(Ofac.Protocol.EnrollResponseV1)
            : typeof(Ofac.Protocol.EnrollRequestV1),
        "delivery" => typeof(Ofac.Protocol.LinkDeliveryV1),
        _ => ConformanceFixtures.Read(c.File).GetProperty("type").GetString()!
            .StartsWith("org.facilitator.command.", StringComparison.Ordinal)
            ? typeof(Ofac.Protocol.EnvelopeCommand)
            : typeof(Ofac.Protocol.EnvelopeEvent),
    };

    /// <summary>Every contract-valid fixture — envelope or link-plane frame —
    /// survives a trip through its generated DTO unchanged.</summary>
    [Theory]
    [MemberData(nameof(ValidFixturesWithDtos))]
    public void ValidFixturesRoundTripThroughTheGeneratedDtos(string file, string dtoName)
    {
        var text = ConformanceFixtures.ReadText(file);
        var dtoType = DtoTypeNamed(dtoName);
        var fixture = JsonNode.Parse(text);

        var dto = JsonSerializer.Deserialize(text, dtoType, ConformanceFixtures.WireOptions);
        Assert.NotNull(dto);

        var reserialized = JsonNode.Parse(JsonSerializer.Serialize(dto, dtoType, ConformanceFixtures.WireOptions))!;
        Assert.True(
            JsonNode.DeepEquals(fixture, reserialized),
            $"{file} drifted through {dtoName}:\n  fixture:       {fixture}\n  round-tripped: {reserialized}");
    }

    /// <summary>The generated converters are the wire law for the identity
    /// bounds: an overlong id or subject cannot be unmarshalled, so the kernel
    /// cannot mint a wire form the contract refuses.</summary>
    [Theory]
    [InlineData("id", 129)] // idToken law: 1..128
    [InlineData("subject", 1025)] // subject law: 1..1024
    public void OverlongIdentityCannotBeDeserialized(string property, int length)
    {
        var root = Mutation(property, new string('x', length));
        AssertUnmarshalable(root);

        // One inside the bound still round-trips, so the rejection is the law
        // and not a broken probe.
        var justInside = Mutation(property, new string('x', length - 1));
        Assert.NotNull(JsonSerializer.Deserialize(
            justInside.ToJsonString(), typeof(Ofac.Protocol.EnvelopeCommand), ConformanceFixtures.WireOptions));
    }

    /// <summary>Same for the closed vocabularies: the const converters refuse an
    /// unknown <c>datacontenttype</c> or <c>specversion</c>.</summary>
    [Theory]
    [InlineData("datacontenttype", "application/xml")]
    [InlineData("specversion", "1.1")]
    public void UnknownConstantCannotBeDeserialized(string property, string value) =>
        AssertUnmarshalable(Mutation(property, value));

    /// <summary>The wire serializer the harness measures with is the one the
    /// generated DTOs declare: converter set in place, nulls omitted, no
    /// HTML-escaping (which would inflate every <c>urn:</c> source).</summary>
    [Fact]
    public void WireOptionsMatchTheGeneratedContract()
    {
        Assert.NotEmpty(ConformanceFixtures.WireOptions.Converters);
        Assert.DoesNotContain("\\u0026", JsonSerializer.Serialize(new { u = "urn:a&b" }, ConformanceFixtures.WireOptions));
        var envelope = (Ofac.Protocol.EnvelopeCommand)JsonSerializer
            .Deserialize(ConformanceFixtures.ReadText("valid/command.work.assign.json"), typeof(Ofac.Protocol.EnvelopeCommand), ConformanceFixtures.WireOptions)!;
        envelope.causationid = null;
        Assert.DoesNotContain(
            "causationid",
            JsonSerializer.Serialize(envelope, typeof(Ofac.Protocol.EnvelopeCommand), ConformanceFixtures.WireOptions));
    }

    private static JsonObject Mutation(string property, string value)
    {
        var root = JsonNode.Parse(ConformanceFixtures.ReadText("valid/command.work.assign.json"))!.AsObject();
        root[property] = value;
        return root;
    }

    private static void AssertUnmarshalable(JsonObject envelope)
    {
        var ex = Record.Exception(() => JsonSerializer.Deserialize(
            envelope.ToJsonString(), typeof(Ofac.Protocol.EnvelopeCommand), ConformanceFixtures.WireOptions));
        Assert.NotNull(ex);
        Assert.Contains("Cannot unmarshal", ex!.Message);
    }

    private static Type DtoTypeNamed(string name) =>
        typeof(Ofac.Protocol.EnvelopeCommand).Assembly.GetType($"Ofac.Protocol.{name}")
            ?? throw new InvalidOperationException($"generated DTO {name} is missing");

    /// <summary>Fixture → generated DTO type, for every contract-valid case the
    /// DTO layer is responsible for (adapter frames have no generated DTO —
    /// they are a $def profile of the shipped child wire, validated as a schema).</summary>
    public static TheoryData<string, string> ValidFixturesWithDtos { get; } = Build();

    private static TheoryData<string, string> Build()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in ConformanceFixtures.Cases.Where(c => c.ExpectValid && c.Class != "adapter"))
        {
            data.Add(c.File, DtoTypeFor(c).Name);
        }

        return data;
    }
}
