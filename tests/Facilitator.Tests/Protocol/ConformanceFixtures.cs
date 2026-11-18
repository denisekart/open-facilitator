using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Ofac.Protocol;
using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Protocol;

/// <summary>
/// .NET mirror of the shared conformance-fixture harness. Reads
/// <c>protocol/conformance-fixtures/index.json</c> — the arbiter both the
/// vitest harness (<c>src/protocol/test/fixtures.test.ts</c>) and
/// <c>protocol/tools/Validate.Fixtures</c> consume — and classifies every case
/// through the validators the kernel actually runs, so the unit tier validates
/// the same goldens as the TS side.
/// Loader semantics are reused from <c>Validate.Fixtures/Program.cs</c>
/// (<c>RunCase</c>) rather than re-invented: the wire-size gate measures the
/// compact wire form under the generated DTOs' converter settings (JSON.stringify
/// escaping, nulls omitted), non-envelope classes dispatch by
/// <c>schemaFile</c>/<c>class</c>, and version negotiation is judged on
/// <c>hello</c> frames only. No DB, no network: every schema is read from the
/// <see cref="Ofac.Protocol"/> assembly's embedded resources.
/// </summary>
public static class ConformanceFixtures
{
    /// <summary>$id base every fixture <c>dataschema</c> rides on.</summary>
    public const string SchemaBaseUri = "https://schemas.openfacilitator.org/v1/";

    private const string CommandPrefix = "org.facilitator.command.";

    /// <summary>One <c>index.json</c> case (same shape as both other harnesses).</summary>
    /// <param name="File">Fixture path relative to the fixtures root.</param>
    /// <param name="Expect"><c>valid</c> or <c>invalid</c>.</param>
    /// <param name="Class">Wire class: command/event/receipt/control/enroll/delivery/adapter.</param>
    /// <param name="Type">Declared envelope <c>type</c>, when the index carries one.</param>
    /// <param name="ErrorClass">Declared error class for invalid cases.</param>
    /// <param name="SchemaFile">Schema filename for enroll/delivery/adapter cases.</param>
    /// <param name="ResponseFixture">Response frame a behaviour fixture expects.</param>
    public sealed record Case(
        string File,
        string Expect,
        string Class,
        string? Type,
        string? ErrorClass,
        string? SchemaFile,
        string? ResponseFixture)
    {
        /// <summary>True when the index declares the payload contract-valid.</summary>
        public bool ExpectValid => Expect == "valid";

        /// <summary>True for CloudEvents envelope cases (command/event).</summary>
        public bool IsEnvelope => Class is "command" or "event";
    }

    /// <summary>Observed verdict, plus the wire size it was observed at.</summary>
    /// <param name="Ok">True when the payload is contract-valid.</param>
    /// <param name="ErrorClass">Observed error class when <paramref name="Ok"/> is false.</param>
    /// <param name="Message">Validator diagnostics.</param>
    /// <param name="WireBytes">Compact wire-form byte count of the payload.</param>
    public sealed record Verdict(bool Ok, string? ErrorClass, string? Message, int WireBytes)
    {
        /// <summary>Failure description for assert messages.</summary>
        public string Describe => $"{ErrorClass ?? "valid"}: {Message ?? "no diagnostics"}";
    }

    /// <summary>Envelope validator over the schemas embedded in <c>Ofac.Protocol</c>.</summary>
    public static EnvelopeValidator Envelopes { get; } = EnvelopeValidator.CreateDefault();

    /// <summary>Control/receipt frame validator over the same embedded schema set.</summary>
    public static ControlFrameValidator Frames { get; } = ControlFrameValidator.CreateDefault();

    /// <summary>
    /// Wire serializer options matching the fixture pipeline: the generated
    /// DTOs' converter set (internal <c>Ofac.Protocol.Converter.Settings</c>,
    /// reached by reflection exactly as <c>Validate.Fixtures</c> does) with
    /// JSON.stringify-equivalent escaping, no pretty-printing and nulls
    /// omitted. Its byte count is the harness's <c>WireBytes</c>.
    /// </summary>
    public static JsonSerializerOptions WireOptions { get; } = BuildWireOptions();

    /// <summary>Envelope byte limit as the index arbiter declares it.</summary>
    public static int MaxEnvelopeBytes => IndexData.Value.MaxEnvelopeBytes;

    /// <summary>Every declared case, in index order.</summary>
    public static IReadOnlyList<Case> Cases => IndexData.Value.Cases;

    /// <summary>Case for one fixture path (fixture names are unique in the index).</summary>
    public static Case ByFile(string file) =>
        Cases.SingleOrDefault(c => c.File == file)
            ?? throw new InvalidOperationException($"{file} is not listed in index.json");

