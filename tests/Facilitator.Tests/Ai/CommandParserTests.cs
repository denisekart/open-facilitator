using System.Text.Json;

using Facilitator.Kernel.Ai;

namespace Facilitator.Tests.Ai;

/// <summary>Pure slash-parser law: built-in + custom verbs, arg mapping,
/// unknown-verb determinism. No AI, no database.</summary>
public sealed class CommandParserTests
{
    [Fact]
    public void NonSlashTextIsNotACommand()
    {
        Assert.Null(CommandParser.TryParse("please build the thing", CommandRegistry.BuiltIns));
    }

    [Fact]
    public void WorkVerbCapturesTheFreeTextObjective()
    {
        var parsed = CommandParser.TryParse("/work build the thing", CommandRegistry.BuiltIns)!;
        Assert.Equal("work", parsed.Verb);
        Assert.False(parsed.Unknown);
        Assert.Equal("build the thing", parsed.Rest);
        Assert.Empty(parsed.Args);
    }

    [Fact]
    public void DeclaredArgsFillPositionallyAndRemainderIsRest()
    {
        var parsed = CommandParser.TryParse("/approve 42 accept", CommandRegistry.BuiltIns)!;
        Assert.False(parsed.Unknown);
        Assert.Equal("42", parsed.Args["approval-id"]);
        Assert.Equal("accept", parsed.Args["decision"]);
        Assert.Empty(parsed.Rest);
    }

    [Fact]
    public void OptionalArgsAreSimplyAbsentWhenMissing()
    {
        var parsed = CommandParser.TryParse("/status", CommandRegistry.BuiltIns)!;
        Assert.False(parsed.Unknown);
        Assert.Empty(parsed.Args);
    }

    [Fact]
    public void UnknownVerbParsesDeterministically()
    {
        var parsed = CommandParser.TryParse("/frobnicate now", CommandRegistry.BuiltIns)!;
        Assert.True(parsed.Unknown);
        Assert.Equal("frobnicate", parsed.Verb);
        Assert.Equal("now", parsed.Rest);
    }

    [Fact]
    public void BareSlashIsUnknown()
    {
        var parsed = CommandParser.TryParse("/", CommandRegistry.BuiltIns)!;
        Assert.True(parsed.Unknown);
    }

    [Fact]
    public void CustomRegistryVerbMergesOverBuiltIns()
    {
        using var doc = JsonDocument.Parse("""
            {"commands":[{"verb":"run-ci-tests","description":"Run CI","capabilityId":"ci.run","args":[{"name":"branch","required":false}]}]}
            """);
        var merged = CommandRegistry.Merge(CommandRegistry.BuiltIns, doc.RootElement.Clone());

        Assert.True(merged.TryGet("/run-ci-tests", out var spec));
        Assert.Equal("ci.run", spec.CapabilityId);
        Assert.False(spec.BuiltIn);
        // Built-ins survive a merge.
        Assert.True(merged.TryGet("work", out _));

        var parsed = CommandParser.TryParse("/run-ci-tests main extra words", merged)!;
        Assert.False(parsed.Unknown);
        Assert.Equal("main", parsed.Args["branch"]);
        Assert.Equal("extra words", parsed.Rest);
    }

    [Fact]
    public void MalformedCustomPayloadDegradesToBuiltIns()
    {
        using var doc = JsonDocument.Parse("""{"commands":[{"verb":"BAD VERB","description":""}]}""");
        var merged = CommandRegistry.Merge(CommandRegistry.BuiltIns, doc.RootElement.Clone());
        Assert.Equal(CommandRegistry.BuiltIns.All().Count, merged.All().Count);
    }
}
