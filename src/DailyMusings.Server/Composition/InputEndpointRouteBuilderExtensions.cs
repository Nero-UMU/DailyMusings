using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Inputs;
using DailyMusings.Application.Jobs;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Time;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// The capture loop: upload, list, revise, delete, retry (docs/开发指导.md §8.2, §9.2, §13).
/// <para>
/// Every handler answers with a stable code on failure rather than an exception page, and none of them logs the
/// audio or the transcript (§16).
/// </para>
/// </summary>
public static class InputEndpointRouteBuilderExtensions
{
    private const int DefaultRecentLimit = 50;
    private const int MaxRecentLimit = 200;

    public static IEndpointRouteBuilder MapInputEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var inputs = endpoints
            .MapGroup("/api/inputs")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        // Form posts read the request body explicitly rather than binding IFormFile, which keeps antiforgery
        // metadata off these endpoints. They authenticate with a device bearer token, so there is no cookie to
        // forge with.
        inputs.MapPost("/voice", UploadVoiceAsync).DisableAntiforgery();
        inputs.MapPost("/text", CaptureTextAsync);
        inputs.MapGet(string.Empty, ListAsync);
        inputs.MapGet("/{id}", GetAsync);
        inputs.MapGet("/{id}/audio", GetAudioAsync);
        inputs.MapPatch("/{id}", ReviseTranscriptAsync);
        inputs.MapDelete("/{id}/audio", DeleteAudioAsync);
        inputs.MapDelete("/{id}", DeleteAsync);
        inputs.MapPost("/{id}/retry-transcription", RetryTranscriptionAsync).DisableAntiforgery();

        var jobs = endpoints
            .MapGroup("/api/jobs")
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        jobs.MapGet(string.Empty, ListJobsAsync);
        jobs.MapPost("/{id}/retry", RetryJobAsync).DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> UploadVoiceAsync(
        HttpContext context,
        IngestVoiceInputUseCase ingest,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Invalid("A multipart/form-data body is required.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var audio = form.Files[VoiceUploadFields.Audio];

        if (audio is null || audio.Length == 0)
        {
            return Invalid($"The '{VoiceUploadFields.Audio}' file part is required and must not be empty.");
        }

        if (!TryReadCaptureContext(form, out var captureContext, out var contextFailure))
        {
            return Invalid(contextFailure!);
        }

        if (TryGetDeviceId(context, out var deviceId))
        {
            captureContext = captureContext with { DeviceId = deviceId };
        }

        TimeSpan? duration = int.TryParse(
            form[VoiceUploadFields.DurationMilliseconds].ToString(),
            CultureInfo.InvariantCulture,
            out var milliseconds) && milliseconds >= 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : null;

        await using var stream = audio.OpenReadStream();

        var result = await ingest
            .ExecuteAsync(stream, audio.ContentType, duration, captureContext, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new IngestResponse(result.WasAlreadyStored, ToDto(result.Entry, result.TranscriptionJob)));
    }

    private static async Task<IResult> CaptureTextAsync(
        HttpContext context,
        [FromBody] TextInputRequest? request,
        IngestTextInputUseCase ingest,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Invalid("Text is required.");
        }

        if (!TryReadCaptureFields(
                request.CreatedAtUtc,
                request.CreatedOffsetMinutes,
                request.IdempotencyKey,
                out var captureContext,
                out var failure))
        {
            return Invalid(failure!);
        }

        if (TryGetDeviceId(context, out var deviceId))
        {
            captureContext = captureContext with { DeviceId = deviceId };
        }

        var result = await ingest
            .ExecuteAsync(request.Text, captureContext, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new IngestResponse(result.WasAlreadyStored, ToDto(result.Entry, result.TranscriptionJob)));
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? date,
        [FromQuery] int? limit,
        ListInputsUseCase listInputs,
        CancellationToken cancellationToken)
    {
        ContentDate? contentDate = null;

        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!ContentDate.TryParse(date, out var parsed))
            {
                return Invalid("The date must be formatted as YYYY-MM-DD.");
            }

