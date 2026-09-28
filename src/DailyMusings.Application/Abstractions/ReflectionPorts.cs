using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// One article that is filed under a topic, in the shape the "these have to be moved first" list needs.
/// <para>
/// Carries the title because the list is useless without it — an operator has to recognize which article to
/// re-file — and the publication status because a topic in use by something already exported is a different
/// decision from one in use by a draft.
/// </para>
/// </summary>
public sealed record TopicArticleUsage(
    ReflectionId ReflectionId,
    ContentDate ContentDate,
    ReflectionVersionId VersionId,
    string Title,
    ReflectionStatus Status,
    PublicationStatus? PublicationStatus);

/// <summary>Persistence for the topic vocabulary (docs/开发指导.md §6.2, decision A.9).</summary>
public interface ITopicRepository
{
    /// <param name="includeMerged">
    /// Whether tombstones are returned. They are kept forever so that nothing which referenced them can dangle,
    /// but they must never appear as an assignment target.
    /// </param>
    Task<IReadOnlyList<Topic>> ListAsync(bool includeMerged, CancellationToken cancellationToken);

    Task<Topic?> FindByIdAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a topic up by display name, ignoring case, spacing and punctuation, so that "录音 上传" and
    /// "录音、上传" are recognized as the same topic instead of quietly becoming two.
    /// </summary>
    Task<Topic?> FindByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// The same lookup, but a name that was merged away resolves to the topic it was merged into (decision A.9)
    /// rather than reporting "no such topic".
    /// <para>
    /// This is what resolving a model's topic names uses. A tombstone must not attract new material, but the
    /// merge also said something the user meant: these two labels are one theme. Falling through to "invent a
    /// new topic with the retired name" would undo that quietly and hand the user two labels they had already
    /// decided were the same.
    /// </para>
    /// </summary>
    Task<Topic?> FindActiveByNameFollowingMergesAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(Topic topic, CancellationToken cancellationToken);

    Task UpdateAsync(Topic topic, CancellationToken cancellationToken);

    /// <summary>
    /// Re-points every input filed under <paramref name="from"/> to <paramref name="to"/> (decision A.9).
    /// <para>
    /// This is the whole of a merge. Note what it does <em>not</em> touch: <c>source_reference</c> rows point at
    /// inputs, never at topics, so historical source maps survive a merge, a rename and a retirement
    /// unchanged. §17.1 asks for that to be a fixed invariant, and it is asserted by a test.
    /// </para>
    /// </summary>
    /// <returns>How many inputs were re-filed.</returns>
    Task<int> RemapInputsAsync(TopicId from, TopicId to, CancellationToken cancellationToken);

