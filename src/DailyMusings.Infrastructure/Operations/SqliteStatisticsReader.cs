using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Embeddings;
using DailyMusings.Application.Operations;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Storage;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// Reads the instance's counters straight from SQLite (docs/开发指导.md §16).
/// <para>
/// Written as a handful of small, obviously-correct queries rather than a dozen <c>Count</c> methods spread
/// across every repository: they are read-only aggregates over tables that already exist, they are all needed
/// at the same moment for the same screen, and giving each one a home in the repository it happens to touch
/// would add a lot of interface for no invariant. Anything that <em>is</em> an invariant already lives behind
/// the port that owns it — the semantic-search judgement, for instance, is asked of the existing use case
/// rather than re-derived here.
/// </para>
/// <para>
/// Every query is a count, a sum or a timestamp. Nothing selects text, a title or a topic name, so §16 holds by
/// construction even if someone later renders this on a page an unauthenticated caller can reach.
/// </para>
/// </summary>
public sealed class SqliteStatisticsReader : IStatisticsReader
{
    private readonly SqliteConnectionAccessor _accessor;
    private readonly IContentSettingsProvider _contentSettings;
    private readonly IGenerationSettingsProvider _generationSettings;
    private readonly ISmtpSettingsProvider _smtpSettings;
    private readonly GetSemanticSearchStateUseCase _semanticSearch;
    private readonly InstancePaths _paths;
    private readonly IClock _clock;

    public SqliteStatisticsReader(
        SqliteConnectionAccessor accessor,
        IContentSettingsProvider contentSettings,
        IGenerationSettingsProvider generationSettings,
        ISmtpSettingsProvider smtpSettings,
        GetSemanticSearchStateUseCase semanticSearch,
        InstancePaths paths,
        IClock clock)
    {
        _accessor = accessor;
        _contentSettings = contentSettings;
        _generationSettings = generationSettings;
        _smtpSettings = smtpSettings;
        _semanticSearch = semanticSearch;
        _paths = paths;
        _clock = clock;
    }

    public async Task<InstanceStatistics> ReadAsync(CancellationToken cancellationToken)
    {
        var content = await _contentSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var generation = await _generationSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var smtp = await _smtpSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var semantic = await _semanticSearch.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        var calendar = content.CreateCalendar();
        var today = calendar.ContentDateOf(_clock.UtcNow);

        return new InstanceStatistics(
            Today: today.ToString(),
            TodayInputCount: await CountInputsAsync(today, sourceType: null, cancellationToken).ConfigureAwait(false),
            TodayVoiceCount: await CountInputsAsync(today, (int)Domain.Inputs.InputSourceType.Voice, cancellationToken)
                .ConfigureAwait(false),
            TodayTextCount: await CountInputsAsync(today, (int)Domain.Inputs.InputSourceType.Text, cancellationToken)
                .ConfigureAwait(false),
            TodayReflectionStatus: await TodayReflectionStatusAsync(today, cancellationToken).ConfigureAwait(false),
            TotalInputCount: await CountAsync(
                "SELECT COUNT(*) FROM input_entry WHERE deleted_at_utc IS NULL;",
                cancellationToken).ConfigureAwait(false),
            TotalReflectionCount: await CountAsync("SELECT COUNT(*) FROM reflection;", cancellationToken)
                .ConfigureAwait(false),
            ConfirmedReflectionCount: await CountAsync(
                $"SELECT COUNT(*) FROM reflection WHERE status = {(int)ReflectionStatus.Confirmed};",
                cancellationToken).ConfigureAwait(false),
            DraftReflectionCount: await CountAsync(
                "SELECT COUNT(*) FROM reflection WHERE confirmed_version_id IS NULL;",
                cancellationToken).ConfigureAwait(false),
            PublishedCount: await PublishedCountAsync(cancellationToken).ConfigureAwait(false),
            PendingPublicationCount: await CountAsync(
                $"""
                 SELECT COUNT(*) FROM publication
                  WHERE status IN ({(int)PublicationStatus.Queued}, {(int)PublicationStatus.InProgress});
                 """,
                cancellationToken).ConfigureAwait(false),
            ActiveDeviceCount: await CountAsync(
                "SELECT COUNT(*) FROM device WHERE revoked_at_utc IS NULL;",
                cancellationToken).ConfigureAwait(false),
            RevokedDeviceCount: await CountAsync(
                "SELECT COUNT(*) FROM device WHERE revoked_at_utc IS NOT NULL;",
                cancellationToken).ConfigureAwait(false),

            // The active vocabulary: tombstones are retired labels, and counting them would make the number
            // disagree with the list the admin page shows (A.9).
            TopicCount: await CountAsync(
                "SELECT COUNT(*) FROM topic WHERE merged_into_id IS NULL;",
                cancellationToken).ConfigureAwait(false),
            LastGenerationAtUtc: await LastGenerationAsync(cancellationToken).ConfigureAwait(false),
            QueuePending: await CountJobsAsync(JobStatus.Pending, cancellationToken).ConfigureAwait(false),
            QueueRunning: await CountJobsAsync(JobStatus.Running, cancellationToken).ConfigureAwait(false),
            QueueFailed: await CountJobsAsync(JobStatus.Failed, cancellationToken).ConfigureAwait(false),
            AudioRetentionDays: content.AudioRetentionDays,
            ContentRetentionDays: content.ContentRetentionDays,
            SemanticSearchAvailable: semantic.Available,
            GenerationEnabled: generation.Enabled,
            SmtpConfigured: smtp.Enabled,
            MediaBytes: MeasureDirectory(_paths.MediaPath),
            DatabaseBytes: MeasureDatabase());
    }

