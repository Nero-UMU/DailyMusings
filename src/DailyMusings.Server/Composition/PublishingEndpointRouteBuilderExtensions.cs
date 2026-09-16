using System.Globalization;
using System.Security.Claims;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Publishing;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// Publishing and notifications (docs/开发指导.md §11, §12, §13).
/// <para>
/// The authorisation split is the product's, not a convenience: a device token may upload a draft, export
/// Markdown and manually publish, while creating targets and enabling unattended publication are administrator
/// actions (decision A.7). Every mutating call records who made it, because §11.1 requires a publication to be
/// attributable after the fact.
/// </para>
/// </summary>
public static class PublishingEndpointRouteBuilderExtensions
{
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;

    public static IEndpointRouteBuilder MapPublishingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var targets = endpoints
            .MapGroup("/api/publish-targets")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        // Reading is allowed to a device: the draft screen has to show where a day can go. Configuring is not, and
        // those routes are mapped outside this group on purpose — §11.1 puts the automatic-publish switch behind
        // the admin page, and a group is the wrong place to say "except for these".
        targets.MapGet(string.Empty, ListTargetsAsync);

        endpoints
            .MapPost("/api/publish-targets", CreateTargetAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly)
            .DisableAntiforgery();

        endpoints
            .MapPatch("/api/publish-targets/{id}", UpdateTargetAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        endpoints
            .MapPost("/api/publish-targets/{id}/automatic-publish", SetAutomaticPublishAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly)
            .DisableAntiforgery();

        var publications = endpoints
            .MapGroup("/api/publications")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        publications.MapGet(string.Empty, ListPublicationsAsync);
        publications.MapGet("/{id}", GetPublicationAsync);
        publications.MapPost("/{id}/retry", RetryPublicationAsync).DisableAntiforgery();
        publications.MapPost("/{id}/check-remote", CheckRemoteAsync).DisableAntiforgery();
        publications.MapPost("/{id}/resolve", ResolveRemoteAsync).DisableAntiforgery();

        // Publishing a day hangs off the day, per §13.
        endpoints
            .MapPost("/api/reflections/{date}/publish/{targetId}", PublishAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin)
            .DisableAntiforgery();

        endpoints
            .MapGet("/api/reflections/{date}/publications", ListForReflectionAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        var notifications = endpoints
            .MapGroup("/api/notification-settings")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        notifications.MapGet(string.Empty, GetNotificationSettingsAsync);
        notifications.MapPatch(string.Empty, UpdateNotificationSettingsAsync);

        return endpoints;
    }

    private static async Task<IResult> ListTargetsAsync(
        ListPublishTargetsUseCase list,
        CancellationToken cancellationToken)
    {
        var targets = await list.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PublishTargetListResponse(targets.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> CreateTargetAsync(
        [FromBody] CreatePublishTargetRequest? request,
        CreatePublishTargetUseCase create,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("A target name is required.");
        }

        if (!TryParseTargetType(request.Type, out var type))
        {
            return Invalid("The target type must be 'wordPress' or 'markdown'.");
        }

        try
        {
            var (target, created) = await create
                .ExecuteAsync(request.Name, type, request.DestinationReference, cancellationToken)
                .ConfigureAwait(false);

            return created
                ? Results.Created($"/api/publish-targets/{target.Id}", ToDto(target))
                : Results.Ok(ToDto(target));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> UpdateTargetAsync(
        string id,
        [FromBody] UpdatePublishTargetRequest? request,
        UpdatePublishTargetUseCase update,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundTarget();
        }

        if (request is null)
        {
            return Invalid("A body is required.");
        }

        try
        {
            var target = await update
                .ExecuteAsync(new PublishTargetId(parsed), request.Name, request.DestinationReference, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(target));
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

    /// <summary>
    /// Enables or disables unattended publishing (docs/开发指导.md §11.1, decision A.7).
    /// <para>
    /// The risk acknowledgement the guide asks for is the client's job to display, and the password requirement is
    /// the server's to enforce — which is why the password travels in the body rather than being inferred from the
    /// session. A device token cannot reach this route at all, so it can never flip the switch.
    /// </para>
    /// </summary>
    private static async Task<IResult> SetAutomaticPublishAsync(
        string id,
        [FromBody] SetAutomaticPublishRequest? request,
        SetAutomaticPublishUseCase setAutomatic,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundTarget();
        }

        if (request is null || string.IsNullOrEmpty(request.CurrentPassword))
        {
            return Invalid("The administrator's current password is required.");
        }

        try
        {
            var target = await setAutomatic
                .ExecuteAsync(new PublishTargetId(parsed), request.Enabled, request.CurrentPassword, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(target));
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

    private static async Task<IResult> ListPublicationsAsync(
        [FromQuery] int? limit,
        ListPublicationsUseCase list,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);
        var items = await list.ExecuteAsync(effectiveLimit, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new PublicationListResponse(items.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> GetPublicationAsync(
        string id,
        ListPublicationsUseCase list,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundPublication();
        }

        var view = await list.GetAsync(new PublicationId(parsed), cancellationToken).ConfigureAwait(false);

        return view is null ? NotFoundPublication() : Results.Ok(ToDto(view));
    }

    private static async Task<IResult> ListForReflectionAsync(
        string date,
        ListPublicationsUseCase list,
        IReflectionRepository reflections,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        var reflection = await reflections
            .FindByContentDateAsync(contentDate, cancellationToken)
            .ConfigureAwait(false);

        if (reflection is null)
        {
            return Results.Ok(new PublicationListResponse([]));
        }

        var items = await list.ForReflectionAsync(reflection.Id, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new PublicationListResponse(items.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> PublishAsync(
        string date,
        string targetId,
        [FromBody] PublishRequest? request,
        HttpContext context,
        RequestPublicationUseCase publish,
        ListPublicationsUseCase list,
        CancellationToken cancellationToken)
    {
        if (!ContentDate.TryParse(date, out var contentDate))
        {
            return Invalid("The date must be formatted as YYYY-MM-DD.");
        }

        if (!Guid.TryParse(targetId, out var parsedTarget))
        {
            return NotFoundTarget();
        }

        if (!TryParseVisibility(request?.Visibility, out var visibility))
        {
            return Invalid("The visibility must be 'draft' or 'public'.");
        }

        var result = await publish
            .ExecuteAsync(
                contentDate,
                new PublishTargetId(parsedTarget),
                visibility,
                ResolveActor(context),
                request?.ReplaceExistingFile ?? false,
                manual: true,
                cancellationToken)
            .ConfigureAwait(false);

        var response = new PublishResponse(
            result.Queued,
            result.Code,
            result.Detail,
            result.Publication is null
                ? null
                : ToDto(await ResolveViewAsync(list, result.Publication.Id, cancellationToken).ConfigureAwait(false)));

        // A refusal is a 409: the request was understood, and the day or the target is simply not in a state
        // where publishing it is allowed. The code says which.
        return result.Queued ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> RetryPublicationAsync(
        string id,
        HttpContext context,
        RetryPublicationUseCase retry,
        ListPublicationsUseCase list,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundPublication();
        }

        try
        {
            var (publication, _) = await retry
                .ExecuteAsync(new PublicationId(parsed), ResolveActor(context), cancellationToken)
                .ConfigureAwait(false);

            var view = await list.GetAsync(publication.Id, cancellationToken).ConfigureAwait(false);
            return view is null ? NotFoundPublication() : Results.Ok(ToDto(view));
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

    private static async Task<IResult> CheckRemoteAsync(
        string id,
        CheckRemoteUseCase check,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundPublication();
        }

        try
        {
            var result = await check.ExecuteAsync(new PublicationId(parsed), cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResponse(result));
        }
        catch (UseCaseException exception)
        {
            return MapUseCaseFailure(exception);
        }
    }

    private static async Task<IResult> ResolveRemoteAsync(
        string id,
        [FromBody] ResolveRemoteRequest? request,
        HttpContext context,
        ResolveRemoteDivergenceUseCase resolve,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundPublication();
        }

        if (!TryParseAction(request?.Action, out var action))
        {
            return Invalid("The action must be 'pull', 'overwrite' or 'keepBoth'.");
        }

        try
        {
            var result = await resolve
                .ExecuteAsync(
                    new PublicationId(parsed),
                    action,
                    ResolveActor(context),
                    request?.AllowOverwriteOfManualEdits ?? false,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToResponse(result));
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

    private static async Task<IResult> GetNotificationSettingsAsync(
        ISmtpSettingsProvider smtp,
        INotificationSettingsProvider notifications,
        CancellationToken cancellationToken) =>
        Results.Ok(await ReadNotificationSettingsAsync(smtp, notifications, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> UpdateNotificationSettingsAsync(
        [FromBody] UpdateNotificationSettingsRequest? request,
        IAppSettingStore settings,
        ISmtpSettingsProvider smtp,
        INotificationSettingsProvider notifications,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        if (request.ToAddress is not null)
        {
            await settings
                .SetAsync(NotificationSettingKeys.ToAddress, request.ToAddress.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.InstanceUrl is not null)
        {
            await settings
                .SetAsync(NotificationSettingKeys.InstanceUrl, request.InstanceUrl.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var (key, value) in new (string Key, bool? Value)[]
                 {
                     (NotificationSettingKeys.DraftReady, request.DraftReady),
                     (NotificationSettingKeys.JobFailed, request.JobFailed),
                     (NotificationSettingKeys.AutomaticPublication, request.AutomaticPublication),
                 })
        {
            if (value is not null)
            {
                await settings
                    .SetAsync(key, value.Value ? "true" : "false", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return Results.Ok(await ReadNotificationSettingsAsync(smtp, notifications, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<NotificationSettingsDto> ReadNotificationSettingsAsync(
        ISmtpSettingsProvider smtp,
        INotificationSettingsProvider notifications,
        CancellationToken cancellationToken)
    {
        var smtpSettings = await smtp.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = await notifications.GetAsync(cancellationToken).ConfigureAwait(false);

        return new NotificationSettingsDto(
            SmtpConfigured: smtpSettings.Enabled,
            ToAddress: settings.ToAddress,
            InstanceUrl: settings.InstanceUrl,
            DraftReady: settings.IsEnabled(Domain.Notifications.NotificationEvent.DraftReady),
            JobFailed: settings.IsEnabled(Domain.Notifications.NotificationEvent.JobFailed),
            AutomaticPublication: settings.IsEnabled(Domain.Notifications.NotificationEvent.AutomaticPublication));
    }

    /// <summary>
    /// Looks a publication's view up after a write, so the response carries the same shape as a read would.
    /// </summary>
    private static async Task<PublicationView> ResolveViewAsync(
        ListPublicationsUseCase list,
        PublicationId id,
        CancellationToken cancellationToken) =>
        await list.GetAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new UseCaseException("publication.unknown", $"No publication with id {id}.");

    /// <summary>
    /// Who asked. A device is recorded by its identifier rather than its display name, because §11.1's audit trail
    /// has to survive a device being renamed.
    /// </summary>
    private static string ResolveActor(HttpContext context)
    {
        if (context.User.Identity?.AuthenticationType == ServerAuthenticationPolicies.DeviceToken)
        {
            var deviceId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrWhiteSpace(deviceId) ? "device:unknown" : $"device:{deviceId}";
        }

        return context.User.Identity?.Name ?? "admin";
    }

    private static bool TryParseTargetType(string? value, out PublishTargetType type)
    {
        type = PublishTargetType.WordPress;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "wordpress":
                return true;
            case "markdown":
                type = PublishTargetType.Markdown;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseVisibility(string? value, out PublicationVisibility visibility)
    {
        visibility = PublicationVisibility.Draft;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "draft":
                return true;
            case "public":
                visibility = PublicationVisibility.Public;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseAction(string? value, out RemoteDivergenceAction action)
    {
        action = RemoteDivergenceAction.KeepBoth;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "keepboth":
            case "keep_both":
            case "keep-both":
                return true;
            case "pull":
                action = RemoteDivergenceAction.Pull;
                return true;
            case "overwrite":
                action = RemoteDivergenceAction.Overwrite;
                return true;
            default:
                return false;
        }
    }

    private static PublishTargetDto ToDto(PublishTarget target) => new(
        target.Id.ToString(),
        target.Name,
        target.Type == PublishTargetType.Markdown ? PublishTargetTypeNames.Markdown : PublishTargetTypeNames.WordPress,
        target.DestinationReference,
        target.AutomaticPublishEnabled,
        target.AutomaticPublishEnabledBy,
        target.AutomaticPublishEnabledAtUtc?.ToString("o", CultureInfo.InvariantCulture));

    private static PublicationDto ToDto(PublicationView view)
    {
        var publication = view.Publication;

        return new PublicationDto(
            publication.Id.ToString(),
            publication.ReflectionId.ToString(),
            publication.ReflectionVersionId.ToString(),
            publication.PublishTargetId.ToString(),
            view.TargetName,
            view.TargetType == PublishTargetType.Markdown ? PublishTargetTypeNames.Markdown : PublishTargetTypeNames.WordPress,
            publication.Trigger == PublicationTrigger.Manual
                ? PublicationTriggerNames.Manual
                : PublicationTriggerNames.Automatic,
            ToWireName(publication.Status),
            publication.RequestedVisibility == PublicationVisibility.Public
                ? PublicationVisibilityNames.Public
                : PublicationVisibilityNames.Draft,
            publication.RemoteId,
            publication.AttemptCount,
            publication.ScheduledAtUtc.ToString("o", CultureInfo.InvariantCulture),
            publication.TriggeredBy,
            publication.TriggeredAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            publication.CompletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            publication.ErrorCode,
            publication.ErrorSummary,

            // Read without a local hash: the endpoint reports what is known about the remote, and the full
            // comparison is what check-remote is for.
            RemoteChecked: publication.RemoteCheckedAtUtc is not null,
            LocalChanged: false,
            RemoteChanged: false,
            publication.ExportRound);
    }

    private static RemoteCheckResponse ToResponse(RemoteCheckResult result) =>
        new(
            ToDto(new PublicationView(result.Publication, result.Target.Name, result.Target.Type)) with
            {
                RemoteChecked = result.Comparison.RemoteChecked,
                LocalChanged = result.Comparison.LocalChanged,
                RemoteChanged = result.Comparison.RemoteChanged,
            },
            result.Comparison.RemoteChecked,
            result.Comparison.LocalChanged,
            result.Comparison.RemoteChanged,
            result.Remote?.Title,
            result.Remote?.Status,
            result.Remote?.Link,
            result.RemoteContentHash,
            result.LocalContentHash);

    private static string ToWireName(PublicationStatus status) => status switch
    {
        PublicationStatus.Queued => PublicationStatusNames.Queued,
        PublicationStatus.InProgress => PublicationStatusNames.InProgress,
        PublicationStatus.DraftUploaded => PublicationStatusNames.DraftUploaded,
        PublicationStatus.Published => PublicationStatusNames.Published,
        PublicationStatus.Expired => PublicationStatusNames.Expired,
        PublicationStatus.Superseded => PublicationStatusNames.Superseded,
        _ => PublicationStatusNames.Failed,
    };

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFoundTarget() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No publish target with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult NotFoundPublication() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No publication with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult MapDomainFailure(DomainException exception)
    {
        var status = exception.Code switch
        {
            "publication.status.illegal_transition" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }

    private static IResult MapUseCaseFailure(UseCaseException exception)
    {
        var status = exception.Code switch
        {
            "publish.target.unknown" or "publication.unknown" or "reflection.unknown" =>
                StatusCodes.Status404NotFound,

            // Two distinct kinds of refusal share the code space here: a permission-shaped one (the password did
            // not match) and a state-shaped one (the remote is gone, the file is not ours).
            "publish.automatic.password_rejected" => StatusCodes.Status403Forbidden,

            "publication.remote_missing" or "publication.pull.not_supported" or
                "publication.overwrite.not_applicable" or
                "publication.pull.empty" or "publish.markdown.path_escapes_root" or
                "publish.markdown.path_not_relative" => StatusCodes.Status409Conflict,

            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }
}