    /// <summary>
    /// How many articles are <em>currently</em> about the topic, which is the deletion guard's query (§6.2).
    /// <para>
    /// "Currently" is the whole point: it counts the days whose working or confirmed version carries the topic,
    /// not every version that ever did. A day that has been regenerated since — or that has been re-filed away
    /// from the topic — is not a reason to keep a label the user no longer wants. Counting every version would
    /// also make a topic referenced only by the permanently retained first version undeletable forever, which is
    /// the opposite of what the guard is for.
    /// </para>
    /// <para>
    /// The article-usage questions live here rather than on <see cref="IReflectionRepository"/> because every
    /// one of them exists for the topic's sake — the version side only ever asks them about a topic it is
    /// deleting or listing. Reads and writes of a <em>version's own</em> topics are the opposite case and stay
    /// with the version, in the repository that loads it.
    /// </para>
    /// </summary>
    Task<int> CountArticleUsagesAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>Which articles currently use the topic, oldest day first, for the admin page's migration list.</summary>
    Task<IReadOnlyList<TopicArticleUsage>> ListArticleUsagesAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>How many inputs are filed under the topic (primary or secondary).</summary>
    Task<int> CountInputUsagesAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>
    /// Detaches a topic from every input, clearing the primary assignment and the secondary rows.
    /// <para>
    /// The input entries themselves are never touched: §6.2's "删除主题不得删除原始输入" means a deletion may
    /// lose the label but must not lose the material. Unfiling is the honest consequence — the alternative,
    /// silently moving everything to some other topic, would invent a decision the user did not make.
    /// </para>
    /// </summary>
    Task ClearInputAssignmentsAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every article's link to the topic, including the versions a day has since moved on from.
    /// <para>
    /// §6.2's 删除主题不得删除原始输入 is about the material, and this is its counterpart on the article side: the
    /// <em>label</em> goes, the text and the source map stay exactly as they are. The historical versions are not
    /// rewritten — they simply no longer carry a topic that no longer exists.
    /// </para>
    /// </summary>
    Task ClearVersionTopicLinksAsync(TopicId id, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the topic row. Callers must have established that no article uses it and detached every input;
    /// this method only deletes.
    /// </summary>
    Task DeleteAsync(TopicId id, CancellationToken cancellationToken);
}

/// <summary>
/// Persistence for daily reflections and their versions (docs/开发指导.md §6.3, §6.4).
/// <para>
/// Versions are never deleted — only the four slot pointers on the reflection rotate (§6.4) — so
/// <see cref="Reflection.ConfirmedVersionId"/> can never dangle and the "initial version is kept forever"
/// promise is a property of the storage model rather than of the code that remembers to keep it.
/// </para>
/// </summary>
public interface IReflectionRepository
{
    /// <summary>One reflection per content day, so this is a point lookup on a unique key.</summary>
    Task<Reflection?> FindByContentDateAsync(ContentDate contentDate, CancellationToken cancellationToken);

