namespace Facilitator.Kernel.Ai;

/// <summary>
/// Result of parsing a mention-stripped message against the
/// <see cref="CommandRegistry"/>.
/// </summary>
/// <param name="Verb">First token minus the leading '/' (empty for a bare '/').</param>
/// <param name="Args">Values mapped positionally onto the verb's declared args (empty for unknown verbs).</param>
/// <param name="Rest">Everything after the consumed positional args — the free-text payload (objective, etc.).</param>
/// <param name="Unknown">True when the verb is outside the registry (reply: "unknown command, try /help").</param>
public sealed record ParsedCommand(string Verb, IReadOnlyDictionary<string, string> Args, string Rest, bool Unknown);

/// <summary>
/// Deterministic slash-command parser — works with zero AI. Pure function over
/// the registry; no I/O. The <c>/link</c> pre-check stays caller-side
/// (ProviderEventStore handles link before ever calling here), but
/// <c>link</c> is a registry verb so a caller may parse it if it wants to.
/// </summary>
public static class CommandParser
{
    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Parse <paramref name="text"/> (already mention-stripped). Null when the
    /// first token does not start with '/'. On a registry hit, the following
    /// tokens fill the declared args positionally (a missing optional arg is
    /// simply absent); everything after the consumed args becomes
    /// <see cref="ParsedCommand.Rest"/>, which is the whole remainder when the
    /// verb declares no args. On a registry miss (including a bare '/') the
    /// result carries <see cref="ParsedCommand.Unknown"/> so the caller can
    /// reply deterministically without consulting the AI.
    /// </summary>
    public static ParsedCommand? TryParse(string text, CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var trimmed = text.TrimStart(Whitespace);
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var breakIndex = trimmed.IndexOfAny(Whitespace);
        var token = breakIndex < 0 ? trimmed : trimmed[..breakIndex];
        var verb = token[1..];
        var remainder = breakIndex < 0 ? string.Empty : trimmed[(breakIndex + 1)..].Trim(Whitespace);

        if (!registry.TryGet(verb, out var spec))
        {
            return new ParsedCommand(verb, NoArgs, remainder, true);
        }

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var rest = remainder;
        foreach (var arg in spec.Args)
        {
            if (rest.Length == 0)
            {
                break;
            }

            var next = rest.IndexOfAny(Whitespace);
            args[arg.Name] = next < 0 ? rest : rest[..next];
            rest = next < 0 ? string.Empty : rest[(next + 1)..].Trim(Whitespace);
        }

        return new ParsedCommand(verb, args, rest, false);
    }
}