    /// <summary>Cases matching <paramref name="filter"/>, as theory data keyed by file.</summary>
    public static TheoryData<string> TheoryWhere(Func<Case, bool> filter)
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Where(filter))
        {
            data.Add(c.File);
        }

        return data;
    }

    /// <summary>Fixtures root, found by walking up from the test assembly
    /// location (repo-root-relative, build-dir safe).</summary>
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "protocol", "conformance-fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("protocol/conformance-fixtures not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Raw fixture text.</summary>
    public static string ReadText(string file) => File.ReadAllText(Path.Combine(Root(), file));

    /// <summary>Parsed fixture. The element keeps its document alive, so the
    /// value stays readable after this call.</summary>
    public static JsonElement Read(string file) => JsonDocument.Parse(ReadText(file)).RootElement;

    /// <summary>Wire-form byte count of a payload (what the size gate measures).</summary>
    public static int WireBytes(JsonElement root) => JsonSerializer.SerializeToUtf8Bytes(root, WireOptions).Length;

    /// <summary>
    /// Index-equivalent verdict for one case: the shipped validators' own
    /// classification plus the two harness-side rules they do not own — the
    /// envelope size gate measured on the compact wire form, and protocol
    /// version negotiation, which is observable on <c>hello</c> only.
    /// </summary>
    public static Verdict Classify(Case c)
    {
        var root = Read(c.File);
        var bytes = WireBytes(root);
        var verdict = c.Class switch
        {
            "receipt" => Of(Frames.ValidateReceipt(root), bytes),
            "control" => Of(Frames.ValidateControlFrame(root), bytes),
            "enroll" or "delivery" or "adapter" => Of(Envelopes.ValidateAgainstSchema(SchemaBaseUri + c.SchemaFile, root), bytes),
            _ => Of(Envelopes.Validate(root, bytes), bytes),
        };
        if (!verdict.Ok)
        {
            return verdict;
        }

        if (c.IsEnvelope && bytes > MaxEnvelopeBytes)
        {
            return new Verdict(false, "payload-too-large", $"{bytes} wire bytes exceed {MaxEnvelopeBytes}", bytes);
        }

        // Behaviour fixtures: negotiation only becomes observable on hello.
        if (c.Class == "control" && root.GetProperty("kind").GetString() == "hello"
            && !root.GetProperty("protocolVersions").EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal).Contains("v1"))
        {
            return new Verdict(false, "no-common-version", "hello offers no compatible version", bytes);
        }

        return verdict;
    }

    /// <summary>
    /// Verdict for the response frame a behaviour fixture names: a path
    /// containing <c>/receipt.</c> is judged as a receipt, anything else as a
    /// control frame — the same gate <c>RunCase</c> applies to the index's
    /// <c>receipt</c> field.
    /// </summary>
    public static Verdict ResponseVerdict(Case c)
    {
        var response = c.ResponseFixture
            ?? throw new InvalidOperationException($"{c.File} names no response fixture");
        var root = Read(response);
        var bytes = WireBytes(root);
        return response.Contains("/receipt.", StringComparison.Ordinal)
            ? Of(Frames.ValidateReceipt(root), bytes)
            : Of(Frames.ValidateControlFrame(root), bytes);
    }

    /// <summary>Every <c>dataschema</c> $id advertised by envelope fixtures.</summary>
    public static IReadOnlyCollection<string> AdvertisedSchemaIds() =>
        Cases.Where(c => c.IsEnvelope)
            .Select(c => Read(c.File).GetProperty("dataschema").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static Verdict Of(EnvelopeValidationResult result, int bytes) =>
        new(result.Ok, result.ErrorClass, result.Message, bytes);

    private static Verdict Of(FrameValidationResult result, int bytes) =>
        new(result.Ok, result.ErrorClass, result.Message, bytes);

    private static JsonSerializerOptions BuildWireOptions()
    {
        var generated = (JsonSerializerOptions)typeof(EnvelopeCommand)
            .Assembly
            .GetType("Ofac.Protocol.Converter")!
            .GetField("Settings")!
            .GetValue(null)!;
        return new JsonSerializerOptions(generated)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    // `Index` alone would bind to System.Index, hence IndexData.
    private static readonly Lazy<(int MaxEnvelopeBytes, IReadOnlyList<Case> Cases)> IndexData = new(Load);

    private static (int MaxEnvelopeBytes, IReadOnlyList<Case> Cases) Load()
    {
        using var index = JsonDocument.Parse(ReadText("index.json"));
        var limit = index.RootElement.GetProperty("limits").GetProperty("maxEnvelopeBytes").GetInt32();
        var cases = index.RootElement.GetProperty("cases").EnumerateArray().Select(c => new Case(
            Text(c, "file")!,
            Text(c, "expect")!,
            Text(c, "class")!,
            Text(c, "type"),
            Text(c, "errorClass"),
            Text(c, "schemaFile"),
            Text(c, "receipt"))).ToArray();
        return (limit, cases);
    }

    private static string? Text(JsonElement caseElement, string property) =>
        caseElement.TryGetProperty(property, out var value) ? value.GetString() : null;
}
