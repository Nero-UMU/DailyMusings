using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Reflections;

/// <summary>
/// Whether a day may be generated right now, and if not, the stable code that says why (§13 surfaces these
/// codes to the client rather than a prose message the client cannot act on).
/// </summary>
public sealed record GenerationDecision(bool Allowed, string? Code, string? Detail)
{
    public static GenerationDecision Permit() => new(true, null, null);

    public static GenerationDecision Block(string code, string detail) => new(false, code, detail);

    public void EnsureAllowed()
    {
        if (!Allowed)
        {
            throw new DomainException(Code ?? "reflection.generation.blocked", Detail ?? "Generation is not allowed.");
        }
    }
}

/// <summary>
/// The scheduling half of §7, expressed as pure predicates over state the caller already has.
/// <para>
/// Everything here is a function of its arguments — no clock, no repository — because these are the rules that
/// decide whether a draft is produced at all, and §17.1 asks for them to be tested directly. Keeping them out
/// of the scheduler also means the same rules serve the scheduled run, the backfill scan and the manual API.
/// </para>
/// </summary>
public static class GenerationRules
{
    /// <summary>
    /// A user-requested generation. §7 allows this for the current content day only — "不支持选择任意历史日期
    /// 重新生成" — because a past day's material set is closed and re-running it would silently rewrite history.
    /// Past days are caught up by the backfill scan instead, where the reason is recorded as
    /// <see cref="GenerationReason.Backfill"/> rather than pretending a human asked for it.
    /// <para>
    /// A user request is deliberately more permissive than the scheduled run: §14 ends with "用户修复配置后可以
    /// 重试单项或批量补跑失败任务", so a terminally failed draft must be reachable by an explicit request even
    /// though the nightly scan refuses to keep retrying it.
    /// </para>
    /// </summary>
    public static GenerationDecision ForManualRequest(
        ContentDate requested,
        ContentDate currentContentDay,
        ReflectionStatus? status,
        bool hasMaterial,
        bool hasBlockingFailures)
    {
        if (requested != currentContentDay)
        {
            return GenerationDecision.Block(
                "reflection.regeneration.date_not_current",
                "Only the current content day can be generated on request.");
        }

        if (!hasMaterial)
        {
            return GenerationDecision.Block(
                "reflection.generation.no_inputs",
                "The day has no input to write about.");
        }

        if (hasBlockingFailures)
        {
            return GenerationDecision.Block(
                "reflection.generation.transcription_failures",
                "At least one input for this day failed to transcribe. Ignore the failures to continue.");
        }

        switch (status)
        {
            case null:
            case ReflectionStatus.PendingInputs:
            case ReflectionStatus.Ready:

            // The user is explicitly retrying after fixing the cause (§14).
            case ReflectionStatus.Failed:

            // An explicit regeneration request. §6.4's rotation guard still applies: the use case must pass
            // userConfirmedOverwrite when the working version carries hand edits.
            case ReflectionStatus.ReviewRequired:
            case ReflectionStatus.StaleByLateInput:
                return GenerationDecision.Permit();

            case ReflectionStatus.Generating:
                return GenerationDecision.Block(
                    "reflection.generation.in_progress",
                    "This day is already being generated.");

            case ReflectionStatus.Confirmed:
                return GenerationDecision.Block(
                    "reflection.generation.already_confirmed",
                    "This day's draft was already confirmed.");

            default:
                return GenerationDecision.Block(
                    "reflection.generation.unknown_status",
                    $"Status {status} is not part of the draft lifecycle.");
        }
    }

