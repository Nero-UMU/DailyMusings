using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Jobs;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Application.Reflections;

/// <summary>Why a generation was asked for, or why it was refused.</summary>
public sealed record ReflectionGenerationRequestResult(
    ContentDate ContentDate,
    GenerationDecision Decision,
    ProcessingJob? Job,
    Reflection? Reflection);

/// <summary>
/// Decides whether a day may be generated and, if so, puts one durable job on the queue
/// (docs/开发指导.md §7, §14).
/// <para>
/// Eligibility is settled here rather than inside the job handler, so the same rules serve the nightly
/// scheduler, the catch-up scan and the manual API, and so a refusal is an immediate answer the client can show
/// instead of a job that fails a few seconds later.
/// </para>
/// </summary>
public sealed class RequestReflectionGenerationUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IReflectionRepository _reflections;
    private readonly IContentCalendarProvider _calendars;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;
    private readonly JobEnqueuer _jobs;

    public RequestReflectionGenerationUseCase(
        IInputEntryRepository inputs,
        IReflectionRepository reflections,
        IContentCalendarProvider calendars,
        IContentSettingsProvider settings,
        IClock clock,
        JobEnqueuer jobs)
    {
        _inputs = inputs;
        _reflections = reflections;
        _calendars = calendars;
        _settings = settings;
        _clock = clock;
        _jobs = jobs;
    }

    /// <param name="manual">
    /// A user asked for this day explicitly. Manual requests may retry a terminally failed draft (§14) and may
    /// overwrite a hand-edited working version if the user accepted that (§6.4).
    /// </param>
    /// <param name="ignoreTranscriptionFailures">
    /// §7: 用户可选择忽略失败项继续. Only honoured for a manual request — the nightly run defers rather than
    /// deciding on the user's behalf that a failed transcription does not matter.
    /// </param>
    /// <param name="allowOverwriteOfManualEdits">
    /// Set when the user accepted losing the working slot's hand edits. Passing this without having warned them
    /// is exactly what §6.4 forbids, so it is only ever set from a request that displayed that warning.
    /// </param>
    public async Task<ReflectionGenerationRequestResult> ExecuteAsync(
        ContentDate contentDate,
        bool manual,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken)
    {
        var calendar = await _calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var currentContentDay = calendar.ContentDateOf(now);

        var dayInputs = await _inputs.ListByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false);
        var material = GenerationRules.SelectDayMaterial(dayInputs);

        var blockingFailures = !(manual && ignoreTranscriptionFailures) &&
                               GenerationRules.HasBlockingTranscriptionFailures(dayInputs);

        var reflection = await _reflections.FindByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false);

        if (reflection is null && await _reflections.IsDeletedAsync(contentDate, cancellationToken).ConfigureAwait(false))
        {
            return new ReflectionGenerationRequestResult(
                contentDate,
                GenerationDecision.Block("reflection.deleted", "这一天的稿件已被你删除，不会自动重新生成。"),
                null,
                null);
        }

        var status = reflection?.Status;

        GenerationDecision decision;

        if (manual)
        {
            decision = GenerationRules.ForManualRequest(
                contentDate,
                currentContentDay,
                status,
                material.Count > 0,
                blockingFailures);
        }
        else
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

            decision = GenerationRules.IsSlotDue(contentDate, now, calendar, settings.GenerationLocalTime)
                ? GenerationRules.ForScheduledRun(status, material.Count > 0, blockingFailures)
                : GenerationDecision.Block(
                    "reflection.generation.slot_not_due",
                    "The day's generation slot has not arrived yet.");
        }

        if (!decision.Allowed)
        {
            return new ReflectionGenerationRequestResult(contentDate, decision, null, reflection);
        }

        var reason = ResolveReason(manual, contentDate, currentContentDay, status);

        // The round is the number of versions this day has produced, which makes "the first draft" and "the
        // draft after new material arrived" two different jobs instead of one key fighting the unique index.
        var round = reflection is null
            ? 0
            : await _reflections.CountVersionsAsync(reflection.Id, cancellationToken).ConfigureAwait(false);

        var payload = new ReflectionGenerationPayload(allowOverwriteOfManualEdits, reason, ignoreTranscriptionFailures);

        var job = await _jobs.EnsureAsync(
            JobType.ReflectionGeneration,
            contentDate.ToString(),
            IdempotencyKeys.ReflectionGeneration(contentDate, round, allowOverwriteOfManualEdits),
            payload.ToJson(),
            requeueFailed: manual,
            cancellationToken).ConfigureAwait(false);

        return new ReflectionGenerationRequestResult(contentDate, decision, job, reflection);
    }

    private static GenerationReason ResolveReason(
        bool manual,
        ContentDate contentDate,
        ContentDate currentContentDay,
        ReflectionStatus? status)
    {
        if (status == ReflectionStatus.StaleByLateInput)
        {
            // §7's only permitted regeneration of an existing day, whether the user pressed the button or the
            // scheduler noticed that the day had gone stale.
            return GenerationReason.LateInputRegeneration;
        }

        if (manual)
        {
            return GenerationReason.Manual;
        }

        return contentDate < currentContentDay ? GenerationReason.Backfill : GenerationReason.Scheduled;
    }
}

