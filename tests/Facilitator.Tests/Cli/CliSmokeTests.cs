using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Facilitator.Tests.Cli;

/// <summary>
/// §26.1 item 5: the Facilitator assembly doubles as its own client. A real
/// server process (same container, unique port) is started, then the SAME
/// assembly is run in <c>cli</c> mode as a child process against the public
/// HTTP API — create + get round-trip through JSON, no backdoor.
/// </summary>
[Collection("postgres:dispatch-link")]
public class CliSmokeTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task CliMode_RidesPublicApi_CreateAndGetRoundTrip()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Facilitator.dll");
        Assert.True(File.Exists(assembly), $"compiled Facilitator.dll missing at {assembly}");

        var port = GetFreePort();
        var startInfo = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__Facilitator"] = fixture.ConnectionString;
        startInfo.Environment["Database__AutoMigrate"] = "true"; // advisory-lock serialized, idempotent
        startInfo.Environment["Identity__Oidc__Authority"] = KeycloakFixture.Issuer;
        startInfo.Environment["Identity__Oidc__Audience"] = "facilitator-api";
        startInfo.Environment["Identity__Oidc__RequireHttps"] = "false";

        using var server = Process.Start(startInfo)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using (var probe = new HttpClient())
            {
                probe.BaseAddress = new Uri($"http://127.0.0.1:{port}");
                await KernelOps.PollUntilAsync(async () =>
                {
                    try
                    {
                        return (await probe.GetAsync("/health", cts.Token)).StatusCode == HttpStatusCode.OK;
                    }
                    catch (HttpRequestException)
                    {
                        return false;
                    }
                }, "server process answers /health", 30_000);
            }

            var (createCode, createBody) = await RunCliAsync(assembly, port,
                ["work", "create", "--objective", "cli-smoke"], cts.Token);
            Assert.Equal(0, createCode);
            var created = JsonDocument.Parse(createBody).RootElement;
            var workId = created.GetProperty("workId").GetGuid();
            Assert.Equal("open", created.GetProperty("state").GetString());
            Assert.Equal(1, created.GetProperty("version").GetInt64());

            var (getCode, getBody) = await RunCliAsync(assembly, port,
                ["work", "get", workId.ToString()], cts.Token);
            Assert.Equal(0, getCode);
            var fetched = JsonDocument.Parse(getBody).RootElement;
            Assert.Equal(workId, fetched.GetProperty("workId").GetGuid());
            Assert.Equal("cli-smoke", fetched.GetProperty("objective").GetString());
        }
        finally
        {
            try
            {
                server.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(string assembly, int port, string[] args, CancellationToken ct)
    {
        var argv = new List<string> { "--url", $"http://127.0.0.1:{port}", "--token", KeycloakFixture.CliToken };
        argv.AddRange(args);
        var startInfo = new ProcessStartInfo("dotnet", $"\"{assembly}\" cli {string.Join(' ', argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