    Task<Reflection?> FindByIdAsync(ReflectionId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Reflection>> ListByDateRangeAsync(
        ContentDate fromInclusive,
        ContentDate toInclusive,
        CancellationToken cancellationToken);

    /// <summary>How many days in the range have a draft, so a pager can say how many pages there are.</summary>
    Task<int> CountByDateRangeAsync(
        ContentDate fromInclusive,
        ContentDate toInclusive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Reflection>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Every draft, oldest first. Used by the export and the backup, which both promise completeness.</summary>
    Task<IReadOnlyList<Reflection>> ListAllAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Days confirmed at or before <paramref name="cutoffUtc"/>, oldest confirmation first.
    /// <para>
    /// This is the retention sweep's query (decision A.1). It filters on the confirmation instant rather than on the
    /// content day, because the window starts when a human signed the day off — not when the material happened.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Reflection>> ListConfirmedBeforeAsync(DateTimeOffset cutoffUtc, int limit, CancellationToken cancellationToken);

    Task AddAsync(Reflection reflection, CancellationToken cancellationToken);

    Task UpdateAsync(Reflection reflection, CancellationToken cancellationToken);

    /// <summary>Whether a user explicitly removed this content day. Tombstones prevent the scheduler recreating it.</summary>
    Task<bool> IsDeletedAsync(ContentDate contentDate, CancellationToken cancellationToken);

    /// <summary>Deletes the article and all versions/publications, while retaining a content-date tombstone.</summary>
    Task DeleteAsync(Reflection reflection, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a version with its source map and its second-stage findings already attached, which is the shape
    /// every caller needs: sources are meaningless apart from the version they were produced for (A.6).
    /// </summary>
    Task<ReflectionVersion?> FindVersionAsync(ReflectionVersionId id, CancellationToken cancellationToken);

    /// <summary>How many versions this day has produced. This is what makes a regeneration round distinguishable.</summary>
    Task<int> CountVersionsAsync(ReflectionId reflectionId, CancellationToken cancellationToken);

    Task AddVersionAsync(ReflectionVersion version, CancellationToken cancellationToken);

    /// <summary>Persists a user edit to an existing version.</summary>
    Task UpdateVersionAsync(ReflectionVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a version's source map and findings.
    /// <para>
    /// A separate call rather than something <see cref="UpdateVersionAsync"/> does implicitly: both collections
    /// are empty on a version that was loaded without them, so "the object holds nothing" cannot be told apart
    /// from "the object was not asked for them" — and a repository that deleted rows on that ambiguity would
    /// quietly destroy provenance. The caller that knows a text change invalidated the map says so explicitly.
    /// </para>
    /// </summary>
    Task ClearSourcesAndFindingsAsync(ReflectionVersionId versionId, CancellationToken cancellationToken);

    /// <summary>Replaces this version's source map, in one transaction with the rest of the write.</summary>
    Task ReplaceSourcesAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<SourceReference> sources,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces this version's findings and stamps the check as done. Both together, because §8.4's check is
    /// only meaningful as an all-or-nothing result: an empty finding list plus a missing timestamp is
    /// "never checked", not "clean".
    /// </summary>
    Task ReplaceUnsourcedClaimsAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<UnsourcedClaim> claims,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the topics a version is about, primary first (§6.2 as revised).
    /// <para>
    /// Addressed by version rather than by day because the topics describe the article, not the calendar: a
    /// regeneration produces a new version with its own judgement, and the previous version keeps the one it
    /// was written under. Callers pass an empty list to clear a version's topics.
    /// </para>
    /// </summary>
    Task SetVersionTopicsAsync(
        ReflectionVersionId versionId,
        IReadOnlyList<TopicId> topicIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The topics of several versions at once, keyed by version and ordered primary first. Batched because a
    /// draft read loads three versions and one query per version would be a per-row query in disguise.
    /// </summary>
    Task<IReadOnlyDictionary<ReflectionVersionId, IReadOnlyList<TopicId>>> ListVersionTopicsAsync(
        IReadOnlyList<ReflectionVersionId> versionIds,
        CancellationToken cancellationToken);
}

/// <summary>One entry waiting to be embedded.</summary>
public sealed record PendingEmbedding(InputEntryId InputId, string Text);

/// <summary>One stored vector, for scoring.</summary>
public sealed record StoredEmbedding(InputEntryId InputId, float[] Vector);

/// <summary>
/// The optional semantic index (docs/开发指导.md §8.3, decision A.8).
/// <para>
/// A single generation of the index is kept: rows carry the configuration fingerprint they were built for, so
/// switching models means writing a new set of rows and then moving one pointer. That is what makes the
/// degraded path always available — the previous index stays readable until the new one is complete.
/// </para>
/// </summary>
public interface IEmbeddingIndexRepository
{
    /// <summary>Recallable entries that have text but no vector for this fingerprint yet.</summary>
    Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(
        string configVersion,
        int limit,
        CancellationToken cancellationToken);

    Task UpsertAsync(
        InputEntryId inputId,
        string configVersion,
        string model,
        int dimensions,
        float[] vector,
        string textHash,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Vectors usable for an article on <paramref name="upToInclusive"/>.
    /// <para>
    /// The content-day filter is applied in SQL rather than after scoring: §8.3 forbids citing material from
    /// after the article's day, and a boundary that only exists in application code is a boundary that a later
    /// refactor can quietly remove.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StoredEmbedding>> ListVectorsAsync(
        string configVersion,
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken);

    Task<int> CountAsync(string configVersion, CancellationToken cancellationToken);

    /// <summary>
    /// How much is still missing for this fingerprint. Used to decide whether a rebuild is done, which is why
    /// it is a count rather than a page of rows.
    /// </summary>
    Task<int> CountPendingAsync(string configVersion, CancellationToken cancellationToken);

    /// <summary>Drops rows belonging to superseded fingerprints once a rebuild has been switched over (A.8).</summary>
    Task DeleteVersionsOtherThanAsync(string keepConfigVersion, CancellationToken cancellationToken);
}
