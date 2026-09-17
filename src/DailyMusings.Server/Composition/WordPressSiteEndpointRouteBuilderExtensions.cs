using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Publishing;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// Where a WordPress target writes, as the administrator can change it (docs/开发指导.md §8.1, §11.1).
/// <para>
/// Administrator-only: the site address and the name of the application-password secret are configuration, and the
/// API gives a device token no route to configuration. §8.1 keeps the secret's <em>value</em> out of reach entirely —
/// only its name is ever stored or returned.
/// </para>
/// </summary>
public static class WordPressSiteEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWordPressSiteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var sites = endpoints
            .MapGroup("/api/publish-targets/{id}/wordpress")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        sites.MapGet(string.Empty, GetAsync);
        sites.MapPatch(string.Empty, UpdateAsync).DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string id,
        IPublishTargetRepository targets,
        IWordPressSiteOverrideStore overrides,
        IPublishDestinationProvider destinations,
        CancellationToken cancellationToken)
    {
        if (!TryParseTargetId(id, out var targetId, out var failure))
        {
            return failure;
        }

        var target = await targets.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return NotFound();
        }

        if (target.Type != PublishTargetType.WordPress)
        {
            return NotWordPress();
        }

        var over = await overrides.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        var destination = await destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        var site = destination.RequireWordPress();

        return Results.Ok(ToDto(over, site));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        [FromBody] UpdateWordPressSiteOverrideRequest? request,
        IPublishTargetRepository targets,
        IWordPressSiteOverrideStore overrides,
        IPublishDestinationProvider destinations,
        UpdateWordPressSiteOverrideUseCase update,
        CancellationToken cancellationToken)
    {
        if (!TryParseTargetId(id, out var targetId, out var failure))
        {
            return failure;
        }

        if (request is null)
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.ValidationFailed, "A body is required."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await update
                .ExecuteAsync(
                    targetId,
                    new WordPressSiteOverride(
                        request.BaseUrl,
                        request.Username,
                        request.SecretName,
                        request.TimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Read back through the same path a publication takes, so the answer is what will really be used.
        var target = await targets.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return NotFound();
        }

        var over = await overrides.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        var destination = await destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(over, destination.RequireWordPress()));
    }

    private static bool TryParseTargetId(string id, out PublishTargetId targetId, out IResult failure)
    {
        if (Guid.TryParse(id, out var parsed))
        {
            targetId = new PublishTargetId(parsed);
            failure = Results.Empty;
            return true;
        }

        targetId = default;
        failure = NotFound();
        return false;
    }

    private static IResult NotFound() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No publish target with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult NotWordPress() =>
        Results.Json(
            new ApiError("publish.site.not_wordpress", "Only a WordPress target has a site address."),
            statusCode: StatusCodes.Status400BadRequest);

    private static WordPressSiteDto ToDto(WordPressSiteOverride over, WordPressSite site) => new(
        !over.IsEmpty,
        over.BaseUrl,
        over.Username,
        over.SecretName,
        over.TimeoutSeconds,
        site.BaseUrl,
        site.Username,
        site.SecretName,
        (int)site.Timeout.TotalSeconds);
}
