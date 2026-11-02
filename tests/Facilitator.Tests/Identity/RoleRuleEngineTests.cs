using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;

using Microsoft.Extensions.Configuration;

namespace Facilitator.Tests.Identity;

/// <summary>
/// Golden cases for the framework-free core: role = OR-list
/// of AND-rules over arbitrary claim pointers; malformed/missing ⇒ no roles,
/// never wildcard.
/// </summary>
public sealed class RoleRuleEngineTests
{
    [Fact]
    public void KeycloakShapes_RealmRolesAndGroups_GrantConfiguredRoles()
    {
        var payload = Json("""
        {
          "sub": "b6f2-uuid",
          "realm_access": { "roles": ["e2e-operator", "offline_access"] },
          "groups": ["/teams/team-beta"],
          "scope": "profile email"
        }
        """);

        var sets = Sets(("kernel-operator", """[{"all":[{"claim":"/realm_access/roles","op":"contains","value":"e2e-operator"}]}]"""),
                        ("team-beta-member", """[{"all":[{"claim":"/groups","op":"contains","value":"/teams/team-beta"}]}]"""),
                        ("scope-holder", """[{"all":[{"claim":"/scope","op":"contains","value":"email"}]}]"""));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Equal(3, outcome.Granted.Count);
        Assert.Contains("kernel-operator", outcome.Granted);
        Assert.Contains("team-beta-member", outcome.Granted);
        Assert.Contains("scope-holder", outcome.Granted);
        Assert.Equal(0, outcome.SkippedRules);
    }

    [Fact]
    public void GoogleShapes_SubjectEqualsAndAmrContains_GrantRole()
    {
        // Tokens from an external IdP with NO group claims: identity via the
        // verified (iss, sub) pair + amr — the spec §11.2 operator use-case.
        var payload = Json("""
        {
          "iss": "https://accounts.google.com",
          "sub": "113044181234567890098",
          "email": "me@gmail.com",
          "amr": ["pwd", "mfa"]
        }
        """);

        var sets = Sets(("approver", """
        [{"all":[{"claim":"/email","op":"equals","value":"me@gmail.com"},{"claim":"/amr","op":"contains","value":"mfa"}]},
         {"all":[{"claim":"/sub","op":"equals","value":"113044181234567890098"}]}]
        """));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Equal(new[] { "approver" }, outcome.Granted);
    }

    [Fact]
    public void WrongPointer_GrantsNothing()
    {
        var payload = Json("""{ "sub": "x", "realm_access": { "roles": ["kernel-operator"] } }""");

        var sets = Sets(("kernel-operator", """[{"all":[{"claim":"/nonexistent_roles","op":"contains","value":"kernel-operator"}]}]"""));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Empty(outcome.Granted);
    }

    [Fact]
    public void MalformedRule_IsSkippedNotWildcard()
    {
        var payload = Json("""{ "sub": "x" }""");

        // empty rule set, condition-less rule, and unknown op all deny.
        var sets = new[]
        {
            new RoleRuleSet("empty", []),
            new RoleRuleSet("no-conditions", [new RoleRule([])]),
            new RoleRuleSet("bad-op", [new RoleRule([new RoleCondition("/sub", "regex", "x")])]),
        };

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Empty(outcome.Granted);
        Assert.Equal(2, outcome.SkippedRules);
    }

    [Fact]
    public void Exists_ResolvesAnyJsonKind()
    {
        var payload = Json("""{ "nested": { "flag": false }, "zero": 0 }""");

        var sets = Sets(
            ("flagist", """[{"all":[{"claim":"/nested/flag","op":"exists"}]}]"""),
            ("zeros", """[{"all":[{"claim":"/zero","op":"exists"}]}]"""),
            ("absent", """[{"all":[{"claim":"/nested/nope","op":"exists"}]}]"""));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Equal(["flagist", "zeros"], outcome.Granted.OrderBy(static r => r, StringComparer.Ordinal));
        Assert.DoesNotContain("absent", outcome.Granted);
    }

