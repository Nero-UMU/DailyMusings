using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Operations;
using DailyMusings.Contracts;
using DailyMusings.Domain.Jobs;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace DailyMusings.Server.Composition;

/// <summary>
/// The operational surface of phase five: exports, backups, restore, the retention sweep, the index and the
/// diagnostics of docs/开发指导.md §15 and §16.
/// <para>
/// Administrator-only, without exception. These endpoints read and replace the whole instance — a device token is a
/// credential for capturing thoughts, not for exporting or overwriting everything the user has.
/// </para>
/// </summary>
public static class OperationsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var exports = endpoints
            .MapGroup("/api/exports")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        exports.MapGet(string.Empty, ListExportsAsync);
        exports.MapPost(string.Empty, CreateExportAsync).DisableAntiforgery();

        var backups = endpoints
            .MapGroup("/api/backups")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        backups.MapGet(string.Empty, ListBackupsAsync);
        backups.MapPost(string.Empty, CreateBackupAsync).DisableAntiforgery();

        // Restore is an upload of an archive and a status read, because the operation itself happens at the next start.
        backups.MapGet("/restore", GetRestoreStatusAsync);
        backups.MapPost("/restore", StageRestoreAsync).DisableAntiforgery();

        var maintenance = endpoints
            .MapGroup("/api/maintenance")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        maintenance.MapPost("/backup/run", RunBackupAsync).DisableAntiforgery();
        maintenance.MapPost("/audio-cleanup/run", RunAudioCleanupAsync).DisableAntiforgery();

        var system = endpoints
            .MapGroup("/api/system")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        // §8.1: a client may see the model name and whether it is on, and nothing else — the Base URL and the
        // secret's name stay with the administrator who configured them.
        endpoints
            .MapGet("/api/system/models", GetModelNamesAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        system.MapGet("/index", GetIndexAsync);
        system.MapGet("/model-endpoints", GetModelEndpointsAsync);
        system.MapPatch("/model-endpoints/{service}", UpdateModelEndpointAsync);
        system.MapGet("/smtp-settings", GetSmtpSettingsAsync);
        system.MapPatch("/smtp-settings", UpdateSmtpSettingsAsync);
        system.MapPost("/index/rebuild", RebuildIndexAsync).DisableAntiforgery();
        system.MapGet("/diagnostic-mode", GetDiagnosticModeAsync);
        system.MapPost("/diagnostic-mode", EnableDiagnosticModeAsync).DisableAntiforgery();
        system.MapDelete("/diagnostic-mode", DisableDiagnosticModeAsync);
        system.MapPost("/test-connection/{service}", TestConnectionAsync).DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListExportsAsync(
        ListInstanceDataUseCase list,
        CancellationToken cancellationToken)
    {
        var items = await list.ListExportsAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new InstanceExportListResponse(items.Select(item => new InstanceExportDto(
            item.RelativeRoot,
            item.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            item.FileCount,
            item.TotalBytes,
            item.RetentionPolicy)).ToArray()));
    }

    private static async Task<IResult> CreateExportAsync(
        CreateExportUseCase create,
        CancellationToken cancellationToken)
    {
        var result = await create.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new InstanceExportDto(
            result.RelativeRoot,

            // The stamp is the directory name, so it is the closest thing to a creation time the result carries.
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            result.Entries.Count,
            result.TotalBytes,
            string.Empty));
    }

    private static async Task<IResult> ListBackupsAsync(
        ListInstanceDataUseCase list,
        CancellationToken cancellationToken)
    {
        var items = await list.ListBackupsAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new BackupListResponse(items.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> CreateBackupAsync(
        CreateBackupUseCase create,
        CancellationToken cancellationToken)
    {
        var summary = await create.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(summary));
    }

    private static async Task<IResult> GetRestoreStatusAsync(
        ListInstanceDataUseCase list,
        CancellationToken cancellationToken)
    {
        var pending = await list.GetPendingRestoreAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(pending));
    }

    /// <summary>
    /// Validates an uploaded backup and stages it for the next start (§15.2 step 3).
    /// <para>
    /// A staged restore is reported as accepted-with-a-caveat rather than as done: the operator has to restart the
    /// instance, and telling them otherwise would be the one lie in this feature that matters.
    /// </para>
    /// </summary>
    private static async Task<IResult> StageRestoreAsync(
        HttpContext context,
        StageRestoreUseCase stage,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Invalid("A multipart/form-data body with an 'archive' part is required.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var archive = form.Files["archive"];

        if (archive is null || archive.Length == 0)
        {
            return Invalid("The 'archive' file part is required and must not be empty.");
        }

        await using var stream = archive.OpenReadStream();
        var validation = await stage.ExecuteAsync(stream, cancellationToken).ConfigureAwait(false);

        return validation.IsValid
            ? Results.Ok(ToDto(validation))
            : Results.Json(ToDto(validation), statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> RunBackupAsync(
        RunMaintenanceJobUseCase run,
        CancellationToken cancellationToken)
    {
        var job = await run.ExecuteAsync(JobType.Backup, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(job));
    }

    private static async Task<IResult> RunAudioCleanupAsync(
        RunAudioCleanupUseCase cleanup,
        CancellationToken cancellationToken)
    {
        var result = await cleanup.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new AudioCleanupResponse(
            result.CandidateDays,
            result.DeletedBlobs,
            result.FailedDeletions,
            result.ReleasedBytes));
    }

    private static async Task<IResult> GetModelNamesAsync(
        ReadModelEndpointsUseCase read,
        CancellationToken cancellationToken)
    {
        var endpoints = await read.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ModelNameListResponse(
            endpoints.Select(endpoint => new ModelNameDto(endpoint.Service, endpoint.Model, endpoint.Enabled)).ToArray()));
    }

    private static async Task<IResult> GetModelEndpointsAsync(
        ReadModelEndpointsUseCase read,
        CancellationToken cancellationToken)
    {
        var endpoints = await read.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ModelEndpointListResponse(endpoints.Select(ToDto).ToArray()));
    }

    private static async Task<IResult> UpdateModelEndpointAsync(
        string service,
        [FromBody] UpdateModelEndpointRequest? request,
        UpdateModelEndpointUseCase update,
        ReadModelEndpointsUseCase read,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        if (!TryParseModelService(service, out var parsed))
        {
            return Invalid("The service must be one of transcription, generation or embedding.");
        }

        try
        {
            await update
                .ExecuteAsync(
                    parsed,
                    new ModelEndpointUpdate(
                        request.Enabled,
                        request.BaseUrl,
                        request.Model,
                        request.SecretName,
                        request.TimeoutSeconds,
                        request.Dimensions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: StatusCodes.Status400BadRequest);
        }

        // Read back rather than echoing the request: what the instance will actually use is the answer that matters.
        var endpoints = await read.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(endpoints.Single(endpoint => endpoint.Service == service.Trim().ToLowerInvariant())));
    }

    private static async Task<IResult> GetSmtpSettingsAsync(
        ReadSmtpSettingsUseCase read,
        CancellationToken cancellationToken) =>
        Results.Ok(ToDto(await read.ExecuteAsync(cancellationToken).ConfigureAwait(false)));

    private static async Task<IResult> UpdateSmtpSettingsAsync(
        [FromBody] UpdateSmtpSettingsRequest? request,
        UpdateSmtpSettingsUseCase update,
        ReadSmtpSettingsUseCase read,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        try
        {
            await update
                .ExecuteAsync(
                    new SmtpSettingsUpdate(
                        request.Enabled,
                        request.Host,
                        request.Port,
                        request.UseStartTls,
                        request.Username,
                        request.SecretName,
                        request.FromAddress,
                        request.FromName,
                        request.TimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(ToDto(await read.ExecuteAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static bool TryParseModelService(string? value, out ModelService service)
    {
        service = ModelService.Transcription;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "transcription":
                return true;
            case "generation":
                service = ModelService.Generation;
                return true;
            case "embedding":
                service = ModelService.Embedding;
                return true;
            default:
                return false;
        }
    }

    private static ModelEndpointDto ToDto(ModelEndpointView endpoint) => new(
        endpoint.Service,
        endpoint.Enabled,
        endpoint.BaseUrl,
        endpoint.Model,
        endpoint.SecretName,
        endpoint.TimeoutSeconds,
        endpoint.Dimensions);

    private static SmtpSettingsDto ToDto(SmtpSettingsView settings) => new(
        settings.Enabled,
        settings.Host,
        settings.Port,
        settings.UseStartTls,
        settings.Username,
        settings.SecretName,
        settings.FromAddress,
        settings.FromName,
        settings.TimeoutSeconds);

    private static async Task<IResult> GetIndexAsync(
        GetIndexStatusUseCase get,
        CancellationToken cancellationToken)
    {
        var status = await get.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new IndexStatusDto(
            status.IndexedVersion,
            status.ConfiguredVersion,
            status.IndexedEntries,
            status.SemanticSearchAvailable,
            status.RebuildInProgress));
    }

    private static async Task<IResult> RebuildIndexAsync(
        RequestIndexRebuildUseCase rebuild,
        CancellationToken cancellationToken)
    {
        var fingerprint = await rebuild.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return fingerprint is null
            ? Results.Json(
                new ApiError("index.embedding_disabled", "No embedding endpoint is configured, so there is nothing to index."),
                statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(new IndexStatusDto(fingerprint, fingerprint, 0, false, true));
    }

    private static async Task<IResult> GetDiagnosticModeAsync(
        ManageDiagnosticModeUseCase manage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var state = await manage.GetAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(state, clock.UtcNow));
    }

    /// <summary>
    /// Turns the temporary debug mode on (§16).
    /// <para>
    /// The acknowledgement travels in the body because the server cannot see whether a warning was displayed, and the
    /// guide requires one. Refusing without it is the only way this end can hold up its half of the bargain.
    /// </para>
    /// </summary>
    private static async Task<IResult> EnableDiagnosticModeAsync(
        HttpContext context,
        [FromBody] EnableDiagnosticModeRequest? request,
        ManageDiagnosticModeUseCase manage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        if (!request.AcknowledgedContentRisk)
        {
            return Results.Json(
                new ApiError(
                    "diagnostics.risk_not_acknowledged",
                    "Recording content while this mode is on may put private material in the log. "
                    + "Acknowledge that before enabling it."),
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            var state = await manage
                .EnableAsync(
                    context.User.Identity?.Name ?? "admin",
                    TimeSpan.FromMinutes(request.DurationMinutes),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ToDto(state, clock.UtcNow));
        }
        catch (UseCaseException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> DisableDiagnosticModeAsync(
        ManageDiagnosticModeUseCase manage,
        IClock clock,
        CancellationToken cancellationToken)
    {
        await manage.DisableAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(await manage.GetAsync(cancellationToken).ConfigureAwait(false), clock.UtcNow));
    }

    private static async Task<IResult> TestConnectionAsync(
        string service,
        ProbeExternalServiceUseCase probe,
        CancellationToken cancellationToken)
    {
        if (!TryParseService(service, out var parsed))
        {
            return Invalid("The service must be one of transcription, generation, embedding, smtp or wordpress.");
        }

        var result = await probe.ExecuteAsync(parsed, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ProbeResultDto(
            parsed.ToString().ToLowerInvariant(),
            result.Ok,
            result.Code,
            result.Detail));
    }

    private static bool TryParseService(string? value, out ExternalService service)
    {
        service = ExternalService.Transcription;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "transcription":
                return true;
            case "generation":
                service = ExternalService.Generation;
                return true;
            case "embedding":
                service = ExternalService.Embedding;
                return true;
            case "smtp":
                service = ExternalService.Smtp;
                return true;
            case "wordpress":
                service = ExternalService.WordPress;
                return true;
            default:
                return false;
        }
    }

    private static BackupDto ToDto(BackupSummary summary) => new(
        summary.FileName,
        summary.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        summary.ByteCount,
        summary.Sha256,
        summary.RedactedDeviceTokens,
        summary.RetentionPolicy);

    private static RestoreStatusResponse ToDto(RestoreValidation? validation) => validation is null
        ? new RestoreStatusResponse(false, false, null, null, [], null, null)
        : new RestoreStatusResponse(true, validation.IsValid, validation.Code, validation.Detail, validation.Contents, validation.SchemaVersion, validation.CreatedAtUtc);

    private static DiagnosticModeDto ToDto(DiagnosticModeState state, DateTimeOffset nowUtc) => new(
        state.Enabled,
        state.ExpiresAtUtc?.ToString("o", CultureInfo.InvariantCulture),
        state.EnabledBy,
        (int)Math.Floor(state.RemainingAt(nowUtc).TotalMinutes));

    private static MaintenanceRunResponse ToDto(ProcessingJob job) => new(
        job.JobType.ToString(),
        job.Id.ToString(),
        job.Status.ToString(),
        job.ScheduledAtUtc.ToString("o", CultureInfo.InvariantCulture));

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);
}
