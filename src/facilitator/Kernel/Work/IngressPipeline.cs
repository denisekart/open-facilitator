using System.Diagnostics;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// Shared plumbing for the inline ingress paths:
/// span/metrics wrapping, inbox settle, and the ONE steer-result switch every
/// caller of <c>ApplySteerAsync</c> must answer identically.
/// </summary>
public sealed class IngressPipeline(IngressStore inbox, KernelMetrics metrics)
{
    /// <summary>Run <paramref name="action"/> inside an <c>ingress.process</c> span; record latency.</summary>
    public async Task<T> WithSpanAsync<T>(string name, Func<Task<T>> action)
    {
        using var span = KernelActivity.Source.StartActivity(name, ActivityKind.Internal);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            metrics.IngressLatency(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Settle the inbox row (processed/failed) and produce the response.</summary>
    public async Task<IResult> SettleAsync(Guid inboxId, string? error, CancellationToken ct, Func<IResult> response)
    {
        await inbox.MarkProcessedAsync(inboxId, error, ct);
        metrics.InboxProcessed();
        return response();
    }

    /// <summary>
    /// THE steer-result switch: every arm settles the inbox row with the
    /// matching error tag and answers the caller-shaped success/duplicate
    /// response; failures answer typed problems.
    /// </summary>
    public Task<IResult> HandleSteerResultAsync(
        SteerResult steer,
        Guid inboxId,
        Guid workId,
        Func<WorkView, IResult> onOk,
        Func<IResult> onDuplicate,
        CancellationToken ct)
    {
        (string?, Func<IResult>) arm = steer switch
        {
            SteerResult.Ok ok => (null, () => onOk(ok.Work)),
            SteerResult.Conflict conflict => ("version-conflict", () => Problem(ProblemErrors.VersionConflict(conflict.CurrentVersion))),
            SteerResult.Terminal => ("terminal-state", () => Problem(ProblemErrors.InvalidTransition("terminal→steer"))),
            SteerResult.Duplicate => (null, onDuplicate),
            _ => ("work-not-found", () => Problem(ProblemErrors.WorkNotFound(workId))),
        };
        return SettleAsync(inboxId, arm.Item1, ct, arm.Item2);
    }

    /// <summary>RFC 9457 problem result.</summary>
    internal static IResult Problem(ProblemDetails problem) => Results.Problem(problem);
}