    /// <summary>
    /// Whether a day is due for generation, i.e. the draft exists but has no version yet.
    /// </summary>
    /// <param name="status">
    /// The draft's status, or <c>null</c> when no draft exists for the day yet. A missing draft is normal: §7
    /// forbids creating an empty article, so the row appears only once the day has material and is due.
    /// </param>
    /// <param name="hasMaterial">Whether the day has at least one usable input.</param>
    /// <param name="hasBlockingFailures">
    /// Whether the day still has a failed transcription and the user has not chosen to ignore it (§7).
    /// </param>
    public static GenerationDecision ForScheduledRun(
        ReflectionStatus? status,
        bool hasMaterial,
        bool hasBlockingFailures)
    {
        if (!hasMaterial)
        {
            // §7: 当天无输入时不得创建空文章.
            return GenerationDecision.Block(
                "reflection.generation.no_inputs",
                "The day has no input to write about.");
        }

        if (hasBlockingFailures)
        {
            // §7: 当天存在转写失败项时，默认暂缓生成并通知用户；用户可选择忽略失败项继续.
            return GenerationDecision.Block(
                "reflection.generation.transcription_failures",
                "At least one input for this day failed to transcribe. Ignore the failures to continue.");
        }

        switch (status)
        {
            case null:
            case ReflectionStatus.PendingInputs:
            case ReflectionStatus.Ready:
                return GenerationDecision.Permit();

            case ReflectionStatus.StaleByLateInput:
                // §7 path ①/②: the day was already written and more same-day material turned up.
                return GenerationDecision.Permit();

            case ReflectionStatus.Generating:
                return GenerationDecision.Block(
                    "reflection.generation.in_progress",
                    "This day is already being generated.");

            case ReflectionStatus.ReviewRequired:
                return GenerationDecision.Block(
                    "reflection.generation.awaiting_review",
                    "This day already has a draft waiting for review.");

            case ReflectionStatus.Confirmed:
                // Confirming is the user's final word for a day (§6.3 gives Confirmed exactly one outgoing
                // edge, taken only when new same-day material arrives).
                return GenerationDecision.Block(
                    "reflection.generation.already_confirmed",
                    "This day's draft was already confirmed.");

            case ReflectionStatus.Failed:
                // Automatic retry would fight the budget §14 already exhausted, so a terminally failed draft
                // waits for a human to fix the cause and retry.
                return GenerationDecision.Block(
                    "reflection.generation.previously_failed",
                    "This day's generation failed and needs an explicit retry.");

            default:
                return GenerationDecision.Block(
                    "reflection.generation.unknown_status",
                    $"Status {status} is not part of the draft lifecycle.");
        }
    }

    /// <summary>
    /// True once a day's slot has arrived. A past day always has, which is what makes the same predicate serve
    /// both the nightly run and the catch-up scan after downtime (§7).
    /// </summary>
    public static bool IsSlotDue(
        ContentDate day,
        DateTimeOffset nowUtc,
        ContentCalendar calendar,
        TimeOnly generationTime)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return nowUtc >= calendar.AtLocalTime(day, generationTime);
    }

    /// <summary>
    /// Whether an input arriving now makes an already-produced draft stale (§7's two staleness paths).
    /// <para>
    /// The rule is read off the transition table rather than duplicated, so "which statuses can go stale" has
    /// exactly one definition. Both documented paths land here: same-day material after the nightly run, and
    /// yesterday's material arriving today — in both cases the entry's own content day names the draft.
    /// </para>
    /// </summary>
    public static bool ShouldMarkStale(ReflectionStatus status) =>
        status != ReflectionStatus.StaleByLateInput &&
        ReflectionStatusTransitions.IsAllowed(status, ReflectionStatus.StaleByLateInput);

    /// <summary>
    /// True when the day still has a transcription failure the user has not agreed to ignore. Deleted entries
    /// do not count: they are not part of the material set at all.
    /// </summary>
    public static bool HasBlockingTranscriptionFailures(IEnumerable<InputEntry> dayInputs)
    {
        ArgumentNullException.ThrowIfNull(dayInputs);

        return dayInputs.Any(entry =>
            !entry.IsDeleted &&
            entry.SourceType == InputSourceType.Voice &&
            entry.TranscriptionStatus == TranscriptionStatus.Failed);
    }

    /// <summary>
    /// The inputs a generation run may use: not deleted, with text to work from, oldest first (§8.4 asks for
    /// the day's inputs in time order).
    /// </summary>
    public static IReadOnlyList<InputEntry> SelectDayMaterial(IEnumerable<InputEntry> dayInputs)
    {
        ArgumentNullException.ThrowIfNull(dayInputs);

        return dayInputs
            .Where(entry => !entry.IsDeleted && !string.IsNullOrWhiteSpace(entry.TranscriptForGeneration))
            .OrderBy(entry => entry.CreatedAtUtc)

            // Two captures can share a timestamp after an offline replay; the id keeps the prompt order stable
            // so the same day always produces the same request.
            .ThenBy(entry => entry.Id.Value)
            .ToArray();
    }
}
