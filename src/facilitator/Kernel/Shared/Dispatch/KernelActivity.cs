using System.Diagnostics;

namespace Facilitator.Kernel.Dispatch;

/// <summary>Activity source for kernel spans (<c>Facilitator.Kernel</c>).</summary>
public static class KernelActivity
{
    /// <summary>Source name (locked contract).</summary>
    public const string SourceName = "Facilitator.Kernel";

    /// <summary>The kernel activity source.</summary>
    public static readonly ActivitySource Source = new(SourceName);
}
