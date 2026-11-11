using System.Reflection;
using System.Text.Json;

namespace Facilitator.Tests.ScenarioGate;

/// <summary>
/// The cross-assembly half of the scenario gate's fail-closed law over
/// <c>docs/scenarios/scenario-bindings.json</c>: every dotnet evidence id
/// under THIS subclass's evidence prefix names a method that exists on a
/// class in the subclass's subject assembly right now (js evidence is
/// checked on disk, as always). Evidence ids carry one optional prefix:
/// a bare <c>Class.Method</c> resolves against <c>Facilitator.Tests</c>,
/// <c>ScenarioWalks:Class.Method</c> against the walk assembly — the prefix
/// rule and the subject assembly are the two knobs a subclass supplies.
/// Each subclass supplies its own filtered <c>TheoryData</c> via
/// <see cref="ScenarioIdsWith"/> and forwards rows to
/// <see cref="ResolveEvidenceFor"/>.
/// </summary>
public abstract class ScenarioEvidenceResolutionTestsBase
{
    protected static string RepoRoot => FindRepoRoot();

    /// <summary>The assembly whose types resolve the evidence type names.</summary>
    protected abstract Assembly Subject { get; }

    /// <summary>
    /// The evidence ids this subclass owns: <c>""</c> means every dotnet id
    /// that carries no prefix (no <c>:</c>), a nonempty prefix (e.g.
    /// <c>"ScenarioWalks:"</c>) means only ids starting with it.
    /// </summary>
    protected abstract string EvidencePrefix { get; }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenFacilitator.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root with OpenFacilitator.slnx");
    }

    protected sealed record Binding(string Id, string Family, string Tier, string Title,
        string Verdict, string Lane, string[] Evidence, string Note);

    protected static List<Binding> Load()
    {
        var path = Path.Combine(RepoRoot, "docs", "scenarios", "scenario-bindings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Binding>();
        foreach (var e in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            list.Add(new Binding(
                e.GetProperty("id").GetString()!,
                e.GetProperty("family").GetString()!,
                e.GetProperty("tier").GetString()!,
                e.GetProperty("title").GetString()!,
                e.GetProperty("verdict").GetString()!,
                e.GetProperty("lane").GetString()!,
                e.GetProperty("evidence").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                e.GetProperty("note").GetString()!));
        }

        return list;
    }

    private static bool Owned(string evidence, string prefix) =>
        prefix.Length == 0
            ? !evidence.Contains(':')
            : evidence.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>Theory data over every manifest id (the unfiltered gate law).</summary>
    protected static TheoryData<string> AllScenarioIds() =>
        new(Load().Select(b => (string)b.Id).ToArray());

    /// <summary>
    /// Theory data for a subclass: the scenario ids that carry at least one
    /// evidence id under <paramref name="prefix"/> (js ids never count).
    /// </summary>
    protected static TheoryData<string> ScenarioIdsWith(string prefix) =>
        new(Load()
            .Where(b => b.Evidence.Any(e => !e.StartsWith("js:", StringComparison.Ordinal)
                && Owned(e, prefix)))
            .Select(b => (string)b.Id)
            .ToArray());

    /// <summary>
    /// The moved gate law, run per scenario id: verdict vocabulary, evidence
    /// presence for covered/partial, and resolution of every evidence id this
    /// subclass owns against <see cref="Subject"/>.
    /// </summary>
    protected void ResolveEvidenceFor(string id)
    {
        var b = Load().Single(x => x.Id == id);
        Assert.Contains(b.Verdict, new[]
        {
            "covered", "partial", "absent", "silent-absent", "divergent", "unexpressible", "environment-blocked",
        });
        if (b.Verdict is "covered" or "partial")
        {
            Assert.NotEmpty(b.Evidence);
        }

        foreach (var ev in b.Evidence)
        {
            if (ev.StartsWith("js:", StringComparison.Ordinal))
            {
                Assert.True(File.Exists(Path.Combine(RepoRoot, ev[3..])),
                    $"{id}: js evidence missing on disk: {ev}");
                continue;
            }

            if (!Owned(ev, EvidencePrefix))
            {
                continue;
            }

            var idBody = EvidencePrefix.Length == 0 ? ev : ev[EvidencePrefix.Length..];
            var (typeName, methodName) = idBody.LastIndexOf('.') switch
            {
                < 0 => throw new InvalidOperationException($"{id}: evidence `{ev}` is not Class.Method"),
                var i => (idBody[..i], idBody[(i + 1)..]),
            };
            var type = Subject
                .GetTypes()
                // The artifact family ships an abstract `<Name>TestsBase` with
                // Fs/S3 concrete twins; the evidence names the family, so any
                // of those spellings resolves (FirstOrDefault: the Fs twin
                // and the base both carry the method).
                .FirstOrDefault(t =>
                    t.Name == typeName || t.FullName == typeName
                    || t.Name == $"{typeName}Base" || t.Name == $"Fs{typeName}" || t.Name == $"S3{typeName}");
            if (type is null)
            {
                throw new InvalidDataException($"{id}: no type resolves for `{typeName}`");
            }

            if (type.GetMethod(methodName) is null)
            {
                throw new InvalidDataException($"{id}: `{typeName}` has no method `{methodName}`");
            }
        }
    }
}