    private async Task<int> CountInputsAsync(ContentDate day, int? sourceType, CancellationToken cancellationToken)
    {
        var filter = sourceType is { } type ? $" AND source_type = {type}" : string.Empty;

        return await CountAsync(
            $"SELECT COUNT(*) FROM input_entry WHERE content_date = $day AND deleted_at_utc IS NULL{filter};",
            cancellationToken,
            ("$day", SqliteValues.ContentDay(day))).ConfigureAwait(false);
    }

    /// <summary>
    /// The day's draft status, as the domain enum's name, or <see cref="InstanceStatistics.NoReflection"/>.
    /// <para>
    /// The enum's name rather than a wire spelling: mapping to the API's own vocabulary happens at the host
    /// boundary, where every other contract type is produced, so a rename cannot leave two spellings of the same
    /// status in two projects.
    /// </para>
    /// </summary>
    private async Task<string> TodayReflectionStatusAsync(ContentDate day, CancellationToken cancellationToken)
    {
        // Nullable so that "no draft yet" arrives as null rather than as status 0, which is a real status.
        var status = await _accessor.QuerySingleAsync(
            "SELECT status FROM reflection WHERE content_date = $day;",
            reader => (int?)reader.GetInt32(0),
            cancellationToken,
            ("$day", SqliteValues.ContentDay(day))).ConfigureAwait(false);

        return status is null ? InstanceStatistics.NoReflection : ((ReflectionStatus)status.Value).ToString();
    }

    /// <summary>
    /// Articles that reached a blog — counted by <em>article</em>, not by publication row, because two targets
    /// both holding the same draft is one article that was published twice, not two articles.
    /// </summary>
    private Task<int> PublishedCountAsync(CancellationToken cancellationToken) =>
        CountAsync(
            $"""
             SELECT COUNT(DISTINCT reflection_id) FROM publication
              WHERE remote_id IS NOT NULL AND status = {(int)PublicationStatus.Published};
             """,
            cancellationToken);

    private Task<DateTimeOffset?> LastGenerationAsync(CancellationToken cancellationToken) =>
        _accessor.QuerySingleAsync(
            "SELECT MAX(created_at_utc) FROM reflection_version;",
            reader => reader.IsDBNull(0)
                ? (DateTimeOffset?)null
                : SqliteValues.ReadRequiredInstant(reader, 0),
            cancellationToken);

    private Task<int> CountJobsAsync(JobStatus status, CancellationToken cancellationToken) =>
        CountAsync($"SELECT COUNT(*) FROM processing_job WHERE status = {(int)status};", cancellationToken);

    private async Task<int> CountAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters) =>
        await _accessor
            .QuerySingleAsync(sql, reader => reader.GetInt32(0), cancellationToken, parameters)
            .ConfigureAwait(false);

    /// <summary>
    /// Total size of the recordings still on disk. A directory walk rather than a stored counter: the number is
    /// only ever used to answer "how much is this instance holding", and a counter that drifted from the
    /// filesystem would be worse than a slightly slow answer.
    /// </summary>
    private static long MeasureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A file that vanished between enumeration and measurement simply does not count.
            }
        }

        return total;
    }

    /// <summary>
    /// The database file plus its write-ahead log, because WAL is where recent writes actually live and a size
    /// that ignored it would understate the instance by however much has not been checkpointed.
    /// </summary>
    private long MeasureDatabase()
    {
        long total = 0;

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _paths.DatabasePath + suffix;

            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }
}
