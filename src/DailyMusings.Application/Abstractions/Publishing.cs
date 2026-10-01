using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Abstractions;

/// <summary>Persistence for the named destinations an instance publishes to (docs/开发指导.md §6.7).</summary>
public interface IPublishTargetRepository
{
    Task<IReadOnlyList<PublishTarget>> ListAsync(CancellationToken cancellationToken);

    Task<PublishTarget?> FindByIdAsync(PublishTargetId id, CancellationToken cancellationToken);

    /// <summary>Looks a target up by its name, so creating one twice does not produce two.</summary>
    Task<PublishTarget?> FindByNameAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(PublishTarget target, CancellationToken cancellationToken);

    Task UpdateAsync(PublishTarget target, CancellationToken cancellationToken);
}

/// <summary>Persistence for publication records (§6.7, §11.1).</summary>
public interface IPublicationRepository
{
    Task<Publication?> FindByIdAsync(PublicationId id, CancellationToken cancellationToken);

    /// <summary>
    /// The one publication of a version to a target. This is the read half of §14's "the same version must not
    /// publish twice to one target", and it is also how a re-export finds the remote article it created before.
    /// </summary>
    Task<Publication?> FindByVersionAndTargetAsync(
        ReflectionVersionId versionId,
        PublishTargetId targetId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Publication>> ListByReflectionAsync(ReflectionId reflectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Publications whose scheduled moment has arrived and which have not run yet. Expiry is decided by the
    /// caller, because the window is a policy the domain owns rather than a property of the row.
    /// </summary>
    Task<IReadOnlyList<Publication>> ListDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>Queued or in-flight publications, for the expiry sweep.</summary>
    Task<IReadOnlyList<Publication>> ListOutstandingAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Publication>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// 已经发表的成稿正文，供生成时当"延续"的参照（§8.4，附录 A.41 续记）。闭区间，新的一天在前。
    /// <para>
    /// 只取当前处于 <see cref="PublicationStatus.Published"/> 的那一版：被 <c>Superseded</c> 的不算博客上的
    /// 内容。同一天理论上只有一条 Published（A.21），万一有，取排在最前的那条。
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PublishedArticle>> ListPublishedArticlesAsync(
        ContentDate from,
        ContentDate to,
        CancellationToken cancellationToken);

    Task AddAsync(Publication publication, CancellationToken cancellationToken);

    Task UpdateAsync(Publication publication, CancellationToken cancellationToken);
}

/// <summary>
/// Where a target writes, resolved from configuration (docs/开发指导.md §11.2).
/// <para>
/// There is exactly one kind of destination left — a Markdown directory — so this record is the resolved
/// directory and nothing else. Resolving it is the application's business; <em>how</em> it is configured is not,
/// which is why this is a port.
/// </para>
/// </summary>
public sealed record PublishDestination(string MarkdownDirectory);

public interface IPublishDestinationProvider
{
    /// <summary>
    /// Resolves a target, or throws a use-case failure when its configuration is unusable. Failing loudly here is
    /// deliberate: a target pointed outside the mounted Markdown root must not silently write somewhere else.
    /// </summary>
    Task<PublishDestination> ResolveAsync(PublishTarget target, CancellationToken cancellationToken);
}

/// <summary>One Markdown export request, with everything the write policy needs to decide safely (§11.2).</summary>
public sealed record MarkdownWriteRequest(
    string Directory,
    string BaseName,
    string Content,

    /// <summary>The file this target wrote last time, or <c>null</c> when it has never written here.</summary>
    string? PreviousFileName,

    /// <summary>Hash of what was written last time, which is how "ours, untouched" is told from "edited by hand".</summary>
    string? PreviousContentHash,

    /// <summary>Set only when the user explicitly accepted replacing a file we wrote and nobody changed since.</summary>
    bool ReplaceExisting);

public sealed record MarkdownWriteResult(
    string FileName,
    string ContentHash,
    MarkdownWritePlan Plan,

    /// <summary>What the file holds now, when a refusal happened. Reported so the client can explain itself.</summary>
    string? ExistingContentHash);

/// <summary>Writes Markdown into a mounted directory (§11.2). Never overwrites anything it cannot prove is its own.</summary>
public interface IMarkdownWriter
{
    Task<MarkdownWriteResult> WriteAsync(MarkdownWriteRequest request, CancellationToken cancellationToken);

    Task<string?> ReadHashAsync(string directory, string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a file only while it still contains exactly what this instance last wrote. A missing file already
    /// satisfies the request; a changed file returns false and is left untouched.
    /// </summary>
    Task<bool> DeleteIfUnchangedAsync(
        string directory,
        string fileName,
        string expectedContentHash,
        CancellationToken cancellationToken);
}
