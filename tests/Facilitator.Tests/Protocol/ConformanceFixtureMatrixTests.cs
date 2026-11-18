using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Protocol;

/// <summary>
/// Index-driven matrix (no DB): every case in the shared
/// <c>protocol/conformance-fixtures/index.json</c> must classify exactly as the
/// index declares, through the validators the kernel ships. This is the .NET
/// unit-tier mirror of <c>src/protocol/test/fixtures.test.ts</c> and
/// <c>protocol/tools/Validate.Fixtures</c>: one arbiter, three ecosystems.
/// </summary>
public sealed class ConformanceFixtureMatrixTests
{
    [Theory]
    [MemberData(nameof(ValidEnvelopeFixtures))]
    public void ValidEnvelopesPassTheSharedValidator(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.Classify(c);
        Assert.True(verdict.Ok, $"{file}: {verdict.Describe}");

        // Where the index declares a type, it must be the payload's type; the
        // wire form must sit inside the size law the index itself sets.
        if (c.Type is not null)
        {
            Assert.Equal(c.Type, ConformanceFixtures.Read(file).GetProperty("type").GetString());
        }

        Assert.InRange(verdict.WireBytes, 1, ConformanceFixtures.MaxEnvelopeBytes);
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopeFixtures))]
    public void InvalidEnvelopesFailWithTheDeclaredClass(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.Classify(c);
        Assert.False(verdict.Ok, $"{file} must be rejected as {c.ErrorClass}");
        Assert.Equal(c.ErrorClass, verdict.ErrorClass);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Message), $"{file}: every rejection carries diagnostics");
    }

    [Theory]
    [MemberData(nameof(ControlFixtures))]
    public void ControlFramesMatchTheirDeclaration(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.Classify(c);
        Assert.Equal(c.ExpectValid, verdict.Ok);
        if (!c.ExpectValid)
        {
            Assert.Equal(c.ErrorClass, verdict.ErrorClass);
        }
    }

    [Theory]
    [MemberData(nameof(ReceiptFixtures))]
    public void ReceiptsMatchTheirDeclaration(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.Classify(c);
        Assert.Equal(c.ExpectValid, verdict.Ok);
        if (!c.ExpectValid)
        {
            Assert.Equal(c.ErrorClass, verdict.ErrorClass);
        }
    }

    /// <summary>Enroll/delivery/adapter frames are bare payloads addressed by
    /// <c>schemaFile</c> — the definition-plane path of the same validator.</summary>
    [Theory]
    [MemberData(nameof(NonEnvelopeFixtures))]
    public void NonEnvelopeFramesMatchTheirDeclaration(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.Classify(c);
        Assert.Equal(c.ExpectValid, verdict.Ok);
        if (!c.ExpectValid)
        {
            Assert.Equal(c.ErrorClass, verdict.ErrorClass);
        }
    }

    /// <summary>Behaviour fixtures name the response frame the contract
    /// expects (an <c>unsupported</c> receipt for an unknown command, an
    /// <c>incompatible</c> goodbye for a failed negotiation); that frame has to
    /// be contract-valid on its own or the pair proves nothing.</summary>
    [Theory]
    [MemberData(nameof(BehaviourFixtures))]
    public void BehaviourFixturesNameAValidResponseFrame(string file)
    {
        var c = ConformanceFixtures.ByFile(file);
        var verdict = ConformanceFixtures.ResponseVerdict(c);
        Assert.True(verdict.Ok, $"{c.ResponseFixture}: {verdict.Describe}");
    }

    /// <summary>Every <c>dataschema</c> a contract-valid fixture advertises has
    /// to resolve in the shipped schema set, or the fixture validates against
    /// nothing. The only advertised id that is not shipped belongs to the
    /// unknown-command fixture: the command table refuses it before its
    /// dataschema is ever consulted, which is the fail-closed ordering.</summary>
    [Fact]
    public void EveryAdvertisedDataschemaIsShipped()
    {
        var unshipped = ConformanceFixtures.AdvertisedSchemaIds()
            .Where(id => !ConformanceFixtures.Envelopes.KnowsSchema(id))
            .ToArray();
        Assert.Equal(["https://schemas.openfacilitator.org/v1/command.novel.thing.v1.json"], unshipped);
        Assert.Equal("unknown-command", ConformanceFixtures.ByFile("invalid/command.unknown.v1.json").ErrorClass);
        foreach (var c in ConformanceFixtures.Cases.Where(c => c.IsEnvelope && c.ExpectValid))
        {
            var advertised = ConformanceFixtures.Read(c.File).GetProperty("dataschema").GetString()!;
            Assert.True(ConformanceFixtures.Envelopes.KnowsSchema(advertised), c.File);
        }
    }

    /// <summary>The index is the arbiter for the wire-size law: if it ever
    /// disagrees with the compiled-in limit, the harnesses drift apart.</summary>
    [Fact]
    public void SizeLimitMatchesTheCompiledInConstant() =>
        Assert.Equal(EnvelopeValidator.MaxEnvelopeBytes, ConformanceFixtures.MaxEnvelopeBytes);

    /// <summary>Guard for the matrix itself: the index and the fixture
    /// directories may not drift, and every declared file must exist.</summary>
    [Fact]
    public void IndexAndFixtureTreeAgree()
    {
        var root = ConformanceFixtures.Root();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ConformanceFixtures.Cases)
        {
            Assert.True(File.Exists(Path.Combine(root, c.File)), $"{c.File} is listed but missing");
            listed.Add(c.File);
            if (c.ResponseFixture is not null)
            {
                Assert.True(File.Exists(Path.Combine(root, c.ResponseFixture)), $"{c.ResponseFixture} is referenced but missing");
                listed.Add(c.ResponseFixture);
            }
        }

        var onDisk = new[] { "valid", "invalid" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir)))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToArray();
        AssertSubset(onDisk, listed);
    }

    /// <summary>Classified count sanity: the .NET matrix must see every case the
    /// index declares (a silently skipped class would hide regressions).</summary>
    [Fact]
    public void EveryCaseIsClassifiedByExactlyOneHarnessPath()
    {
        var classes = new[] { "command", "event", "receipt", "control", "enroll", "delivery", "adapter" };
        foreach (var c in ConformanceFixtures.Cases)
        {
            Assert.Contains(c.Class, classes);
            if (c.Class is "enroll" or "delivery" or "adapter")
            {
                Assert.False(string.IsNullOrEmpty(c.SchemaFile), $"{c.File} needs a schemaFile");
            }

            if (c.Class is "command" or "event")
            {
                Assert.StartsWith(c.Class == "command" ? "org.facilitator.command." : "org.facilitator.event.", ReadType(c));
            }
            else
            {
                Assert.Null(c.Type);
            }

            if (!c.ExpectValid)
            {
                Assert.False(string.IsNullOrEmpty(c.ErrorClass), $"{c.File} needs an errorClass");
            }
        }
    }

    private static string? ReadType(ConformanceFixtures.Case c) =>
        ConformanceFixtures.Read(c.File).TryGetProperty("type", out var t) ? t.GetString() : null;

    private static void AssertSubset(string[] actual, HashSet<string> expected) =>
        Assert.Empty(actual.Where(f => !expected.Contains(f)).ToArray());

    public static TheoryData<string> ValidEnvelopeFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.IsEnvelope && c.ExpectValid);

    public static TheoryData<string> InvalidEnvelopeFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.IsEnvelope && !c.ExpectValid);

    public static TheoryData<string> ControlFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.Class == "control");

    public static TheoryData<string> ReceiptFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.Class == "receipt");

    public static TheoryData<string> NonEnvelopeFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.Class is "enroll" or "delivery" or "adapter");

    public static TheoryData<string> BehaviourFixtures =>
        ConformanceFixtures.TheoryWhere(c => c.ResponseFixture is not null);
}
