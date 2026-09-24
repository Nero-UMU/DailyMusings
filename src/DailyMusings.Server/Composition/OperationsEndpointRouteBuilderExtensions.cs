using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Notifications;
using DailyMusings.Application.Operations;
using DailyMusings.Contracts;
using DailyMusings.Domain.Jobs;
using DailyMusings.Infrastructure.Configuration;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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
    /// <summary>How many log lines the console-style page asks for when it does not say.</summary>
    private const int DefaultLogLines = 200;

    /// <summary>An upper bound on the request, because the answer is serialised into one response.</summary>
    private const int MaxLogLines = 2000;

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
        maintenance.MapPost("/content-cleanup/run", RunContentCleanupAsync).DisableAntiforgery();

        var system = endpoints
            .MapGroup("/api/system")
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        // §8.1: a client may see the model name and whether it is on, and nothing else — the Base URL and the
        // secret's name stay with the administrator who configured them.
        endpoints
            .MapGet("/api/system/models", GetModelNamesAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOrAdmin);

        system.MapGet("/index", GetIndexAsync);
        system.MapGet("/statistics", GetStatisticsAsync);
        system.MapGet("/logs", GetLogsAsync);
        system.MapGet("/model-endpoints", GetModelEndpointsAsync);
        system.MapPatch("/model-endpoints/{service}", UpdateModelEndpointAsync);
        system.MapGet("/smtp-settings", GetSmtpSettingsAsync);
        system.MapPatch("/smtp-settings", UpdateSmtpSettingsAsync);

        // §12: one real message, so "the relay accepted our greeting" is not mistaken for "mail works".
        system.MapPost("/smtp-settings/test", SendTestEmailAsync).DisableAntiforgery();
        system.MapPost("/index/rebuild", RebuildIndexAsync).DisableAntiforgery();
        system.MapGet("/diagnostic-mode", GetDiagnosticModeAsync);
        system.MapPost("/diagnostic-mode", EnableDiagnosticModeAsync).DisableAntiforgery();
        system.MapDelete("/diagnostic-mode", DisableDiagnosticModeAsync);
        system.MapPost("/test-connection/{service}", TestConnectionAsync).DisableAntiforgery();

        // §8.1's "everything configurable from the admin page": the operational knobs that used to be reachable only
        // by editing compose and restarting, plus the listening port — the one setting that cannot be stored in the
        // settings table, and is therefore written to the bootstrap overrides file instead.
        system.MapGet("/instance-settings", GetInstanceSettingsAsync);
        system.MapPatch("/instance-settings", UpdateInstanceSettingsAsync);
        system.MapGet("/listening-port", GetListeningPort);
        system.MapPatch("/listening-port", UpdateListeningPort).DisableAntiforgery();

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

    /// <summary>
    /// Runs the content retention sweep now, for the operator who would rather not wait for the nightly slot.
    /// Enqueued like every other maintenance action, so the queue is the single place that decides when it runs.
    /// </summary>
    private static async Task<IResult> RunContentCleanupAsync(
        RunMaintenanceJobUseCase run,
        CancellationToken cancellationToken)
    {
        var job = await run.ExecuteAsync(JobType.ContentCleanup, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(job));
    }

    private static async Task<IResult> GetStatisticsAsync(
        IStatisticsReader statistics,
        CancellationToken cancellationToken)
    {
        var read = await statistics.ReadAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new StatisticsResponse(
            read.Today,
            read.TodayInputCount,
            read.TodayVoiceCount,
            read.TodayTextCount,

            // The reader reports the domain enum's name (or "none"); the API's own vocabulary is applied here,
            // at the one boundary that owns wire spellings.
            ToWireName(read.TodayReflectionStatus),
            read.TotalInputCount,
            read.TotalReflectionCount,
            read.ConfirmedReflectionCount,
            read.DraftReflectionCount,
            read.PublishedCount,
            read.PendingPublicationCount,
            read.ActiveDeviceCount,
            read.RevokedDeviceCount,
            read.TopicCount,
            read.LastGenerationAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            read.QueuePending,
            read.QueueRunning,
            read.QueueFailed,
            read.AudioRetentionDays,
            read.ContentRetentionDays,
            read.SemanticSearchAvailable,
            read.GenerationEnabled,
            read.SmtpConfigured,
            read.MediaBytes,
            read.DatabaseBytes));
    }

    /// <summary>
    /// The instance's own recent log lines (§16). Bounded by the buffer, and the requested count is bounded too:
    /// this reads an in-memory ring, and an unbounded count would be a way to ask the server to serialise
    /// everything it has.
    /// </summary>
    private static IResult GetLogsAsync(
        [FromQuery] int? lines,
        IRecentLogReader logs)
    {
        var requested = Math.Clamp(lines ?? DefaultLogLines, 1, MaxLogLines);
        var entries = logs.Read(requested);

        return Results.Ok(new LogResponse(
            entries
                .Select(entry => new LogEntryDto(
                    entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                    entry.Level,
                    entry.Category,
                    entry.Message))
                .ToArray(),
            logs.MinimumLevel));
    }

    /// <summary>
    /// Sends one real test message (§12). A failure is answered as 200 with <c>sent: false</c> and a code rather
    /// than as an HTTP error: the operator pressed a button and needs a sentence, and "the relay refused us" is
    /// a result of the test, not a failure of the request.
    /// </summary>
    private static async Task<IResult> SendTestEmailAsync(
        [FromBody] SmtpTestRequest? request,
        SendTestEmailUseCase send,
        CancellationToken cancellationToken)
    {
        var result = await send.ExecuteAsync(request?.ToAddress, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new SmtpTestResponse(result.Sent, result.Code, result.Detail));
    }

    /// <summary>Maps the reader's domain status name onto the API's wire spelling.</summary>
    private static string ToWireName(string domainStatus) => domainStatus switch
    {
        "PendingInputs" => ReflectionStatusNames.PendingInputs,
        "Ready" => ReflectionStatusNames.Ready,
        "Generating" => ReflectionStatusNames.Generating,
        "ReviewRequired" => ReflectionStatusNames.ReviewRequired,
        "Confirmed" => ReflectionStatusNames.Confirmed,
        "StaleByLateInput" => ReflectionStatusNames.StaleByLateInput,
        "Failed" => ReflectionStatusNames.Failed,

        // InstanceStatistics.NoReflection, and any status this mapping has not learned about: passed through
        // rather than guessed at, so a new state shows up as itself instead of as "failed".
        _ => domainStatus,
    };

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
                        request.Dimensions,
                        request.Password,
                        request.ClearPassword),
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
                        request.FromAddress,

                        // Two booleans, not one parsed token: the form has two checkboxes now, and "SSL and STARTTLS
                        // are both on" is a state the use case refuses with a code rather than something to parse.
                        request.UseSsl,
                        request.UseStartTls,
                        request.Password,
                        request.ClearPassword,
                        request.ToAddress),
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
        endpoint.Dimensions,
        ToWireName(endpoint.PasswordSource),
        endpoint.HasPassword);

    private static SmtpSettingsDto ToDto(SmtpSettingsView settings) => new(
        settings.Enabled,
        settings.Host,
        settings.Port,
        settings.FromAddress,
        settings.UseSsl,
        settings.UseStartTls,
        settings.ToAddress,
        settings.HasPassword,
        ToWireName(settings.PasswordSource));

    /// <summary>
    /// The password's origin, in the API's own vocabulary. <c>SecretSource</c> is an application-level enum, and
    /// the wire spelling is decided here like every other contract value.
    /// </summary>
    private static string ToWireName(SecretSource source) => source switch
    {
        SecretSource.Ui => SecretSourceNames.Ui,
        SecretSource.File => SecretSourceNames.SecretFile,
        SecretSource.Environment => SecretSourceNames.Environment,
        _ => SecretSourceNames.None,
    };

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
            return Invalid("The service must be one of transcription, generation, embedding or smtp.");
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

    private static async Task<IResult> GetInstanceSettingsAsync(
        IInstanceSettingsProvider settings,
        CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToDto(current));
    }

    private static async Task<IResult> UpdateInstanceSettingsAsync(
        [FromBody] UpdateInstanceSettingsRequest? request,
        UpdateInstanceSettingsUseCase update,
        IInstanceSettingsProvider settings,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        // Parsed here so a typo is reported against the field that has it; every other bound is checked by the use
        // case, which validates the whole set before writing any of it.
        if (!TryParseLocalTime(request.BackupLocalTime, out var backupTime) ||
            !TryParseLocalTime(request.AudioCleanupLocalTime, out var cleanupTime) ||
            !TryParseLocalTime(request.ContentCleanupLocalTime, out var contentCleanupTime))
        {
            return Invalid("备份时刻、录音清理时刻与内容清理时刻需要写成 HH:mm，例如 03:30。");
        }

        try
        {
            await update
                .ExecuteAsync(
                    new InstanceSettingsUpdate(
                        request.SchedulerIntervalSeconds,
                        request.SchedulerBackfillWindowDays,
                        request.SchedulerMaxGenerationsPerTick,
                        request.BackupEnabled,
                        backupTime,
                        cleanupTime,
                        request.BackupKeepCount,
                        request.RetrievalMaxMaterials,
                        request.RetrievalCandidateScanLimit,
                        request.RetrievalMinimumRelevance,
                        request.RetrievalMinimumLexicalScore,
                        contentCleanupTime,
                        request.InlineTranscriptionTimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            return Results.Json(
                new ApiError(exception.Code, exception.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Read back rather than echoing the request: what the instance will actually use is the answer that matters.
        return Results.Ok(ToDto(await settings.GetAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static IResult GetListeningPort(
        HttpContext context,
        IRuntimeOverridesStore overrides,
        IConfiguration configuration) =>
        Results.Ok(ToDto(overrides.Read(), context, overrides, configuration));

    /// <summary>
    /// Saves the port the instance should listen on at its next start.
    /// <para>
    /// Deliberately reports "saved, restart required" rather than "applied". A container's published mapping is
    /// fixed when the container starts, so claiming the new port is live would be the one lie that matters here —
    /// and the page prints the two commands that finish the job.
    /// </para>
    /// </summary>
    private static IResult UpdateListeningPort(
        HttpContext context,
        [FromBody] UpdateListeningPortRequest? request,
        IRuntimeOverridesStore overrides,
        IConfiguration configuration,
        IClock clock)
    {
        if (request is null)
        {
            return Invalid("A body is required.");
        }

        if (request.Port is { } port &&
            port is < RuntimeOverridesFile.MinimumPort or > RuntimeOverridesFile.MaximumPort)
        {
            return Invalid(
                $"端口需要介于 {RuntimeOverridesFile.MinimumPort} 与 {RuntimeOverridesFile.MaximumPort} 之间："
                + "低于 1024 的端口容器里的非特权用户绑定不了，那会让实例重启后连不上。");
        }

        try
        {
            overrides.Write(request.Port, context.User.Identity?.Name, clock.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(
                new ApiError(
                    "instance.port.write_failed",
                    $"无法写入 {overrides.ConfigPath}，请检查该目录的权限。"),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(ToDto(overrides.Read(), context, overrides, configuration));
    }

    /// <summary>
    /// <paramref name="request"/> absent means "leave it as it is"; a blank string means the same, so a page that
    /// posts every field keeps working.
    /// </summary>
    private static bool TryParseLocalTime(string? request, out TimeOnly? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(request))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(request, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            parsed = time;
            return true;
        }

        return false;
    }

    private static InstanceSettingsDto ToDto(InstanceSettings settings) => new(
        settings.SchedulerIntervalSeconds,
        settings.SchedulerBackfillWindowDays,
        settings.SchedulerMaxGenerationsPerTick,
        settings.BackupEnabled,
        ContentSettings.FormatTime(settings.BackupLocalTime),
        ContentSettings.FormatTime(settings.AudioCleanupLocalTime),
        ContentSettings.FormatTime(settings.ContentCleanupLocalTime),
        settings.BackupKeepCount,
        settings.RetrievalMaxMaterials,
        settings.RetrievalCandidateScanLimit,
        settings.RetrievalMinimumRelevance,
        settings.RetrievalMinimumLexicalScore,
        settings.InlineTranscriptionTimeoutSeconds);

    private static ListeningPortDto ToDto(
        RuntimeOverrides current,
        HttpContext context,
        IRuntimeOverridesStore overrides,
        IConfiguration configuration)
    {
        // The port this request actually arrived on is the only truthful "effective port": it is measured, not
        // inferred from configuration that may or may not have been honoured.
        var effective = context.Connection.LocalPort;

        return new ListeningPortDto(
            current.ListeningPort,
            effective,
            current.ListeningPort is { } port && port != effective,
            current.UpdatedBy,
            current.UpdatedAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            overrides.ConfigPath,
            configuration["Storage:RootPath"] ?? ".",
            configuration["Storage:SecretsPath"] ?? "/run/secrets",
            configuration["Storage:KeyRingPath"] ?? "keys");
    }

    private static IResult Invalid(string message) =>
        Results.Json(new ApiError(ApiErrorCodes.ValidationFailed, message), statusCode: StatusCodes.Status400BadRequest);
}
