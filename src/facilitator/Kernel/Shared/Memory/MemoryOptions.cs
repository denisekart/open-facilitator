namespace Facilitator.Kernel.Shared.Memory;

/// <summary>
/// Memory-broker configuration (<c>Memory</c> section, Plan 16). The only
/// shipped provider profile is <c>postgres-fts</c> (spec §14.7): the shared
/// kernel Postgres is the store and the index — no new container. An unknown
/// provider value fails startup (no silent fallback, the artifact-options
/// law). Hindsight/custom stay documented seams behind <see cref="IMemoryProvider"/>.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Memory";

    /// <summary>Active provider profile; only <c>postgres-fts</c> ships. Read once at composition.</summary>
    public string Provider { get; set; } = "postgres-fts";

    /// <summary>Retention policy defaults (spec §14.6: candidates age out sooner than reviewed knowledge).</summary>
    public RetentionOptions Retention { get; set; } = new();

    /// <summary>Retention-pass scheduling (the second hosted loop, advisory lock 420003).</summary>
    public GcOptions Gc { get; set; } = new();

    /// <summary>Write-amplification guards (spec §14.8 #3).</summary>
    public QuotaOptions Quota { get; set; } = new();

    /// <summary>Candidate/expiry defaults; per-Project <c>projects.config</c> key <c>memory</c> overrides (D7).</summary>
    public sealed class RetentionOptions
    {
        /// <summary>Days an unreviewed <c>candidate</c> stays searchable before the GC pass tombstones it. 0 = expires same day.</summary>
        public int CandidateDays { get; set; } = 90;
    }

    /// <summary>Retention-pass loop scheduling.</summary>
    public sealed class GcOptions
    {
        /// <summary>Seconds between retention passes.</summary>
        public int IntervalSeconds { get; set; } = 300;
    }

    /// <summary>Quota/payload guards (D9).</summary>
    public sealed class QuotaOptions
    {
        /// <summary>Hard cap on retained content length (the schema carries the same maxLength).</summary>
        public int MaxContentChars { get; set; } = 16384;

        /// <summary>Per-team UTC-day retain budget; the (N+1)-th retain answers <c>memory-quota-exceeded</c>.</summary>
        public int DailyRetainPerTeam { get; set; } = 200;
    }
}
