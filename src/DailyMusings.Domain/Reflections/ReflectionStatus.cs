using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Reflections;

/// <summary>
/// The draft lifecycle (docs/开发指导.md §6.3, decision A.3). Seven values, mirroring the state
/// diagram exactly: <c>Failed</c> and <c>StaleByLateInput</c> are statuses, not flags, so that the
/// client can group drafts by status without re-deriving display state on its own
/// (which §4.1 keeps on the server).
/// </summary>
public enum ReflectionStatus
{
    PendingInputs = 0,
    Ready = 1,
    Generating = 2,
    ReviewRequired = 3,
    Confirmed = 4,

    /// <summary>The draft state after the two-stage generation exhausted its retries.</summary>
    /// <remarks>
    /// Distinct from <see cref="Jobs.JobStatus.Failed"/>, which is the task state. Conflating the two
    /// is exactly what A.3 warns against.
    /// </remarks>
    Failed = 5,

    /// <summary>Generated, then more same-day input arrived. Waiting for regeneration.</summary>
    StaleByLateInput = 6,
}

/// <summary>Why this reflection was generated. Kept for audit and for the §7 exception rules.</summary>
public enum GenerationReason
{
    /// <summary>The ordinary 23:00 slot.</summary>
    Scheduled = 0,

    /// <summary>Caught up after downtime or an offline client, day by day (§7).</summary>
    Backfill = 1,

    /// <summary>
    /// Regeneration caused by late same-day input — the single permitted exception to
    /// "no arbitrary historical regeneration" (§7).
    /// </summary>
    LateInputRegeneration = 2,

    /// <summary>A user asked for generation explicitly.</summary>
    Manual = 3,
}

/// <summary>Why a draft was marked stale. Audit trail only — it never drives status by itself (A.3).</summary>
public enum StaleReason
{
    LateInput = 0,
}

/// <summary>The allowed draft transitions. Anything absent here is a programming error, not a runtime state.</summary>
public static class ReflectionStatusTransitions
{
    private static readonly Dictionary<ReflectionStatus, ReflectionStatus[]> Allowed = new()
    {
        [ReflectionStatus.PendingInputs] = [ReflectionStatus.Ready],
        [ReflectionStatus.Ready] = [ReflectionStatus.Generating],
        [ReflectionStatus.Generating] = [ReflectionStatus.ReviewRequired, ReflectionStatus.Failed],
        [ReflectionStatus.Failed] = [ReflectionStatus.Generating, ReflectionStatus.Ready],
        [ReflectionStatus.ReviewRequired] =
        [
            ReflectionStatus.Generating,
            ReflectionStatus.Confirmed,
            ReflectionStatus.StaleByLateInput,
        ],

        // Confirmed 有两条出边：新的当日素材让它失效（→ StaleByLateInput），或者**用户自己**要求重新生成
        // （→ Generating）。后者是 A.21 的「已有正式稿后再生成新稿」，2026-10-01 才真正打通（A.40）：
        // 在那之前这条边不存在，于是「发布之后再生成」在实现上根本走不到。
        // 关键在于**没有自动路径**能走这条边：ForScheduledRun 仍然拒绝已确认的稿件，所以确认过的稿子
        // 只会被用户本人推翻，不会被定时任务悄悄换掉——A.3 那条不变量的本意保住了。
        [ReflectionStatus.Confirmed] = [ReflectionStatus.StaleByLateInput, ReflectionStatus.Generating],

        // A stale draft can be regenerated, or the user can simply accept it as it stands.
        [ReflectionStatus.StaleByLateInput] = [ReflectionStatus.Generating, ReflectionStatus.Confirmed],
    };

    public static bool IsAllowed(ReflectionStatus from, ReflectionStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0);

    public static IReadOnlyList<ReflectionStatus> From(ReflectionStatus status) =>
        Allowed.TryGetValue(status, out var targets) ? targets : [];

    /// <summary>Throws unless the transition is part of the model.</summary>
    public static void EnsureAllowed(ReflectionStatus from, ReflectionStatus to)
    {
        if (!IsAllowed(from, to))
        {
            throw new DomainException(
                "reflection.status.illegal_transition",
                $"Transition {from} -> {to} is not part of the draft lifecycle.");
        }
    }
}