/// <summary>How one generation run ended.</summary>
public enum ReflectionGenerationOutcome
{
    Generated = 0,

    /// <summary>The day has nothing to write about. Counted as success: retrying would find the same nothing.</summary>
    SkippedNoMaterial = 1,

    /// <summary>The day was confirmed, so §6.3's state machine has no edge back into generation.</summary>
    SkippedAlreadyConfirmed = 2,

    /// <summary>
    /// §6.4: 不得静默覆盖用户手工编辑. The working version carries hand edits and nobody has accepted losing
    /// them, so the draft is left exactly as it is for the user to resolve.
    /// </summary>
    SkippedManualEditsProtected = 3,
}

public sealed record ReflectionGenerationResult(
    ReflectionGenerationOutcome Outcome,
    Reflection? Reflection,
    ReflectionVersion? Version,
    int UnresolvedCitations,
    ProcessingJob? CheckJob);

/// <summary>
/// Writes one day's reflection: builds the request from the day's material plus retrieved history, installs the
/// result as a new version and rotates the slots (docs/开发指导.md §8.4, §6.4).
/// <para>
/// Called by the durable job handler, so everything it needs to survive a restart is either in the database or
/// in the job's payload. It performs no eligibility check of its own — the enqueuer decided — but it does
/// refuse the two states where generating would destroy something: a confirmed day, and a working version with
/// hand edits that nobody agreed to lose.
/// </para>
/// </summary>
public sealed class GenerateReflectionUseCase
{
    private readonly IInputEntryRepository _inputs;
    private readonly IReflectionRepository _reflections;
    private readonly IReflectionGenerationClient _client;
    private readonly IGenerationSettingsProvider _settings;
    private readonly IContentCalendarProvider _calendars;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly HistoryRetrievalUseCase _retrieval;
    private readonly JobEnqueuer _jobs;
    private readonly Notifications.QueueNotificationUseCase _notifications;
    private readonly ITopicRepository _topics;
    private readonly Topics.ResolveArticleTopicsUseCase _resolveTopics;

    public GenerateReflectionUseCase(
        IInputEntryRepository inputs,
        IReflectionRepository reflections,
        IReflectionGenerationClient client,
        IGenerationSettingsProvider settings,
        IContentCalendarProvider calendars,
        IUnitOfWork unitOfWork,
        IClock clock,
        HistoryRetrievalUseCase retrieval,
        JobEnqueuer jobs,
        Notifications.QueueNotificationUseCase notifications,
        ITopicRepository topics,
        Topics.ResolveArticleTopicsUseCase resolveTopics)
    {
        _inputs = inputs;
        _reflections = reflections;
        _client = client;
        _settings = settings;
        _calendars = calendars;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _retrieval = retrieval;
        _jobs = jobs;
        _notifications = notifications;
        _topics = topics;
        _resolveTopics = resolveTopics;
    }

    public async Task<ReflectionGenerationResult> ExecuteAsync(
        ContentDate contentDate,
        ReflectionGenerationPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var now = _clock.UtcNow;

        var dayInputs = await _inputs.ListByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false);
        var material = GenerationRules.SelectDayMaterial(dayInputs);

