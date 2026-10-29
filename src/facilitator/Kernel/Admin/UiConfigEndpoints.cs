namespace Facilitator.Kernel.Admin;

/// <summary>
/// <c>GET /ui-config</c> — the SPA bootstrap document (oidc-client-ts needs
/// the authority + client id before it can sign anyone in). Intentionally
/// anonymous and OUTSIDE <c>/api/*</c>: the role gate guards only
/// <c>/api/*</c>, so this route rides past it without a new authz binding.
/// </summary>
public static class UiConfigEndpoints
{
    /// <summary>Map the bootstrap route (mount after <c>MapAdminApi</c>).</summary>
    public static IEndpointRouteBuilder MapUiConfig(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        app.MapGet("/ui-config", () => Results.Ok(new
        {
            oidcAuthority = configuration["ui:oidcAuthority"] ?? string.Empty,
            oidcClientId = configuration["ui:oidcClientId"] ?? "facilitator-api",
        }));
        return app;
    }
}
