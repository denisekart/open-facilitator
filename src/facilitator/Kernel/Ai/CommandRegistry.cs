using System.Text.Json;
using System.Text.RegularExpressions;

namespace Facilitator.Kernel.Ai;

/// <summary>One declared positional argument of a command verb.</summary>
/// <param name="Name">Argument name (feeds the parse map + the decode prompt).</param>
/// <param name="Required">True when the applier must reject the command without a value.</param>
public sealed record CommandArg(string Name, bool Required);

/// <summary>One command verb: what it does, how it routes, what it takes.</summary>
/// <param name="Verb">Lowercase verb, no leading slash (<c>[a-z0-9-]+</c>).</param>
/// <param name="Description">Human one-liner — feeds <c>/help</c> AND the decode prompt's command list.</param>
/// <param name="CapabilityId">Routing seed for Work-creating verbs; null for built-ins.</param>
/// <param name="Args">Declared positional arguments, in order.</param>
/// <param name="BuiltIn">True for the code-defined verbs (cannot be deleted by config; description may be overridden).</param>
public sealed record CommandSpec(string Verb, string Description, string? CapabilityId, IReadOnlyList<CommandArg> Args, bool BuiltIn);

/// <summary>
/// The single source of truth for command verbs, consumed twice: the
/// deterministic slash parser AND the LLM's command list rendered into the
/// decode prompt — the AI is a second parser over one registry. Built-ins are
/// code-defined and cannot be deleted; the <c>admin_config</c> <c>commands</c>
/// row merges in custom verbs (and may override a built-in's description,
/// capability, and argument list) via <see cref="Merge"/>.
/// </summary>
public sealed class CommandRegistry
{
    // Verb charset shared with the admin_config `commands` validator.
    internal static readonly Regex VerbPattern = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private readonly Dictionary<string, CommandSpec> byVerb;
    private readonly List<CommandSpec> ordered;

    private CommandRegistry(IEnumerable<CommandSpec> specs)
    {
        byVerb = new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase);
        ordered = [];
        foreach (var spec in specs)
        {
            byVerb[spec.Verb] = spec;
            ordered.Add(spec);
        }
    }

    /// <summary>The code-defined verbs — always present, never deletable.</summary>
    public static CommandRegistry BuiltIns { get; } = new(
    [
        new CommandSpec("help", "List the available commands.", null, [], true),
        new CommandSpec("work", "Create a Work item; the free-text remainder becomes its objective.", null, [], true),
        new CommandSpec("status", "Show the state of the Work bound to this conversation (or the given work-id).", null, [new CommandArg("work-id", false)], true),
        new CommandSpec("cancel", "Cancel the Work bound to this conversation (or the given work-id).", null, [new CommandArg("work-id", false)], true),
        new CommandSpec("approve", "Accept or reject an approval: /approve &lt;approval-id&gt; &lt;accept|reject&gt;.", null, [new CommandArg("approval-id", true), new CommandArg("decision", true)], true),
        new CommandSpec("link", "Link this conversation to a Work item (behavior is provider-plane code, not the parser).", null, [], true),
    ]);

    /// <summary>
    /// Built-ins merged with the <c>admin_config</c> <c>commands</c> row
    /// payload <c>{"commands":[{"verb","description","capabilityId","args":[{"name","required"}]}]}</c>.
    /// A custom entry whose verb equals a built-in overrides its description
    /// (and, when present, its capability/args) but never removes it. Any
    /// malformed shape (non-object, bad verb charset, empty description,
    /// duplicate verb, junk args) skips that entry; a wholly malformed
    /// payload yields built-ins only — the registry never fails closed on AI-
    /// optional config.
    /// </summary>
    /// <param name="builtins">Base registry (normally <see cref="BuiltIns"/>).</param>
    /// <param name="customRowPayload">Stored <c>commands</c> payload, or null when the row is absent.</param>
    public static CommandRegistry Merge(CommandRegistry builtins, JsonElement? customRowPayload)
    {
        if (customRowPayload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return builtins;
        }

        var custom = new List<CommandSpec>();
        foreach (var entry in commands.EnumerateArray())
        {
            var spec = TryReadCustom(entry);
            if (spec is not null && !Contains(custom, spec.Verb))
            {
                custom.Add(spec);
            }
        }

        if (custom.Count == 0)
        {
            return builtins;
        }

        var merged = new List<CommandSpec>(builtins.ordered);
        foreach (var spec in custom)
        {
            var index = merged.FindIndex(s => string.Equals(s.Verb, spec.Verb, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(spec);
            }
            else
            {
                var builtin = merged[index];
                merged[index] = builtin with
                {
                    Description = spec.Description,
                    CapabilityId = spec.CapabilityId ?? builtin.CapabilityId,
                    Args = spec.Args,
                };
            }
        }

        return new CommandRegistry(merged);
    }

    /// <summary>All specs: built-ins first (declaration order), then custom verbs.</summary>
    public IReadOnlyList<CommandSpec> All() => ordered;

    /// <summary>Spec by verb (leading '/' tolerated, case-insensitive), or false.</summary>
    public bool TryGet(string verb, out CommandSpec spec)
    {
        var key = verb.StartsWith("/", StringComparison.Ordinal) ? verb[1..] : verb;
        return byVerb.TryGetValue(key, out spec!);
    }

    private static bool Contains(List<CommandSpec> specs, string verb) =>
        specs.Exists(s => string.Equals(s.Verb, verb, StringComparison.OrdinalIgnoreCase));

    private static CommandSpec? TryReadCustom(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || entry.GetProperty("verb") is not { ValueKind: JsonValueKind.String } verb
            || verb.GetString() is not { } verbText
            || !VerbPattern.IsMatch(verbText)
            || entry.GetProperty("description") is not { ValueKind: JsonValueKind.String } description
            || string.IsNullOrEmpty(description.GetString()))
        {
            return null;
        }

        string? capabilityId = null;
        if (entry.TryGetProperty("capabilityId", out var capability) && capability.ValueKind == JsonValueKind.String)
        {
            capabilityId = capability.GetString();
        }

        var args = new List<CommandArg>();
        if (entry.TryGetProperty("args", out var argsArray))
        {
            if (argsArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var arg in argsArray.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.Object
                    || !arg.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(name.GetString())
                    || !arg.TryGetProperty("required", out var required)
                    || (required.ValueKind != JsonValueKind.True && required.ValueKind != JsonValueKind.False))
                {
                    return null;
                }

                args.Add(new CommandArg(name.GetString()!, required.ValueKind == JsonValueKind.True));
            }
        }

        return new CommandSpec(verbText, description.GetString()!, capabilityId, args, false);
    }
}
