using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Facilitator.Kernel.Cli;

/// <summary>
/// The Facilitator doubles as its own client: <c>cli</c> mode talks to the
/// public HTTP API only — no backdoor. Verbs: <c>work create</c>,
/// <c>work steer</c>, <c>work get</c>, <c>work timeline</c>.
/// </summary>
public static class CliRunner
{
    /// <summary>Run one CLI invocation; exit code 0 iff the API answered 2xx.</summary>
    /// <param name="args">Arguments after the <c>cli</c> verb.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var url = "http://localhost:8088";
        var token = Environment.GetEnvironmentVariable("FACILITATOR_TOKEN") ?? "dev-token";
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case string opt when opt.StartsWith("--", StringComparison.Ordinal)
                                     && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    options[opt[2..]] = args[++i];
                    break;
                case string opt when opt.StartsWith("--", StringComparison.Ordinal):
                    Console.Error.WriteLine($"missing value for {opt}");
                    return 2;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        using var client = new HttpClient { BaseAddress = new Uri(url) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (method, path, json) = positional switch
        {
            ["work", "create"] => ("POST", "/api/works", Serialise(new Dictionary<string, object> { ["objective"] = Required(options, "objective") })),
            ["work", "get", var id] => ("GET", $"/api/works/{id}", null),
            ["work", "timeline", var id] => ("GET", $"/api/works/{id}/timeline", null),
            ["work", "steer", var id] => ("POST", $"/api/works/{id}/steer", Serialise(new Dictionary<string, object>
            {
                ["directive"] = Required(options, "directive"),
                ["commandId"] = Guid.NewGuid().ToString("N"),
                ["expectedVersion"] = long.Parse(Required(options, "expect-version")),
            })),
            _ => Usage(),
        };

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"request failed: {ex.Message}");
            return 1;
        }

        using (response)
        {
            Console.Out.WriteLine(await response.Content.ReadAsStringAsync(ct));
        }

        return (int)response.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    private static string Required(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
        {
            Console.Error.WriteLine($"--{key} is required");
            Environment.Exit(2);
        }

        return value;
    }

    private static (string, string, string?) Usage()
    {
        Console.Error.WriteLine("usage: cli [--url URL] [--token TOKEN] work create --objective \"...\" | work steer <id> --directive \"...\" --expect-version N | work get <id> | work timeline <id>");
        Environment.Exit(2);
        return ("GET", "/health", null);
    }

    private static string Serialise(IDictionary<string, object> payload)
    {
        var doc = JsonSerializer.SerializeToNode(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        return doc.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
