using DailyMusings.Client.Core;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Reflections;

/// <summary>
/// The reflection and publishing endpoints the draft screen needs (§9.3 草稿：编辑、来源核验、版本切换和发布).
/// <para>
/// Deliberately the whole set in one port: every one of them acts on one day's draft and they are always used
/// together, so splitting them would only spread the same failure classification over several files. Every call
/// answers with <see cref="ApiResult{T}"/>, so "no draft for that day" and "the server could not be reached" stay
/// distinguishable without an exception escaping a screen that is coming into view.
/// </para>
/// </summary>
public interface IReflectionApiClient
{
    Task<ApiResult<ReflectionDto>> GetAsync(string contentDate, CancellationToken cancellationToken);

    /// <summary>
    /// The drafts in a date range, for the calendar screen. The server caps a range at 366 days and defaults to 31,
    /// which is why a month is what the calendar asks for.
    /// </summary>
    Task<ApiResult<IReadOnlyList<ReflectionDto>>> ListAsync(
        string fromInclusive,
        string toInclusive,
        CancellationToken cancellationToken);

    /// <summary>Saves the hand edits. The server keeps the original version alongside them.</summary>
    Task<ApiResult<ReflectionDto>> EditAsync(
        string contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken);

    /// <summary>Moves a version into the working slot (§6.4).</summary>
    Task<ApiResult<ReflectionDto>> SwitchVersionAsync(
        string contentDate,
        string versionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the day. <paramref name="acceptedUnsourcedClaims"/> is the user's explicit acceptance of the
    /// sentences the source check could not trace — the server refuses without it (§8.4).
    /// </summary>
    Task<ApiResult<ReflectionDto>> ConfirmAsync(
        string contentDate,
        bool acceptedUnsourcedClaims,
        CancellationToken cancellationToken);

    Task<ApiResult<ReflectionGenerationResponse>> GenerateAsync(
        string contentDate,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken);

    Task<ApiResult<IReadOnlyList<PublishTargetDto>>> ListTargetsAsync(CancellationToken cancellationToken);

    Task<ApiResult<PublishResponse>> PublishAsync(
        string contentDate,
        string targetId,
        string visibility,
        bool replaceExistingFile,
        CancellationToken cancellationToken);

    Task<ApiResult<IReadOnlyList<PublicationDto>>> ListPublicationsAsync(
        string contentDate,
        CancellationToken cancellationToken);

    /// <summary>Reads the remote back and reports whether it still matches the published version (§11.1).</summary>
    Task<ApiResult<RemoteCheckResponse>> CheckRemoteAsync(
        string publicationId,
        CancellationToken cancellationToken);
}
