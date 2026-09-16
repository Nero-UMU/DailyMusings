using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;

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

    Task AddAsync(Publication publication, CancellationToken cancellationToken);

    Task UpdateAsync(Publication publication, CancellationToken cancellationToken);
}

/// <summary>Where a WordPress target lives. The secret is a <em>name</em>; its value is resolved at call time (§10.4).</summary>
public sealed record WordPressSite(string BaseUrl, string Username, string SecretName, TimeSpan Timeout)
{
    public static WordPressSite Default { get; } = new(
        BaseUrl: "http://127.0.0.1:8090",
        Username: "owner",
        SecretName: "wordpress-application-password",
        Timeout: TimeSpan.FromSeconds(30));
}

/// <summary>
/// Everything needed to reach one destination, resolved from configuration.
/// <para>
/// A WordPress target stores a configuration key in its <c>destination_reference</c>; a Markdown target stores a
/// directory. Resolving them is the application's business, but <em>how</em> they are configured is not — which
/// is why this is a port.
/// </para>
/// </summary>
public sealed record PublishDestination(
    PublishTargetType Type,
    WordPressSite? WordPress,
    string? MarkdownDirectory)
{
    public static PublishDestination ForWordPress(WordPressSite site) => new(PublishTargetType.WordPress, site, null);

    public static PublishDestination ForMarkdown(string directory) => new(PublishTargetType.Markdown, null, directory);

    /// <summary>
    /// The site, or a failure. Never a silent fallback: a WordPress target whose site is not configured must not
    /// quietly export a file instead, because the user asked for their blog.
    /// </summary>
    public WordPressSite RequireWordPress() =>
        WordPress ?? throw new UseCaseException(
            "publish.wordpress_not_configured",
            "No WordPress site is configured for this target.");
}

public interface IPublishDestinationProvider
{
    /// <summary>
    /// Resolves a target, or throws a use-case failure when its configuration is missing. Failing loudly here is
    /// deliberate: a WordPress target whose site is not configured must not quietly export a Markdown file
    /// instead, because the user asked for their blog.
    /// </summary>
    Task<PublishDestination> ResolveAsync(PublishTarget target, CancellationToken cancellationToken);
}

/// <summary>What a publisher is asked to put on the remote.</summary>
public sealed record RemoteArticleDraft(string Title, string Content, string? Slug, bool IsDraft);

/// <summary>An article as the remote reports it.</summary>
public sealed record RemoteArticle(
    string RemoteId,
    string Title,
    string Content,
    string Status,
    string? Link,
    DateTimeOffset? ModifiedAtUtc);

/// <summary>
/// A remote site that articles can be pushed to (docs/开发指导.md §11.1). Implementations must classify failures
/// as transient or permanent, because that classification is what §14's retry budget acts on.
/// <para>
/// The site is a parameter rather than state on the client: one client serves every configured target, and a
/// long-lived singleton remembering "the site I am currently talking to" would be a data race the first time two
/// publications ran at once.
/// </para>
/// </summary>
public interface IRemotePublisher
{
    /// <summary>Creates the article. Never creates a second one for the same request — see <see cref="UpdateAsync"/>.</summary>
    Task<RemoteArticle> CreateAsync(WordPressSite site, RemoteArticleDraft draft, CancellationToken cancellationToken);

    /// <summary>Pushes new content onto an article this instance created before, keeping its remote id.</summary>
    Task<RemoteArticle> UpdateAsync(
        WordPressSite site,
        string remoteId,
        RemoteArticleDraft draft,
        CancellationToken cancellationToken);

    /// <summary>Reads the article back, or <c>null</c> when it is gone.</summary>
    Task<RemoteArticle?> GetAsync(WordPressSite site, string remoteId, CancellationToken cancellationToken);
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
}