        if (material.Count == 0)
        {
            // §7: 当天无输入时不得创建空文章.
            return new ReflectionGenerationResult(ReflectionGenerationOutcome.SkippedNoMaterial, null, null, 0, null);
        }

        var reflection = await _reflections.FindByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false);

        if (reflection?.Status == ReflectionStatus.Confirmed)
        {
            return new ReflectionGenerationResult(
                ReflectionGenerationOutcome.SkippedAlreadyConfirmed,
                reflection,
                null,
                0,
                null);
        }

        var workingVersion = reflection?.WorkingVersionId is { } workingId
            ? await _reflections.FindVersionAsync(workingId, cancellationToken).ConfigureAwait(false)
            : null;

        if (workingVersion?.HasManualEdits == true && !payload.AllowOverwriteOfManualEdits)
        {
            // Checked before the model is called, not after: the point is to leave the user's text alone, and
            // there is no reason to spend a generation to discover that.
            return new ReflectionGenerationResult(
                ReflectionGenerationOutcome.SkippedManualEditsProtected,
                reflection,
                workingVersion,
                0,
                null);
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        // The draft is persisted as generating *before* the model is called. Two things depend on that: the
        // client can show §9.1's 生成状态 instead of "nothing here yet", and a failure leaves a draft in the
        // Failed state §6.3 defines rather than no draft at all, which is what makes §14's "fix the cause and
        // retry" possible from the draft screen.
        var isNew = reflection is null;
        var reflectionId = reflection?.Id ?? ReflectionId.New();

        reflection ??= Reflection.Create(reflectionId, contentDate, payload.Reason, now);
        if (reflection.Status == ReflectionStatus.PendingInputs)
        {
            reflection.MarkReady(now);
        }

        reflection.BeginGeneration(payload.Reason, now);
        await SaveReflectionAsync(reflection, isNew, cancellationToken).ConfigureAwait(false);

        var retrieval = await _retrieval
            .ExecuteAsync(contentDate, material, cancellationToken)
            .ConfigureAwait(false);

        var writing = payload.Settings ?? WritingSettings.Default;

        // The vocabulary the model may choose from. Only active topics: a merged one is a tombstone and must
        // never attract new material (A.9), and offering it would invite exactly that.
        var knownTopics = (await _topics.ListAsync(includeMerged: false, cancellationToken).ConfigureAwait(false))
            .Select(topic => topic.Name)
            .ToArray();

        var draft = await _client
            .GenerateAsync(
                new GenerationRequest(
                    contentDate,
                    material,
                    retrieval.Materials,
                    writing,
                    settings.PromptVersion,
                    knownTopics),
                cancellationToken)
            .ConfigureAwait(false);

        // §6.2 as revised: the article's topics come from the model's answer, resolved against the real
        // vocabulary. Failures here are swallowed on purpose — a topic that could not be filed is a missing
        // label, whereas a failed generation is a day with no draft at all, and the second is far worse than
        // the first. This mirrors the input side, where topic recognition is likewise a convenience.
        var (primaryTopic, secondaryTopics) = await ResolveDraftTopicsAsync(draft, cancellationToken)
            .ConfigureAwait(false);

        var draftTopics = new List<TopicId>();

        if (primaryTopic is { } primary)
        {
            draftTopics.Add(primary);
        }

        draftTopics.AddRange(secondaryTopics);

        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflectionId,
            draft.Title,
            draft.Summary,
            draft.Body,
            writing,
            new ModelInfo(settings.Model),
            settings.PromptVersion,
            now,
            draft.Tags,
            draft.Categories,
            draftTopics);

        var (sources, unresolved) = BuildSources(version, draft.Citations, dayInputs, retrieval.Materials, contentDate);

        // §6.4: the version that was being edited moves to the previous slot, and the first version ever
        // produced stays where it is forever. The domain owns that rule; passing the flags here is how this use
        // case reports what it knows about the working slot.
        reflection.ApplyGeneratedVersion(
            version.Id,
            workingVersion?.HasManualEdits ?? false,
            payload.AllowOverwriteOfManualEdits,
            now);

        ProcessingJob checkJob;

        await using (var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);
            await _reflections.AddVersionAsync(version, cancellationToken).ConfigureAwait(false);
            await _reflections.ReplaceSourcesAsync(version.Id, sources, cancellationToken).ConfigureAwait(false);

            // The version's topics are written with the version itself: a draft whose topics arrive later would
            // be a draft the admin list shows as unfiled for a moment, and the merge guard could let a topic be
            // deleted in that window.
            if (version.TopicIds.Count > 0)
            {
                await _reflections
                    .SetVersionTopicsAsync(version.Id, version.TopicIds, cancellationToken)
                    .ConfigureAwait(false);
            }

            // §8.4's second stage is its own persisted job, enqueued in the same transaction as the version it
            // checks: a draft must never exist without the check that belongs to it having been scheduled.
            checkJob = await _jobs.EnsureAsync(
                JobType.UnsourcedStatementCheck,
                version.Id.ToString(),
                IdempotencyKeys.UnsourcedStatementCheck(version.Id),
                payload: null,
                requeueFailed: false,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // §12's "新草稿待确认". Queued after the draft is durable, so a mail can never announce something that a
        // failed transaction rolled back; a mail failure, in turn, cannot touch the draft (§12).
        await _notifications
            .QueueDraftReadyAsync(contentDate, version.Id, version.Title, cancellationToken)
            .ConfigureAwait(false);

        return new ReflectionGenerationResult(
            ReflectionGenerationOutcome.Generated,
            reflection,
            version,
            unresolved,
            checkJob);
    }

    /// <summary>
    /// Turns the model's topic answer into real topics (docs/开发指导.md §6.2 as revised).
    /// <para>
    /// Failure is reported as "no topics", never as a failed generation. The draft is the point; the filing is
    /// a convenience, and losing a day's writing because a label could not be stored would be an absurd trade —
    /// the same reasoning the input side already applies to topic recognition.
    /// </para>
    /// </summary>
    private async Task<(TopicId? Primary, IReadOnlyList<TopicId> Secondary)> ResolveDraftTopicsAsync(
        GeneratedDraft draft,
        CancellationToken cancellationToken)
    {
        // Reused names first: a day is about an existing theme far more often than it needs a new one, and the
        // order decides which one becomes the primary topic.
        var names = draft.Topics.Concat(draft.NewTopics).ToArray();

        if (names.Length == 0)
        {
            return (null, []);
        }

        try
        {
            return await _resolveTopics.ExecuteAsync(names, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            return (null, []);
        }
    }

    private async Task SaveReflectionAsync(
        Reflection reflection,
        bool isNew,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (isNew)
        {
            await _reflections.AddAsync(reflection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the model's citations into source references by locating each quoted sentence in the draft the
    /// model actually produced (decision A.6).
    /// <para>
    /// Citations whose quote cannot be found, or that name an input that was never offered, are dropped and
    /// counted. Dropping is deliberate: a citation nobody can follow is worse than a missing one, because the
    /// user checking it would be shown the wrong sentence.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<SourceReference> Sources, int Unresolved) BuildSources(
        ReflectionVersion version,
        IReadOnlyList<GeneratedCitation> citations,
        IReadOnlyList<InputEntry> dayInputs,
        IReadOnlyList<Domain.Retrieval.RetrievedMaterial> historicalMaterial,
        ContentDate contentDate)
    {
        var offered = new Dictionary<InputEntryId, InputEntry>();
        foreach (var entry in dayInputs.Concat(historicalMaterial.Select(material => material.Entry)))
        {
            offered[entry.Id] = entry;
        }

        var sources = new List<SourceReference>();
        var unresolved = 0;

        foreach (var citation in citations)
        {
            var located = SourceQuoteLocator.Locate(version.Body, citation.Quote);
            if (located is null)
            {
                unresolved++;
                continue;
            }

            var cited = citation.InputIds.Where(offered.ContainsKey).ToArray();
            if (cited.Length == 0)
            {
                unresolved++;
                continue;
            }

            foreach (var inputId in cited)
            {
                var entry = offered[inputId];
                sources.Add(SourceReference.Create(
                    SourceReferenceId.New(),
                    version.Id,
                    located.BlockIndex,
                    located.CharStart,
                    located.CharEnd,
                    located.Text,
                    inputId,
                    citation.Relevance,
                    citation.Reason,
                    isHistorical: entry.ContentDate < contentDate));
            }
        }

        return (sources, unresolved);
    }
}
