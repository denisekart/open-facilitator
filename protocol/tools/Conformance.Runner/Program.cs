// Kernel conformance runner entrypoint. Config comes from a
// JSON file (--config) with CLI flags on top; the JSON report goes to
// stdout and, with --out, to a file. Exit code is non-zero when any
// scenario failed.
//
//   dotnet run --project protocol/tools/Conformance.Runner -- \
//     --kernel-url http://localhost:8088 --idp-url http://localhost:8080 \
//     --realm facilitator --username alice --password alice \
//     --enrollment-token "$TOKEN" \
//     --link-client "node protocol/tools/link-client-foreign/dist/main.js" \
//     --out report.json

using System.Text.Json.Nodes;

using Ofac.Conformance;

var flags = new Dictionary<string, string>(StringComparer.Ordinal);
string? outPath = null;
for (var i = 0; i < args.Length; i += 1)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"usage: conformance-runner [--config f] [--kernel-url U] [--idp-url U] [--realm R] [--username U] [--password P] [--operator-client C] [--operator-secret S] [--enrollment-token T] [--team SLUG] [--link-client CMD] [--fixtures-dir DIR] [--out FILE]");
        return 2;
    }

    flags[args[i][2..]] = args[++i];
}

outPath = flags.GetValueOrDefault("out");
var config = ConfigLoader.Load(flags);
var fixturesDir = flags.GetValueOrDefault("fixtures-dir", "protocol/conformance-fixtures");

using var kernel = new KernelClient(config);
await kernel.AuthenticateAsync(CancellationToken.None);

var scenarios = new Scenarios(config, kernel, fixturesDir);
await scenarios.RunAsync(CancellationToken.None);

var failed = scenarios.Results.Any(r => r.Status == "fail");
var report = new JsonObject
{
    ["generatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
    ["kernel"] = config.BaseUrl,
    ["realm"] = config.Realm,
    ["scenarios"] = new JsonArray([.. scenarios.Results.Select(r => (JsonNode)new JsonObject
    {
        ["item"] = r.Item,
        ["scenario"] = r.Scenario,
        ["status"] = r.Status,
        ["evidence"] = r.Evidence,
    })]),
    ["summary"] = new JsonObject
    {
        ["pass"] = scenarios.Results.Count(r => r.Status == "pass"),
        ["fail"] = scenarios.Results.Count(r => r.Status == "fail"),
        ["deferred"] = scenarios.Results.Count(r => r.Status == "deferred"),
    },
};

var text = report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(text);
if (outPath is not null)
{
    await File.WriteAllTextAsync(outPath, text + "\n", CancellationToken.None);
}

return failed ? 1 : 0;
