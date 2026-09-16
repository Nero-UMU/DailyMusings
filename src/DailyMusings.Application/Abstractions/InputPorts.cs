using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Abstractions;

/// <summary>Persistence for captured entries (docs/开发指导.md §6.1).</summary>
public interface IInputEntryRepository
{
    Task<InputEntry?> FindByIdAsync(InputEntryId id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the entry a replayed upload produced. This is the read half of the upload idempotency guarantee:
    /// a client that never learned whether its request landed can ask again with the same key and get the same
    /// entry back instead of creating a second one (§9.2, §14).
    /// </summary>
    Task<InputEntry?> FindByClientKeyAsync(string clientIdempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<InputEntry>> ListByContentDateAsync(ContentDate contentDate, CancellationToken cancellationToken);

    /// <summary>
    /// Everything that could be cited by a reflection on <paramref name="upToInclusive"/>: not deleted, opted
    /// into future recall, with text to work from, oldest first.
    /// <para>
    /// The boundary is expressed here, in the query, rather than left to the caller to filter afterwards — §8.3
    /// forbids citing material from after the article's day, and a rule enforced only by the code that happens
    /// to call this method is a rule a future caller can forget.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<InputEntry>> ListRecallCandidatesAsync(
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Content days that have at least one usable input, newest first. This is what the catch-up scan walks
    /// after downtime (§7: 按每条输入的实际创建日期逐日补跑) — enumerating days from the input side means a day
    /// with no input is never considered at all, so an empty article cannot be produced by accident.
    /// </summary>
    Task<IReadOnlyList<ContentDate>> ListContentDatesWithInputsAsync(
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Most recent entries across days, newest first. Used by the client's timeline.</summary>
    Task<IReadOnlyList<InputEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Every entry, oldest first, including days that never produced a draft. Used by the export and the backup:
    /// §15.1 asks for 输入 in the export, and an input on a day with no reflection is still the user's material.
    /// </summary>
    Task<IReadOnlyList<InputEntry>> ListAllAsync(int limit, CancellationToken cancellationToken);

    Task AddAsync(InputEntry entry, CancellationToken cancellationToken);

    Task UpdateAsync(InputEntry entry, CancellationToken cancellationToken);
}

/// <summary>Result of persisting an audio blob.</summary>
public sealed record StoredAudio(string RelativePath, long ByteCount, string? ContentType);

/// <summary>
/// Audio blob storage. §8.2 requires the blob to be durable <em>before</em> the server acknowledges the upload,
/// so the only ordering this port permits is: save, then record, then answer.
/// </summary>
public interface IAudioStore
{
    /// <summary>Writes the blob and returns its stored location. Throws if it cannot be written.</summary>
    Task<StoredAudio> SaveAsync(
        InputEntryId inputId,
        ContentDate contentDate,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken);

    /// <summary>Removes the blob. Idempotent: a missing blob is not an error.</summary>
    Task DeleteAsync(string storedPath, CancellationToken cancellationToken);

    /// <summary>Size in bytes, or <c>null</c> when the blob is gone.</summary>
    Task<long?> GetSizeAsync(string storedPath, CancellationToken cancellationToken);
}

/// <summary>Persistence for the durable work queue (§6.6, §14).</summary>
public interface IJobRepository
{
    Task AddAsync(ProcessingJob job, CancellationToken cancellationToken);

    Task<ProcessingJob?> FindByIdAsync(JobId id, CancellationToken cancellationToken);

    /// <summary>
    /// The single job of a given kind for a given target, which is what makes "retry this transcription"
    /// idempotent rather than additive.
    /// </summary>
    Task<ProcessingJob?> FindByTypeAndTargetAsync(JobType jobType, string targetId, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a job up by the key a caller derived from the work it wants done. Combined with the table's unique
    /// index this is what lets "generate the third draft of this day" be requested repeatedly without ever
    /// queueing the work twice (§14).
    /// </summary>
    Task<ProcessingJob?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessingJob>> ListDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessingJob>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves a pending job to running. Returns <c>false</c> when another executor won the race, so a
    /// job can never be executed twice by accident.
    /// </summary>
    Task<bool> TryClaimAsync(JobId id, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every running job to pending, and reports how many were recovered. Called once at startup: this
    /// process has just begun, so nothing can legitimately be running, and a job left running by a killed process
    /// would otherwise stay stuck forever (§14 requires restart recovery).
    /// </summary>
    Task<int> RecoverInterruptedAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task UpdateAsync(ProcessingJob job, CancellationToken cancellationToken);
}
