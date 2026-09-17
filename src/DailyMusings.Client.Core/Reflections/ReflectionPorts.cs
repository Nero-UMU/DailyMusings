using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Reflections;

/// <summary>
/// One answer from the reflection endpoints, with the failure already classified
/// (docs/开发指导.md §6.3, §8.4, §11.1).
/// <para>
/// The same shape as <see cref="InputListResult"/>, for the same reason: the draft screen must be able to tell
/// "the server has no draft for that day" apart from "the server could not be reached", and neither of those may
/// be an exception on a screen that is coming into view.
/// </para>
/// </summary>
public sealed record ReflectionResult<T>(T? Value, bool ServerReached, string? FailureCode)
{
    public bool Succeeded => ServerReached && FailureCode is null && Value is not null;

    public static ReflectionResult<T> From(T value) => new(value, true, null);

    /// <summary>The request never answered, so nothing is known.</summary>
    public static ReflectionResult<T> Unreachable(string failureCode) => new(default, false, failureCode);

    /// <summary>The server answered and refused, or answered with something unusable.</summary>
    public static ReflectionResult<T> Refused(string failureCode) => new(default, true, failureCode);
}

/// <summary>
/// The reflection and publishing endpoints the draft screen needs (§9.3 草稿：编辑、来源核验、版本切换和发布).
/// <para>
/// Deliberately the whole set in one port: every one of them acts on one day's draft and they are always used
/// together, so splitting them would only spread the same failure classification over several files.
/// </para>
/// </summary>
public interface IReflectionApiClient
{
    Task<ReflectionResult<ReflectionDto>> GetAsync(string contentDate, CancellationToken cancellationToken);

    /// <summary>Saves the hand edits. The server keeps the original version alongside them.</summary>
    Task<ReflectionResult<ReflectionDto>> EditAsync(
        string contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken);

    /// <summary>Moves a version into the working slot (§6.4).</summary>
    Task<ReflectionResult<ReflectionDto>> SwitchVersionAsync(
        string contentDate,
        string versionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the day. <paramref name="acceptedUnsourcedClaims"/> is the user's explicit acceptance of the
    /// sentences the source check could not trace — the server refuses without it (§8.4).
    /// </summary>
    Task<ReflectionResult<ReflectionDto>> ConfirmAsync(
        string contentDate,
        bool acceptedUnsourcedClaims,
        CancellationToken cancellationToken);

    Task<ReflectionResult<ReflectionGenerationResponse>> GenerateAsync(
        string contentDate,
        bool ignoreTranscriptionFailures,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken);

    Task<ReflectionResult<IReadOnlyList<PublishTargetDto>>> ListTargetsAsync(CancellationToken cancellationToken);

    Task<ReflectionResult<PublishResponse>> PublishAsync(
        string contentDate,
        string targetId,
        string visibility,
        bool replaceExistingFile,
        CancellationToken cancellationToken);

    Task<ReflectionResult<IReadOnlyList<PublicationDto>>> ListPublicationsAsync(
        string contentDate,
        CancellationToken cancellationToken);

    /// <summary>Reads the remote back and reports whether it still matches the published version (§11.1).</summary>
    Task<ReflectionResult<RemoteCheckResponse>> CheckRemoteAsync(
        string publicationId,
        CancellationToken cancellationToken);
}
