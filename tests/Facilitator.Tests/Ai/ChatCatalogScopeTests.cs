using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Facilitator.Kernel.Ai;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// <c>GET /api/chat/catalog</c>: the principal's slice of the action catalog.
/// A team-scoped ai-operator (no admin roles) must NOT be offered the admin
/// mutations its own role cannot run; a framework-admin ai-operator sees the
/// whole list. Verbs mirror the command registry.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ChatCatalogScopeTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task CatalogIsFilteredToTheCallersRoles()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);

        // minty: ai-operator + team-beta only (no kernel-operator, no framework-admin).
        using var minty = await UserAsync(factory, "minty");
        var scoped = await KernelOps.GetJsonAsync(minty, "/api/chat/catalog", HttpStatusCode.OK);
        var scopedPaths = scoped.GetProperty("catalog").EnumerateArray()
            .Select(e => $"{e.GetProperty("method").GetString()} {e.GetProperty("path").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("POST /api/admin/dead-letters/{deadLetterId}/replay", scopedPaths);
        Assert.DoesNotContain("PATCH /api/admin/config/{resource}", scopedPaths);
        // No binding on GET /api/works/{id}: any authenticated principal passes.
        Assert.Contains("GET /api/works/{id}", scopedPaths);
        // Bound to kernel-operator only: minty is not offered the work-write.
        Assert.DoesNotContain("POST /api/works", scopedPaths);

        // alice: framework-admin + ai-operator holds the admin mutations too.
        using var alice = await UserAsync(factory, "alice");
        var wide = await KernelOps.GetJsonAsync(alice, "/api/chat/catalog", HttpStatusCode.OK);
        var widePaths = wide.GetProperty("catalog").EnumerateArray()
            .Select(e => $"{e.GetProperty("method").GetString()} {e.GetProperty("path").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("POST /api/admin/dead-letters/{deadLetterId}/replay", widePaths);
        Assert.Contains("PATCH /api/admin/config/{resource}", widePaths);

        // Effect labels: GET is a read, everything else a write.
        foreach (var entry in wide.GetProperty("catalog").EnumerateArray())
        {
            var expected = entry.GetProperty("method").GetString() == "GET" ? "read" : "write";
            Assert.Equal(expected, entry.GetProperty("effect").GetString());
        }

        // Verbs = the registry's own answer.
        var verbs = wide.GetProperty("verbs").EnumerateArray()
            .Select(v => v.GetProperty("verb").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            CommandRegistry.BuiltIns.All().Select(s => (string?)s.Verb).ToHashSet(StringComparer.Ordinal),
            verbs);
    }

    private static async Task<HttpClient> UserAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await KeycloakFixture.TokenForUserAsync(user, user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
