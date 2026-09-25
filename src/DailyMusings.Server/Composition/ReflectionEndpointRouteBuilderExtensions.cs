using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Inputs;
using DailyMusings.Application.Jobs;
using DailyMusings.Application.Reflections;
using DailyMusings.Application.Topics;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// Topics and daily drafts (docs/开发指导.md §13, §9.3).
/// <para>
/// Two conventions run through every handler here. Failures answer with a stable code the client can act on
/// rather than an exception page, and no handler ever logs or echoes draft text — §16 keeps the user's writing
/// out of the default log, and the response body is the only place it belongs.
/// </para>
/// </summary>
public static class ReflectionEndpointRouteBuilderExtensions
{
    private const int DefaultListDays = 31;
    private const int MaxListDays = 366;

    public static IEndpointRouteBuilder MapReflectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var topics = endpoints
            .MapGroup("/api/topics")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        topics.MapGet(string.Empty, ListTopicsAsync);
        topics.MapPost(string.Empty, CreateTopicAsync).DisableAntiforgery();
        topics.MapPatch("/{id}", RenameTopicAsync);
        topics.MapPost("/merge", MergeTopicsAsync).DisableAntiforgery();

        // Deleting a topic and asking who is using it are administrator operations, and they are mapped outside
        // the group so the policy is simply "administrator" rather than "device-or-admin *and* administrator" —
        // a combination that works but reads as a mistake. Both questions belong to the admin page: a device
        // token may file its own capture under a topic, not retire the vocabulary.
        endpoints
            .MapGet("/api/topics/{id}/usage", GetTopicUsageAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        endpoints
            .MapDelete("/api/topics/{id}", DeleteTopicAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        var reflections = endpoints
            .MapGroup("/api/reflections")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        reflections.MapGet(string.Empty, ListReflectionsAsync);
        reflections.MapGet("/{date}", GetReflectionAsync);
        reflections.MapGet("/{date}/sources", GetSourcesAsync);
        reflections.MapPost("/{date}/generate", GenerateAsync).DisableAntiforgery();
        reflections.MapPost("/{date}/regenerate-stale", RegenerateStaleAsync).DisableAntiforgery();
        reflections.MapPatch("/{date}/working-version", SwitchWorkingVersionAsync);
        reflections.MapPatch("/{date}/working-version/content", EditVersionAsync);

        // Re-filing an article's topics is the migration path the topic deletion guard sends the user to, so it
        // is administrator-only for the same reason deletion is.
        endpoints
            .MapPatch("/api/reflections/{date}/topics", AssignReflectionTopicsAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        reflections.MapPost("/{date}/confirm", ConfirmAsync).DisableAntiforgery();

        // Filing an input under topics belongs to the input's own URL, per §13.
        endpoints
            .MapPut("/api/inputs/{id}/topics", AssignInputTopicsAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        var semantics = endpoints
            .MapGroup("/api/system")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        // §8.3 requires the degraded state to be visible to the client, and A.8 requires it to be a queryable
        // status rather than a per-search probe.
        semantics.MapGet("/semantic-search", GetSemanticSearchAsync);

        return endpoints;
    }

    private static async Task<IResult> ListTopicsAsync(
        [FromQuery] bool? includeMerged,
        ListTopicsWithUsageUseCase list,
        CancellationToken cancellationToken)
    {
        var summaries = await list.ExecuteAsync(includeMerged ?? false, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new TopicListResponse(summaries.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> GetTopicUsageAsync(
        string id,
        GetTopicUsageUseCase usage,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundTopic();
        }

        try
        {
            var view = await usage.ExecuteAsync(new TopicId(parsed), cancellationToken).ConfigureAwait(false);

            return Results.Ok(new TopicUsageDto(
                view.Topic.Id.ToString(),
                view.Articles
                    .Select(article => new TopicUsageArticleDto(
                        article.ReflectionId.ToString(),
                        article.ContentDate.ToString(),
                        article.VersionId.ToString(),
                        article.Title,
                        ToWireName(article.Status),
                        article.PublicationStatus is { } status ? status.ToString() : null))
                    .ToArray(),
                view.InputCount));
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    /// <summary>
    /// Deletes a topic (§6.2). A refusal arrives as 409 with <c>topic.in_use</c> rather than as a silent
    /// detach: the user is being told which articles to move first, and the message says so.
    /// </summary>
    private static async Task<IResult> DeleteTopicAsync(
        string id,
        DeleteTopicUseCase delete,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundTopic();
        }

        try
        {
            await delete.ExecuteAsync(new TopicId(parsed), cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    /// <summary>
    /// Re-files the working version's topics. Answered with the refreshed draft so the caller can re-render
    /// without a second request.
    /// </summary>
    private static async Task<IResult> AssignReflectionTopicsAsync(
        string date,
        [FromBody] AssignReflectionTopicsRequest? request,
        AssignReflectionVersionTopicsUseCase assign,
        GetReflectionUseCase get,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        if (request is null)
        {
            return Invalid("A topic assignment is required.");
        }

        TopicId? primary = null;
        if (!string.IsNullOrWhiteSpace(request.PrimaryTopicId))
        {
            if (!Guid.TryParse(request.PrimaryTopicId, out var primaryId))
            {
                return Invalid("primaryTopicId must be a UUID.");
            }

            primary = new TopicId(primaryId);
        }

        var secondary = new List<TopicId>();
        foreach (var value in request.SecondaryTopicIds ?? [])
        {
            if (!Guid.TryParse(value, out var secondaryId))
            {
                return Invalid("Every secondary topic id must be a UUID.");
            }

            secondary.Add(new TopicId(secondaryId));
        }

        try
        {
            await assign
                .ExecuteAsync(contentDate, primary, secondary, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }

        var view = await get.ExecuteAsync(contentDate, cancellationToken).ConfigureAwait(false);

        return view is null
            ? Results.Json(
                new ApiError(ApiErrorCodes.NotFound, "No reflection has been produced for that day yet."),
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ToDto(view));
    }

    private static async Task<IResult> CreateTopicAsync(
        [FromBody] CreateTopicRequest? request,
        CreateTopicUseCase create,
        GetTopicUsageUseCase usage,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("A topic name is required.");
        }

        try
        {
            var (topic, created) = await create.ExecuteAsync(request.Name, cancellationToken).ConfigureAwait(false);
            var dto = await ToDtoAsync(topic, usage, cancellationToken).ConfigureAwait(false);

            // 201 on creation, 200 when the name already existed: creating twice is not an error, and the caller
            // can tell the difference without a second request.
            return created
                ? Results.Created($"/api/topics/{topic.Id}", dto)
                : Results.Ok(dto);
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> RenameTopicAsync(
        string id,
        [FromBody] RenameTopicRequest? request,
        RenameTopicUseCase rename,
        GetTopicUsageUseCase usage,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundTopic();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("A topic name is required.");
        }

        try
        {
            var topic = await rename
                .ExecuteAsync(new TopicId(parsed), request.Name, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(await ToDtoAsync(topic, usage, cancellationToken).ConfigureAwait(false));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static async Task<IResult> MergeTopicsAsync(
        [FromBody] MergeTopicsRequest? request,
        MergeTopicsUseCase merge,
        GetTopicUsageUseCase usage,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            !Guid.TryParse(request.SourceTopicId, out var sourceId) ||
            !Guid.TryParse(request.TargetTopicId, out var targetId))
        {
            return Invalid("Both sourceTopicId and targetTopicId are required.");
        }

        try
        {
            var (source, _, remapped) = await merge
                .ExecuteAsync(new TopicId(sourceId), new TopicId(targetId), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new TopicMergeResponse(
                await ToDtoAsync(source, usage, cancellationToken).ConfigureAwait(false),
                remapped));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static async Task<IResult> AssignInputTopicsAsync(
        string id,
        [FromBody] AssignInputTopicsRequest? request,
        AssignInputTopicsUseCase assign,
        GetInputUseCase getInput,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        if (request is null)
        {
            return Invalid("A topic assignment is required.");
        }

        TopicId? primary = null;
        if (!string.IsNullOrWhiteSpace(request.PrimaryTopicId))
        {
            if (!Guid.TryParse(request.PrimaryTopicId, out var primaryId))
            {
                return Invalid("primaryTopicId must be a UUID.");
            }

            primary = new TopicId(primaryId);
        }

        var secondary = new List<TopicId>();
        foreach (var value in request.SecondaryTopicIds ?? [])
        {
            if (!Guid.TryParse(value, out var secondaryId))
            {
                return Invalid("Every secondary topic id must be a UUID.");
            }

            secondary.Add(new TopicId(secondaryId));
        }

        try
        {
            await assign
                .ExecuteAsync(new InputEntryId(parsed), primary, secondary, cancellationToken)
                .ConfigureAwait(false);

            var view = await getInput
                .ExecuteAsync(new InputEntryId(parsed), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new InputTopicAssignmentResponse(
                view.Entry.Id.ToString(),
                view.Entry.PrimaryTopicId?.ToString(),
                view.Entry.SecondaryTopicIds.Select(topicId => topicId.ToString()).ToArray()));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static async Task<IResult> ListReflectionsAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        ListReflectionsUseCase list,
        IContentCalendarProvider calendars,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var calendar = await calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var today = calendar.ContentDateOf(clock.UtcNow);

        if (!TryReadDate(to, today, out var toInclusive))
        {
            return Invalid("The 'to' date must be formatted as YYYY-MM-DD.");
        }

        if (!TryReadDate(from, toInclusive.AddDays(-(DefaultListDays - 1)), out var fromInclusive))
        {
            return Invalid("The 'from' date must be formatted as YYYY-MM-DD.");
        }

        if (fromInclusive > toInclusive)
        {
            return Invalid("'from' must not be after 'to'.");
        }

        if (toInclusive.AddDays(-(MaxListDays - 1)) > fromInclusive)
        {
            return Invalid($"A range may not exceed {MaxListDays} days.");
        }

        var effectivePage = Math.Max(page ?? 1, 1);

        // Without an explicit pageSize the whole requested range comes back, which is the behaviour this
        // endpoint had before it learned to page: the phone's calendar asks for a range and draws all of it, and
        // silently truncating that to a default page size would look like days with no draft.
        var rangeDays = toInclusive.Value.DayNumber - fromInclusive.Value.DayNumber + 1;
        var effectivePageSize = Math.Clamp(pageSize ?? rangeDays, 1, MaxListDays);

        var result = await list
            .ExecutePageAsync(fromInclusive, toInclusive, effectivePage, effectivePageSize, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ReflectionListResponse(
            result.Items.Select(ToDto).ToArray(),
            result.Total,
            effectivePage,
            effectivePageSize));
    }

    private static async Task<IResult> GetReflectionAsync(
        string date,
        GetReflectionUseCase get,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        var view = await get.ExecuteAsync(contentDate, cancellationToken).ConfigureAwait(false);

        // A day with no draft is a normal answer, not an error: §7 forbids creating an empty article, so the
        // client must be able to see "nothing here yet".
        return view is null
            ? Results.Json(
                new ApiError(ApiErrorCodes.NotFound, "No reflection has been produced for that day yet."),
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ToDto(view));
    }

    private static async Task<IResult> GetSourcesAsync(
        string date,
        GetReflectionSourcesUseCase get,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        try
        {
            var view = await get.ExecuteAsync(contentDate, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new ReflectionSourcesResponse(
                view.ContentDate.ToString(),
                view.VersionId.ToString(),
                view.CheckedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
                view.Sources.Select(ToDto).ToArray(),
                view.UnsourcedClaims
                    .Select(claim => new UnsourcedClaimDto(claim.BlockIndex, claim.CharStart, claim.CharEnd, claim.Reason))
                    .ToArray(),
                ToDto(view.SemanticSearch)));
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static Task<IResult> GenerateAsync(
        string date,
        [FromBody] GenerateReflectionRequest? request,
        RequestReflectionGenerationUseCase generate,
        CancellationToken cancellationToken) =>
        RequestGenerationAsync(date, request, regenerateStaleOnly: false, generate, get: null, cancellationToken);

    /// <summary>
    /// §7's only permitted regeneration of an existing day. It refuses unless the draft is actually stale, so the
    /// endpoint cannot become a back door to "regenerate any past date".
    /// </summary>
    private static Task<IResult> RegenerateStaleAsync(
        string date,
        [FromBody] GenerateReflectionRequest? request,
        RequestReflectionGenerationUseCase generate,
        GetReflectionUseCase get,
        CancellationToken cancellationToken) =>
        RequestGenerationAsync(date, request, regenerateStaleOnly: true, generate, get, cancellationToken);

    private static async Task<IResult> RequestGenerationAsync(
        string date,
        GenerateReflectionRequest? request,
        bool regenerateStaleOnly,
        RequestReflectionGenerationUseCase generate,
        GetReflectionUseCase? get,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        if (regenerateStaleOnly && get is not null)
        {
            var current = await get.ExecuteAsync(contentDate, cancellationToken).ConfigureAwait(false);

            if (current is null || current.Status != ReflectionStatus.StaleByLateInput)
            {
                return Results.Json(
                    new ApiError(
                        "reflection.regeneration.not_stale",
                        "This day is not waiting for regeneration after new material."),
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        var result = await generate
            .ExecuteAsync(
                contentDate,
                manual: true,
                ignoreTranscriptionFailures: request?.IgnoreTranscriptionFailures ?? false,
                allowOverwriteOfManualEdits: request?.AllowOverwriteOfManualEdits ?? false,
                cancellationToken)
            .ConfigureAwait(false);

        var response = new ReflectionGenerationResponse(
            result.ContentDate.ToString(),
            result.Job is not null,
            result.Decision.Code,
            result.Decision.Detail,
            result.Job is null ? null : ToDto(result.Job));

        // A refusal is reported with 409 rather than 400: the request was well formed, the day simply is not in a
        // state where generating it is allowed, and the code says which.
        return result.Decision.Allowed
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> SwitchWorkingVersionAsync(
        string date,
        [FromBody] SwitchWorkingVersionRequest? request,
        SwitchReflectionVersionUseCase switchVersion,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        if (request is null || !Guid.TryParse(request.VersionId, out var versionId))
        {
            return Invalid("A versionId is required.");
        }

        return await RunReflectionWriteAsync(
            () => switchVersion.ExecuteAsync(contentDate, new ReflectionVersionId(versionId), cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<IResult> EditVersionAsync(
        string date,
        [FromBody] EditReflectionRequest? request,
        EditReflectionVersionUseCase edit,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        if (request is null)
        {
            return Invalid("A body is required.");
        }

        return await RunReflectionWriteAsync(
            () => edit.ExecuteAsync(contentDate, request.Title, request.Summary, request.Body, cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ConfirmAsync(
        string date,
        [FromBody] Contracts.ConfirmReflectionRequest? request,
        ConfirmReflectionUseCase confirm,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        return await RunReflectionWriteAsync(
            () => confirm.ExecuteAsync(
                contentDate,
                new Application.Reflections.ConfirmReflectionRequest(request?.AcceptedUnsourcedClaims ?? false),
                cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<IResult> GetSemanticSearchAsync(
        Application.Embeddings.GetSemanticSearchStateUseCase get,
        CancellationToken cancellationToken)
    {
        var state = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(state));
    }

    private static async Task<IResult> RunReflectionWriteAsync(Func<Task<ReflectionView>> action)
    {
        try
        {
            var view = await action().ConfigureAwait(false);
            return Results.Ok(ToDto(view));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static bool TryReadDate(string? value, ContentDate fallback, out ContentDate result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = fallback;
            return true;
        }

        return ContentDate.TryParse(value, out result);
    }

    private static TopicDto ToDto(TopicSummary summary) =>
        ToDto(summary.Topic, summary.ArticleCount, summary.InputCount);

    /// <summary>
    /// A single topic with its counts, for the create/rename/merge answers. Those responses carry the same shape
    /// as the list so a client can drop the result straight into its table instead of re-reading it.
    /// </summary>
    private static async Task<TopicDto> ToDtoAsync(
        Topic topic,
        GetTopicUsageUseCase usage,
        CancellationToken cancellationToken)
    {
        var view = await usage.ExecuteAsync(topic.Id, cancellationToken).ConfigureAwait(false);
        return ToDto(topic, view.Articles.Count, view.InputCount);
    }

    private static TopicDto ToDto(Topic topic, int articleCount, int inputCount) => new(
        topic.Id.ToString(),
        topic.Name,
        topic.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        topic.MergedIntoId?.ToString(),
        topic.MergedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        topic.Origin == TopicOrigin.Model ? TopicOriginNames.Model : TopicOriginNames.User,
        articleCount,
        inputCount);

    private static ReflectionDto ToDto(ReflectionView view) => new(
        view.Id.ToString(),
        view.ContentDate.ToString(),
        ToWireName(view.Status),
        ToWireName(view.GenerationReason),
        view.LastStaleReason?.ToString(),
        view.InitialVersionId?.ToString(),
        view.PreviousVersionId?.ToString(),
        view.WorkingVersionId?.ToString(),
        view.ConfirmedVersionId?.ToString(),
        view.ConfirmedVersionIsNotWorking,
        view.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        view.UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        view.InitialVersion is null ? null : ToDto(view.InitialVersion),
        view.PreviousVersion is null ? null : ToDto(view.PreviousVersion),
        view.WorkingVersion is null ? null : ToDto(view.WorkingVersion),
        ToDto(view.SemanticSearch));

    private static ReflectionVersionDto ToDto(ReflectionVersionView view) => new(
        view.Id.ToString(),
        view.Title,
        view.Summary,
        view.Body,
        view.Tags,
        view.Categories,
        view.HasManualEdits,
        view.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        view.EditedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        view.ModelName,
        view.PromptVersion,
        view.SourcesCheckedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        view.Sources.Select(ToDto).ToArray(),
        view.UnsourcedClaims
            .Select(claim => new UnsourcedClaimDto(claim.BlockIndex, claim.CharStart, claim.CharEnd, claim.Reason))
            .ToArray(),
        view.Topics
            .Select(topic => new TopicRefDto(topic.Id.ToString(), topic.Name, topic.IsPrimary))
            .ToArray());

    private static SourceReferenceDto ToDto(SourceReferenceView view) => new(
        view.InputId.ToString(),
        view.BlockIndex,
        view.CharStart,
        view.CharEnd,
        view.Relevance,
        view.Reason,
        view.IsHistorical,
        ToWireName(view.Drift));

    private static SemanticSearchDto ToDto(SemanticSearchState state) =>
        new(state.Enabled, state.Available, state.Rebuilding);

    private static JobDto ToDto(ProcessingJob job) => new(
        job.Id.ToString(),
        job.JobType.ToString(),
        job.TargetId,
        job.Status.ToString(),
        job.AttemptCount,
        job.ScheduledAtUtc.ToString("o", CultureInfo.InvariantCulture),
        job.StartedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        job.CompletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        job.ErrorCode,
        job.ErrorSummary);

    private static string ToWireName(ReflectionStatus status) => status switch
    {
        ReflectionStatus.PendingInputs => ReflectionStatusNames.PendingInputs,
        ReflectionStatus.Ready => ReflectionStatusNames.Ready,
        ReflectionStatus.Generating => ReflectionStatusNames.Generating,
        ReflectionStatus.ReviewRequired => ReflectionStatusNames.ReviewRequired,
        ReflectionStatus.Confirmed => ReflectionStatusNames.Confirmed,
        ReflectionStatus.StaleByLateInput => ReflectionStatusNames.StaleByLateInput,
        _ => ReflectionStatusNames.Failed,
    };

    private static string ToWireName(GenerationReason reason) => reason switch
    {
        GenerationReason.Backfill => GenerationReasonNames.Backfill,
        GenerationReason.LateInputRegeneration => GenerationReasonNames.LateInputRegeneration,
        GenerationReason.Manual => GenerationReasonNames.Manual,
        _ => GenerationReasonNames.Scheduled,
    };

    private static string ToWireName(SourceDrift drift) => drift switch
    {
        SourceDrift.Exact => SourceDriftNames.Exact,
        SourceDrift.Unresolvable => SourceDriftNames.Unresolvable,
        _ => SourceDriftNames.Drifted,
    };

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFoundTopic() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No topic with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult NotFoundInput() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No input with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult MapDomainFailure(DomainException exception)
    {
        var status = exception.Code switch
        {
            "input.unknown" or "topic.unknown" or "reflection.version.unknown" => StatusCodes.Status404NotFound,

            // A rule refused the change: the request was understood and is not allowed on this state.
            "reflection.status.illegal_transition" or
                "reflection.regeneration.overwrites_manual_edits" or
                "topic.merged" or
                "reflection.version.switch_while_generating" => StatusCodes.Status409Conflict,

            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }

    private static IResult MapUseCaseFailure(UseCaseException exception)
    {
        var status = exception.Code switch
        {
            "topic.unknown" or "input.unknown" or "reflection.unknown" or
                "reflection.version.unknown" or "reflection.sources.no_version" or
                "reflection.confirm.no_version" or "reflection.edit.no_version" =>
                StatusCodes.Status404NotFound,

            "reflection.confirm.unsourced_claims_not_acknowledged" or

            // 来源检查还没跑完就要求确认：请求本身没问题，是实例的状态还不允许，和旁边那条一样是 409。
            // 400 会让「稍后再试一次就好」看起来像「你请求写错了」。
            "reflection.confirm.source_check_pending" or

            "topic.merge.target_retired" or
                "topic.retired" or
                "topic.merge.self" or

                // The topic is in use, or is the target of a merge: both are refusals about the instance's
                // current state rather than about the request, so 409 with the code and the instruction is the
                // honest answer (§6.2: 请先在内容管理里迁移相关内容).
                "topic.in_use" or
                "topic.merge_target" => StatusCodes.Status409Conflict,

            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }
}