            contentDate = parsed;
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultRecentLimit, 1, MaxRecentLimit);

        var views = await listInputs
            .ExecuteAsync(contentDate, effectiveLimit, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new InputListResponse(
            views.Select(view => ToDto(view.Entry, view.TranscriptionJob)).ToArray()));
    }

    private static async Task<IResult> GetAsync(
        string id,
        GetInputUseCase getInput,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        try
        {
            var view = await getInput
                .ExecuteAsync(new InputEntryId(parsed), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(view.Entry, view.TranscriptionJob));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    /// <summary>
    /// Streams the stored recording back (docs/开发指导.md §15.2 step 6, decision A.1).
    /// <para>
    /// Added in phase five. A.1 keeps audio for thirty days precisely so a transcript can be re-checked against
    /// it, and §15.2's restore verification asks for the restored instance's audio to be playable — neither is
    /// possible if the blob is reachable only from the volume on the host. Range requests are enabled because
    /// playing audio means seeking in it.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetAudioAsync(
        string id,
        GetInputUseCase getInput,
        IAudioStore audio,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        InputEntry entry;
        try
        {
            entry = (await getInput
                .ExecuteAsync(new InputEntryId(parsed), cancellationToken)
                .ConfigureAwait(false)).Entry;
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }

        if (entry.AudioPath is null)
        {
            // Deleted by the retention sweep or by the user: the entry is still there, the recording is not.
            return Results.Json(
                new ApiError("input.audio.deleted", "The audio for this entry is no longer stored."),
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var stream = await audio.OpenReadAsync(entry.AudioPath, cancellationToken).ConfigureAwait(false);

            // Ownership of the stream passes to the response, which disposes it once the body is written.
            return Results.Stream(
                stream,
                contentType: entry.AudioContentType ?? "application/octet-stream",
                enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return Results.Json(
                new ApiError("input.audio.missing", "The stored audio file is gone."),
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static async Task<IResult> ReviseTranscriptAsync(
        string id,
        [FromBody] ReviseTranscriptRequest? request,
        ReviseTranscriptUseCase revise,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        try
        {
            var entry = await revise
                .ExecuteAsync(new InputEntryId(parsed), request?.RevisedTranscript, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(entry, null));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> DeleteAudioAsync(
        string id,
        DeleteInputAudioUseCase deleteAudio,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        try
        {
            await deleteAudio.ExecuteAsync(new InputEntryId(parsed), cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        DeleteInputUseCase delete,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        try
        {
            await delete.ExecuteAsync(new InputEntryId(parsed), cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> RetryTranscriptionAsync(
        string id,
        RetryTranscriptionUseCase retry,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return NotFoundInput();
        }

        try
        {
            var view = await retry
                .ExecuteAsync(new InputEntryId(parsed), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(view.Entry, view.TranscriptionJob));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> ListJobsAsync(
        [FromQuery] int? limit,
        ListJobsUseCase listJobs,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit ?? DefaultRecentLimit, 1, MaxRecentLimit);
        var jobs = await listJobs.ExecuteAsync(effectiveLimit, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new JobListResponse(jobs.Select(view => ToDto(view.Job)).ToArray()));
    }

    private static async Task<IResult> RetryJobAsync(
        string id,
        RetryJobUseCase retry,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.NotFound, "No job with that identifier."),
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var view = await retry.ExecuteAsync(new JobId(parsed), cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToDto(view.Job));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    /// <summary>
    /// Reads the capture fields. The client's own offset is recorded but never decides the content day: that is
    /// always derived from the capture instant in the configured content time zone (§7, decision A.4).
    /// </summary>
    private static bool TryReadCaptureContext(
        IFormCollection form,
        out CaptureContext captureContext,
        out string? failure) =>
        TryReadCaptureFields(
            form[VoiceUploadFields.CreatedAtUtc].ToString(),
            int.TryParse(form[VoiceUploadFields.CreatedOffsetMinutes].ToString(), CultureInfo.InvariantCulture, out var offset)
                ? offset
                : null,
            form[VoiceUploadFields.IdempotencyKey].ToString(),
            out captureContext,
            out failure);

    private static bool TryReadCaptureFields(
        string? createdAtUtc,
        int? createdOffsetMinutes,
        string? idempotencyKey,
        out CaptureContext captureContext,
        out string? failure)
    {
        captureContext = null!;

        DateTimeOffset createdAt;
        if (string.IsNullOrWhiteSpace(createdAtUtc))
        {
            // A capture the server itself creates has no device clock to trust, so "now" is the honest answer.
            createdAt = DateTimeOffset.UtcNow;
        }
        else if (!DateTimeOffset.TryParse(
                     createdAtUtc,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.RoundtripKind,
                     out createdAt))
        {
            failure = "createdAtUtc must be an ISO-8601 instant.";
            return false;
        }

        var offset = createdOffsetMinutes ?? (int)createdAt.Offset.TotalMinutes;

        if (offset is < -840 or > 840)
        {
            failure = "createdOffsetMinutes is outside the real-world range of ±14 hours.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyKey.Length > 128)
        {
            failure = "idempotencyKey may not exceed 128 characters.";
            return false;
        }

        captureContext = new CaptureContext(
            createdAt.ToUniversalTime(),
            offset,
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            DeviceId: null);

        failure = null;
        return true;
    }

    private static bool TryGetDeviceId(HttpContext context, out DeviceId deviceId)
    {
        deviceId = default;

        // Only a device-token caller carries this claim; an administrator using the API leaves it unset.
        var claim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (context.User.Identity?.AuthenticationType != ServerAuthenticationPolicies.DeviceToken ||
            claim is null ||
            !Guid.TryParse(claim, out var parsed))
        {
            return false;
        }

        deviceId = new DeviceId(parsed);
        return true;
    }

    private static InputDto ToDto(InputEntry entry, ProcessingJob? job) => new(
        entry.Id.ToString(),
        entry.SourceType == InputSourceType.Voice ? InputSourceNames.Voice : InputSourceNames.Text,
        entry.ContentDate.ToString(),
        entry.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        entry.CreatedOffsetMinutes,
        entry.OriginalTranscript,
        entry.RevisedTranscript,
        entry.TranscriptForGeneration,
        ToWireName(entry.TranscriptionStatus),
        job?.ErrorCode ?? entry.TranscriptionErrorCode,
        entry.HasAudio,
        entry.AudioContentType,
        entry.AudioDuration?.TotalSeconds,
        entry.IsDeleted,
        job is null ? null : ToWireName(job.Status),
        job?.AttemptCount ?? 0,
        entry.PrimaryTopicId?.ToString(),
        entry.SecondaryTopicIds.Select(topicId => topicId.ToString()).ToArray());

    private static JobDto ToDto(ProcessingJob job) => new(
        job.Id.ToString(),
        job.JobType.ToString(),
        job.TargetId,
        ToWireName(job.Status),
        job.AttemptCount,
        job.ScheduledAtUtc.ToString("o", CultureInfo.InvariantCulture),
        job.StartedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        job.CompletedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        job.ErrorCode,
        job.ErrorSummary);

    private static string ToWireName(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NotApplicable => TranscriptionStatusNames.NotApplicable,
        TranscriptionStatus.Pending => TranscriptionStatusNames.Pending,
        TranscriptionStatus.InProgress => TranscriptionStatusNames.InProgress,
        TranscriptionStatus.Succeeded => TranscriptionStatusNames.Succeeded,
        _ => TranscriptionStatusNames.Failed,
    };

    private static string ToWireName(JobStatus status) => status switch
    {
        JobStatus.Pending => JobStatusNames.Pending,
        JobStatus.Running => JobStatusNames.Running,
        JobStatus.Succeeded => JobStatusNames.Succeeded,
        _ => JobStatusNames.Failed,
    };

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFoundInput() =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, "No input with that identifier."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult MapDomainFailure(DomainException exception)
    {
        var status = exception.Code switch
        {
            "input.unknown" or "job.unknown" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }
}
