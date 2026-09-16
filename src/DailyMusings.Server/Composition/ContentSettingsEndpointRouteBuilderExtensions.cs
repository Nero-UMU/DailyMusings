using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Contracts;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// The instance's content and schedule settings (docs/开发指导.md §4.1, §7, §11.1).
/// <para>
/// Administrator-only, like every other switch that changes what the instance does on its own. The generation time
/// and the publish time decide when drafts appear and when they may reach a blog, so they belong to the same
/// authority as the automatic-publish switch rather than to whoever holds a device token.
/// </para>
/// </summary>
public static class ContentSettingsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapContentSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var settings = endpoints
            .MapGroup("/api/content-settings")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        settings.MapGet(string.Empty, GetAsync);
        settings.MapPatch(string.Empty, UpdateAsync);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        IContentSettingsProvider provider,
        CancellationToken cancellationToken)
    {
        var settings = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(settings));
    }

    private static async Task<IResult> UpdateAsync(
        [FromBody] UpdateContentSettingsRequest? request,
        UpdateContentSettingsUseCase update,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        if (request.GenerationLocalTime is not null && !TryParseTime(request.GenerationLocalTime, out _))
        {
            return Invalid("generationLocalTime must be formatted as HH:mm.");
        }

        if (request.PublishLocalTime is not null && !TryParseTime(request.PublishLocalTime, out _))
        {
            return Invalid("publishLocalTime must be formatted as HH:mm.");
        }

        TryParseTime(request.GenerationLocalTime, out var generation);
        TryParseTime(request.PublishLocalTime, out var publish);

        try
        {
            var settings = await update
                .ExecuteAsync(
                    new ContentSettingsUpdate(
                        request.TimeZoneId,
                        request.GenerationLocalTime is null ? null : generation,
                        request.PublishLocalTime is null ? null : publish,
                        request.PublishWindowMinutes,
                        request.AudioRetentionDays),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(settings));
        }
        catch (DomainException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UseCaseException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);

    private static bool TryParseTime(string? value, out TimeOnly result) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

    private static ContentSettingsDto ToDto(ContentSettings settings) => new(
        settings.TimeZoneId,
        ContentSettings.FormatTime(settings.GenerationLocalTime),
        ContentSettings.FormatTime(settings.PublishLocalTime),
        settings.PublishWindowMinutes,
        settings.AudioRetentionDays);
}