    [Fact]
    public void Equals_NonStringLeafDenies()
    {
        var payload = Json("""{ "count": 5, "flag": true }""");

        var sets = Sets(
            ("numeric", """[{"all":[{"claim":"/count","op":"equals","value":"5"}]}]"""),
            ("boolean", """[{"all":[{"claim":"/flag","op":"equals","value":"true"}]}]"""));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        Assert.Empty(outcome.Granted);
    }

    [Fact]
    public void Contains_EmptyArrayDenies_BareStringIsSingleToken()
    {
        var payload = Json("""{ "roles": [], "scalar": "kernel-operator" }""");

        var sets = Sets(
            ("empty-arr", """[{"all":[{"claim":"/roles","op":"contains","value":"kernel-operator"}]}]"""),
            ("scalar", """[{"all":[{"claim":"/scalar","op":"contains","value":"kernel-operator"}]}]"""),
            ("scalar-multiword", """[{"all":[{"claim":"/scalar","op":"contains","value":"kernel"}]}]"""));

        var outcome = RoleEvaluator.Evaluate(payload, sets);

        // a bare string IS a valid space-delimited single-token value.
        Assert.Equal(["scalar"], outcome.Granted);
    }

    [Fact]
    public void MalformedJsonbDocument_ParsesToNull()
    {
        Assert.Null(RoleRuleJson.ParseRules("{ not json"));
        Assert.Null(RoleRuleJson.ParseRules("""{"object":"not an array"}"""));
    }

    [Fact]
    public void Pointer_EscapesAndArrayIndexes()
    {
        var payload = Json("""{ "a/b": { "c~d": ["zero", "one"] }, "~": "root-tilde-name" }""");

        Assert.True(ClaimPointers.TryResolve(payload, "/a~1b/c~0d/1", out var one));
        Assert.Equal("one", one.GetString());
        Assert.True(ClaimPointers.TryResolve(payload, "/~0", out var tilde));
        Assert.Equal("root-tilde-name", tilde.GetString());
        Assert.False(ClaimPointers.TryResolve(payload, "/a~1b/c~0d/01", out _));
        Assert.False(ClaimPointers.TryResolve(payload, "/a~1b/c~0d/2", out _));
        Assert.False(ClaimPointers.TryResolve(payload, "relative", out _));
        Assert.False(ClaimPointers.TryResolve(payload, string.Empty, out _));
    }

    [Fact]
    public void ConfigBinding_FullSchemaRoundTrips()
    {
        var json = """
        {
          "identity": {
            "oidc": { "authority": "https://idp.example.test/realms/facilitator", "audience": "facilitator-api", "trustedIssuers": ["https://accounts.google.com"] },
            "claims": {
              "subject": "/sub",
              "displayName": "/preferred_username",
              "groups": [{ "path": "/groups", "shape": "string-array" }],
              "scopes": [{ "path": "/scope", "shape": "space-delimited-string" }],
              "permissions": [{ "path": "/permissions", "shape": "string-array" }],
              "authenticationMethods": [{ "path": "/amr", "shape": "string-array" }],
              "authenticationContext": "/acr"
            },
            "roles": {
              "kernel-operator": [{ "all": [{ "claim": "/realm_access/roles", "op": "contains", "value": "kernel-operator" }] }],
              "approver": [{ "all": [{ "claim": "/email", "op":"equals","value":"me@gmail.com" }, { "claim": "/amr", "op":"contains","value":"mfa" }] }]
            },
            "authorization": {
              "operations": [{ "id": "work.write", "methods": ["POST"], "paths": ["/api/works"], "rolesAnyOf": ["kernel-operator"] }]
            }
          }
        }
        """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var options = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>()!;

        Assert.Empty(options.Validate());
        Assert.Equal("https://idp.example.test/realms/facilitator", options.Oidc.Authority);
        Assert.Equal(2, options.Roles.Count);
        Assert.Equal("/sub", options.Claims.Subject);
        Assert.Single(options.Authorization.Operations);
        Assert.Equal(5, options.RolesRefreshSeconds);
        Assert.NotNull(RoleRuleJson.FromConfig(options).FirstOrDefault(static s => s.Name == "kernel-operator"));
    }

