using System.Text;

using Facilitator.Kernel.Shared.Identity;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The bounded read phase of a chat turn (7c): the model may ask for a few
/// GETs — drawn ONLY from the principal's own catalog slice, so no read can
/// cross a role line — executed as loopback calls under that principal's own
/// token through <see cref="ChatActionExecutor.ExecuteReadAsync"/>. The gate
/// audits each call; no invented endpoints, a hard count cap, a hard
/// per-body size cap. Writes are untouched: propose-then-confirm.
/// </summary>
public sealed class ChatReadLoop(ChatActionExecutor executor, int limit, int maxBodyChars)
{
    /// <summary>The read-phase default body trim (4000 chars).</summary>
    public const int DefaultMaxBodyChars = 4000;

    /// <summary>One executed read: what was asked, what answered.</summary>
    public sealed record ReadOutcome(string Method, string Path, int Status, string Body);

    /// <summary>
    /// Drops reads outside <paramref name="allowed"/> (the model may not
    /// invent), caps at the configured limit, executes the rest. Returns
    /// executed reads in request order (empty when nothing usable survived).
    /// </summary>
    public async Task<IReadOnlyList<ReadOutcome>> RunAsync(
        IReadOnlyList<(string Method, string Path)> reads,
        IReadOnlyList<CatalogEntry> allowed,
        FacilitatorPrincipal principal,
        HttpRequest request,
        CancellationToken ct)
    {
        if (limit <= 0)
        {
            return [];
        }

        var usable = reads
            .Where(r => string.Equals(r.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && allowed.Any(e => e.Matches(r.Method, r.Path)))
            .Take(limit)
            .ToList();
        var outcomes = new List<ReadOutcome>(usable.Count);
        foreach (var (method, path) in usable)
        {
            var (status, body) = await executor.ExecuteReadAsync(method, path, principal, request, maxBodyChars, ct);
            outcomes.Add(new ReadOutcome(method, path, status, body));
        }

        return outcomes;
    }

    /// <summary>The READ RESULTS block appended to the second model call.</summary>
    public static string RenderResults(IReadOnlyList<ReadOutcome> outcomes)
    {
        var text = new StringBuilder("READ RESULTS");
        foreach (var outcome in outcomes)
        {
            text.Append('\n').Append(outcome.Method).Append(' ').Append(outcome.Path)
                .Append(" -> ").Append(outcome.Status).Append('\n').Append(outcome.Body);
        }

        return text.ToString();
    }
}
