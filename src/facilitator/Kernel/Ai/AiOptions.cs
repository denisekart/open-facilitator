using Microsoft.Extensions.Configuration;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// One named AI endpoint (LiteLLM deployment): base addressing + model name +
/// secret + per-call timeout. Bound from <c>Ai:Endpoints:&lt;name&gt;</c>.
/// </summary>
public sealed class AiEndpointOptions
{
    /// <summary>Proxy base URL (no trailing slash needed; the gateway appends the path).</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Model name in the LiteLLM <c>model_list</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Bearer secret for the proxy (file/env only, never DB).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Per-call deadline ceiling; the CTS also carries a 10 s floor connect budget.</summary>
    public int TimeoutSeconds { get; init; } = 20;
}

/// <summary>
/// AiDeck file config (<c>Ai:</c> section): named endpoints + optional prompt
/// template directory override. Absent section ⇒ AiDeck inert everywhere (AI
/// is optional by spec — no boot error). A present endpoint without
/// <c>BaseUrl</c>/<c>Model</c> fails composition closed (Storage/Memory/
/// Provider precedent). Secrets stay in file/env, never DB.
/// </summary>
public sealed class AiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ai";

    /// <summary>Named endpoints (e.g. <c>decoder</c>, <c>decision</c>); empty when the section is absent.</summary>
    public Dictionary<string, AiEndpointOptions> Endpoints { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory overriding embedded templates per-template; null = embedded only.</summary>
    public string? TemplateDirectory { get; init; }

    /// <summary>True when the <c>Ai</c> section carried at least one endpoint.</summary>
    public bool HasEndpoints => Endpoints.Count > 0;

    /// <summary>Endpoint by name (case-insensitive), or null.</summary>
    public AiEndpointOptions? Endpoint(string name) =>
        Endpoints.TryGetValue(name, out var endpoint) ? endpoint : null;

    /// <summary>Load + fail-closed validate. A missing section yields an inert
    /// (endpoint-less) instance; a half-addressed endpoint throws.</summary>
    public static AiOptions Load(IConfiguration configuration)
    {
        var bound = configuration.GetSection(SectionName).Get<AiOptions>();
        if (bound is null || bound.Endpoints.Count == 0)
        {
            return new AiOptions();
        }

        foreach (var (name, endpoint) in bound.Endpoints)
        {
            if (string.IsNullOrEmpty(endpoint.BaseUrl) || string.IsNullOrEmpty(endpoint.Model))
            {
                throw new InvalidOperationException(
                    $"Ai:Endpoints:{name} configured but BaseUrl/Model unset — fail-closed.");
            }
        }

        return bound;
    }
}