    [Fact]
    public void Validation_RejectsEveryStagedDefect()
    {
        var options = new IdentityOptions();
        var errors = options.Validate();

        Assert.Contains(errors, static e => e.Contains("authority", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Contains("audience", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Contains("subject", StringComparison.Ordinal));

        options.Oidc.Authority = "not-a-uri";
        options.Oidc.Audience = "aud";
        options.Claims.Subject = "sub";
        options.Roles["r"] = [new RoleRuleConfig { All = [new RoleConditionConfig { Claim = "/x", Op = "wat", Value = "v" }] }];
        options.Authorization.Operations.Add(new OperationBinding { Id = "op", Methods = ["POST"], Paths = ["/api/x"], RolesAnyOf = ["ghost"] });

        errors = options.Validate();
        Assert.Contains(errors, static e => e.Contains("absolute https", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Contains("not a valid RFC 6901", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Contains("op 'wat' is unknown", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Contains("unknown role 'ghost'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_AcceptsHttpWhenRequireHttpsFalse()
    {
        var options = new IdentityOptions();
        options.Oidc.Authority = "http://localhost:8480/realms/facilitator";
        options.Oidc.RequireHttps = false;
        options.Oidc.Audience = "facilitator-api";
        options.Claims.Subject = "/sub";

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void PrincipalMapper_NormalizesSpecBlock()
    {
        var payload = Json("""
        {
          "sub": "alice-sub",
          "preferred_username": "alice",
          "groups": ["/teams/team-alpha"],
          "scope": "openid email",
          "scp": ["profile"],
          "amr": ["pwd"],
          "acr": "urn:acr:test"
        }
        """);
        var options = new IdentityOptions();
        options.Claims.Subject = "/sub";
        options.Claims.DisplayName = "/preferred_username";
        options.Claims.Groups = [new ClaimDescriptor { Path = "/groups" }];
        options.Claims.Scopes =
        [
            new ClaimDescriptor { Path = "/scope", Shape = ClaimShape.SpaceDelimited },
            new ClaimDescriptor { Path = "/scp", Shape = ClaimShape.StringArrayOrSpaceDelimited },
        ];
        options.Claims.AuthenticationMethods = [new ClaimDescriptor { Path = "/amr" }];
        options.Claims.AuthenticationContext = "/acr";

        var p = PrincipalMapper.Map(payload, "https://idp.test", options);

        Assert.Equal("oidc:https://idp.test|alice-sub", p.Actor);
        Assert.Equal("alice", p.DisplayName);
        Assert.Equal(["/teams/team-alpha"], p.Groups);
        Assert.Equal(["openid", "email", "profile"], p.Scopes);
        Assert.Equal(["pwd"], p.Amr);
        Assert.Equal("urn:acr:test", p.Acr);
        Assert.Empty(p.Roles);
    }

    [Fact]
    public void PointerValidator_RejectsRelativeAndEmpty()
    {
        Assert.True(ClaimPointers.IsValidPointer("/"));
        Assert.True(ClaimPointers.IsValidPointer("/a/b"));
        Assert.False(ClaimPointers.IsValidPointer(""));
        Assert.False(ClaimPointers.IsValidPointer("sub"));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static IReadOnlyList<RoleRuleSet> Sets(params (string Name, string RulesJson)[] entries) =>
        entries.Select(static e => new RoleRuleSet(e.Name, RoleRuleJson.ParseRules(e.RulesJson)
            ?? throw new InvalidOperationException("test fixture json: " + e.Name))).ToList();
}
