namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// Strongly-typed binding of the <c>identity:</c> configuration section
/// (spec §11.2): OIDC token validation, claim-pointer normalization, the
/// open role-rule dictionary, and the operation gates. Vendor-neutral — the
/// kernel speaks OIDC and RFC 6901 only. <see cref="Validate"/> implements
/// the staged fail-closed boot rules; any error refuses startup.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>Config section name (<c>identity:</c>).</summary>
    public const string SectionName = "identity";

    /// <summary>Oidc: discovery + issuer/audience validation settings.</summary>
    public OidcSection Oidc { get; set; } = new();

    /// <summary>Claims: principal normalization pointers (spec §11.2 verbatim).</summary>
    public ClaimsSection Claims { get; set; } = new();

    /// <summary>Roles: role name -> OR-list of rules; a rule is an AND of conditions.</summary>
    public Dictionary<string, List<RoleRuleConfig>> Roles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Authorization: operation gates (method + path -> required roles).</summary>
    public AuthorizationSection Authorization { get; set; } = new();

    /// <summary>Seconds between role-store refreshes from the DB (default 5).</summary>
    public int RolesRefreshSeconds { get; set; } = 5;

    /// <summary>Staged validation; every returned string is a boot-blocking error.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        // Stage 1: OIDC endpoints.
        if (string.IsNullOrWhiteSpace(Oidc.Authority))
        {
            errors.Add("identity:oidc:authority is required.");
        }
        else if (!Uri.TryCreate(Oidc.Authority, UriKind.Absolute, out var authority)
                 || (authority.Scheme != Uri.UriSchemeHttps && !(authority.Scheme == Uri.UriSchemeHttp && !Oidc.RequireHttps)))
        {
            errors.Add($"identity:oidc:authority '{Oidc.Authority}' must be an absolute https URI (http only with identity:oidc:requireHttps=false).");
        }

        foreach (var issuer in Oidc.TrustedIssuers)
        {
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out _))
            {
                errors.Add($"identity:oidc:trustedIssuers entry '{issuer}' is not an absolute URI.");
            }
        }

        if (string.IsNullOrWhiteSpace(Oidc.Audience))
        {
            errors.Add("identity:oidc:audience is required (non-empty).");
        }

        // Stage 2: claim pointers (RFC 6901) + subject presence.
        if (string.IsNullOrWhiteSpace(Claims.Subject))
        {
            errors.Add("identity:claims:subject is required (e.g. \"/sub\").");
        }
        else if (!ClaimPointers.IsValidPointer(Claims.Subject))
        {
            errors.Add($"identity:claims:subject '{Claims.Subject}' is not a valid RFC 6901 pointer.");
        }

        foreach (var (name, descriptor) in EnumerateDescriptors())
        {
            if (!ClaimPointers.IsValidPointer(descriptor.Path))
            {
                errors.Add($"identity:claims:{name} path '{descriptor.Path}' is not a valid RFC 6901 pointer.");
            }

            if (!ClaimShape.IsKnown(descriptor.Shape))
            {
                errors.Add($"identity:claims:{name} shape '{descriptor.Shape}' is unknown (string-array | space-delimited-string | string-array-or-space-delimited).");
            }
        }

        if (!string.IsNullOrWhiteSpace(Claims.AuthenticationContext) && !ClaimPointers.IsValidPointer(Claims.AuthenticationContext))
        {
            errors.Add($"identity:claims:authenticationContext '{Claims.AuthenticationContext}' is not a valid RFC 6901 pointer.");
        }

        // Stage 3: role rules.
        foreach (var (role, rules) in Roles)
        {
            if (rules is null || rules.Count == 0)
            {
                errors.Add($"identity:roles:{role} has no rules.");
            }

            var ruleIndex = 0;
            foreach (var rule in rules ?? [])
            {
                ruleIndex++;
                if (rule.All is null || rule.All.Count == 0)
                {
                    errors.Add($"identity:roles:{role}[{ruleIndex}] has no conditions.");
                }

                foreach (var condition in rule.All ?? [])
                {
                    if (!ClaimPointers.IsValidPointer(condition.Claim))
                    {
                        errors.Add($"identity:roles:{role}[{ruleIndex}] claim pointer '{condition.Claim}' is not a valid RFC 6901 pointer.");
                    }

                    if (!RoleConditionOps.IsKnown(condition.Op))
                    {
                        errors.Add($"identity:roles:{role}[{ruleIndex}] op '{condition.Op}' is unknown (equals | contains | exists).");
                    }
                    else if (condition.Op != "exists" && string.IsNullOrEmpty(condition.Value))
                    {
                        errors.Add($"identity:roles:{role}[{ruleIndex}] op '{condition.Op}' requires a value.");
                    }
                }
            }
        }

        // Stage 4: operation bindings.
        foreach (var operation in Authorization.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Id))
            {
                errors.Add("identity:authorization:operations entry with empty id.");
            }

            if (operation.Methods.Count == 0)
            {
                errors.Add($"identity:authorization:operations:{operation.Id} needs non-empty methods.");
            }

            if (operation.Paths.Count == 0)
            {
                errors.Add($"identity:authorization:operations:{operation.Id} needs non-empty paths.");
            }

            if (operation.RolesAnyOf.Count == 0)
            {
                errors.Add($"identity:authorization:operations:{operation.Id} needs non-empty rolesAnyOf.");
            }

            foreach (var path in operation.Paths)
            {
                if (!path.StartsWith('/'))
                {
                    errors.Add($"identity:authorization:operations:{operation.Id} path '{path}' must be route-template absolute.");
                }
            }

            foreach (var role in operation.RolesAnyOf)
            {
                if (!Roles.ContainsKey(role))
                {
                    errors.Add($"identity:authorization:operations:{operation.Id} requires unknown role '{role}'.");
                }
            }
        }

        if (RolesRefreshSeconds < 1)
        {
            errors.Add("identity:rolesRefreshSeconds must be >= 1.");
        }

        return errors;
    }

    private IEnumerable<(string Name, ClaimDescriptor Descriptor)> EnumerateDescriptors()
    {
        foreach (var g in Claims.Groups)
        {
            yield return ("groups", g);
        }

        foreach (var s in Claims.Scopes)
        {
            yield return ("scopes", s);
        }

        foreach (var p in Claims.Permissions)
        {
            yield return ("permissions", p);
        }

        foreach (var a in Claims.AuthenticationMethods)
        {
            yield return ("authenticationMethods", a);
        }
    }
}

