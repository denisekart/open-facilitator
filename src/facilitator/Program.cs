using Facilitator.Kernel;
using Facilitator.Kernel.Admin;
using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Cli;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

if (args.Length > 0 && args[0] == "cli")
{
    return await CliRunner.RunAsync(args[1..], CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);
// CLI default URL (dev without launchSettings): the kernel API lives on 8088.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
{
    builder.WebHost.UseUrls("http://localhost:8088");
}

builder.AddKernel();

var app = builder.Build();

await app.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.UseMiddleware<LinkAuthMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<IngressRoleGateMiddleware>();
app.UseRateLimiter();
app.MapWorkApi();
app.MapApprovalsApi();
app.MapArtifactsApi();
app.MapResultManifestsApi();
app.MapMemoryApi();
app.MapLinkApi(app.Environment);
app.MapIdentityApi();
app.MapAdminApi();
app.MapProviderIdentitiesApi();
app.MapUiConfig(builder.Configuration);
// Embedded operator SPA: hashed assets + SPA fallback. Non-/api
// paths ride past the role gate, so the static surface stays anonymous.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();

return 0;

public partial class Program;