/// <summary>identity:oidc — token validation endpoints.</summary>
public sealed class OidcSection
{
    /// <summary>OIDC issuer/authority: discovery URL source and the expected <c>iss</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Extra accepted <c>iss</c> values (direct external IdP federation).</summary>
    public List<string> TrustedIssuers { get; set; } = [];

    /// <summary>Required <c>aud</c> value.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Enforce https on the authority (default true).</summary>
    public bool RequireHttps { get; set; } = true;
}

/// <summary>identity:claims — principal normalization (spec §11.2 block).</summary>
public sealed class ClaimsSection
{
    /// <summary>Pointer to the subject identifier (required).</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Pointer to the display name (display-only).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Pointers feeding <c>Groups</c>.</summary>
    public List<ClaimDescriptor> Groups { get; set; } = [];

    /// <summary>Pointers feeding <c>Scopes</c>.</summary>
    public List<ClaimDescriptor> Scopes { get; set; } = [];

    /// <summary>Pointers feeding <c>Permissions</c>.</summary>
    public List<ClaimDescriptor> Permissions { get; set; } = [];

    /// <summary>Pointers feeding <c>Amr</c>.</summary>
    public List<ClaimDescriptor> AuthenticationMethods { get; set; } = [];

    /// <summary>Pointer to the authentication context class (<c>acr</c>).</summary>
    public string? AuthenticationContext { get; set; }
}

/// <summary>One claim source: RFC 6901 path + value shape.</summary>
public sealed class ClaimDescriptor
{
    /// <summary>RFC 6901 pointer into the token payload.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Value encoding (see <see cref="ClaimShape"/>).</summary>
    public string Shape { get; set; } = ClaimShape.StringArray;
}

/// <summary>identity:roles:&lt;name&gt; element — one OR-alternative (AND of conditions).</summary>
public sealed class RoleRuleConfig
{
    /// <summary>AND-ed conditions; the rule passes iff all pass.</summary>
    public List<RoleConditionConfig> All { get; set; } = [];
}

/// <summary>One claim-requirement condition.</summary>
public sealed class RoleConditionConfig
{
    /// <summary>RFC 6901 pointer into the token payload (may address ANY claim).</summary>
    public string Claim { get; set; } = string.Empty;

    /// <summary>Operator: equals | contains | exists.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>Comparison value (absent only for exists).</summary>
    public string? Value { get; set; }
}

/// <summary>identity:authorization — the operation gate table.</summary>
public sealed class AuthorizationSection
{
    /// <summary>Route/method -> required role bindings.</summary>
    public List<OperationBinding> Operations { get; set; } = [];
}

/// <summary>One gated operation: methods x route-template paths -> any-of roles.</summary>
public sealed class OperationBinding
{
    /// <summary>Stable operation id (audit <c>action</c> component).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>HTTP methods (e.g. POST).</summary>
    public List<string> Methods { get; set; } = [];

    /// <summary>Route templates, e.g. <c>/api/works/{id}/steer</c>.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Principal passes iff it holds at least one of these roles.</summary>
    public List<string> RolesAnyOf { get; set; } = [];
}

/// <summary>The three claim value shapes (spec §11.2).</summary>
public static class ClaimShape
{
    /// <summary>JSON array of strings.</summary>
    public const string StringArray = "string-array";

    /// <summary>One space-delimited string (e.g. OAuth <c>scope</c>).</summary>
    public const string SpaceDelimited = "space-delimited-string";

    /// <summary>Either of the two above, auto-detected.</summary>
    public const string StringArrayOrSpaceDelimited = "string-array-or-space-delimited";

    /// <summary>Known shape?</summary>
    public static bool IsKnown(string shape) =>
        shape is StringArray or SpaceDelimited or StringArrayOrSpaceDelimited;
}

/// <summary>The three condition operators.</summary>
public static class RoleConditionOps
{
    /// <summary>Resolved JSON string equals value.</summary>
    public const string EqualsOp = "equals";

    /// <summary>String-array contains value, or space-delimited string contains the token.</summary>
    public const string ContainsOp = "contains";

    /// <summary>Pointer resolves (to any JSON value).</summary>
    public const string ExistsOp = "exists";

    /// <summary>Known operator?</summary>
    public static bool IsKnown(string op) =>
        op is EqualsOp or ContainsOp or ExistsOp;
}
