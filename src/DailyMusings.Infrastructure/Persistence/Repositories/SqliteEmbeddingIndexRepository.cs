using System.Runtime.InteropServices;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// The optional semantic index (docs/开发指导.md §8.3, decision A.8).
/// <para>
/// Vectors are stored as raw little-endian float32. They are never read by a person, never exported, and only
/// ever compared with other vectors from the same fingerprint — so a compact binary form is the right shape, and
/// a text form would only add a parse that can fail.
/// </para>
/// </summary>
public sealed class SqliteEmbeddingIndexRepository : IEmbeddingIndexRepository
{
    private readonly SqliteConnectionAccessor _accessor;

    public SqliteEmbeddingIndexRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(
        string configVersion,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            """
            SELECT i.id, COALESCE(i.revised_transcript, i.original_transcript)
              FROM input_entry i
             WHERE i.deleted_at_utc IS NULL
               AND COALESCE(i.revised_transcript, i.original_transcript) IS NOT NULL
               AND NOT EXISTS (
                       SELECT 1 FROM input_embedding e
                        WHERE e.input_entry_id = i.id AND e.config_version = $config)
             ORDER BY i.content_date, i.created_at_utc, i.id
             LIMIT $limit;
            """,
            reader => new PendingEmbedding(SqliteIds.InputEntry(reader.GetString(0)), reader.GetString(1)),
            cancellationToken,
            ("$config", configVersion),
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<int> CountPendingAsync(string configVersion, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            """
            SELECT COUNT(*)
              FROM input_entry i
             WHERE i.deleted_at_utc IS NULL
               AND COALESCE(i.revised_transcript, i.original_transcript) IS NOT NULL
               AND NOT EXISTS (
                       SELECT 1 FROM input_embedding e
                        WHERE e.input_entry_id = i.id AND e.config_version = $config);
            """,
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$config", configVersion)).ConfigureAwait(false);

    public async Task UpsertAsync(
        InputEntryId inputId,
        string configVersion,
        string model,
        int dimensions,
        float[] vector,
        string textHash,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vector);

        await _accessor.ExecuteAsync(
            """
            INSERT INTO input_embedding
                (input_entry_id, config_version, model_name, dimensions, vector, text_hash, created_at_utc)
            VALUES ($id, $config, $model, $dimensions, $vector, $hash, $at)
            ON CONFLICT (input_entry_id, config_version) DO UPDATE
               SET model_name = excluded.model_name,
                   dimensions = excluded.dimensions,
                   vector = excluded.vector,
                   text_hash = excluded.text_hash,
                   created_at_utc = excluded.created_at_utc;
            """,
            cancellationToken,
            ("$id", inputId.ToString()),
            ("$config", configVersion),
            ("$model", model),
            ("$dimensions", dimensions),
            ("$vector", ToBytes(vector)),
            ("$hash", textHash),
            ("$at", SqliteValues.Instant(at))).ConfigureAwait(false);
    }

    /// <summary>
    /// Vectors for material an article on <paramref name="upToInclusive"/> may cite.
    /// <para>
    /// The content-day and recall filters are applied in SQL. §8.3's boundary is the rule that keeps a reflection
    /// from describing something as having happened before it did, and enforcing it in the query means no scoring
    /// code can accidentally widen it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<StoredEmbedding>> ListVectorsAsync(
        string configVersion,
        ContentDate upToInclusive,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            """
            SELECT e.input_entry_id, e.vector
              FROM input_embedding e
              JOIN input_entry i ON i.id = e.input_entry_id
             WHERE e.config_version = $config
               AND i.deleted_at_utc IS NULL
               AND i.allow_future_recall = 1
               AND i.content_date <= $upTo
             ORDER BY i.content_date DESC, i.created_at_utc DESC
             LIMIT $limit;
            """,
            reader => new StoredEmbedding(
                SqliteIds.InputEntry(reader.GetString(0)),
                FromBytes((byte[])reader.GetValue(1))),
            cancellationToken,
            ("$config", configVersion),
            ("$upTo", SqliteValues.ContentDay(upToInclusive)),
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<int> CountAsync(string configVersion, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            "SELECT COUNT(*) FROM input_embedding WHERE config_version = $config;",
            reader => reader.GetInt32(0),
            cancellationToken,
            ("$config", configVersion)).ConfigureAwait(false);

    public async Task DeleteVersionsOtherThanAsync(string keepConfigVersion, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            "DELETE FROM input_embedding WHERE config_version <> $keep;",
            cancellationToken,
            ("$keep", keepConfigVersion)).ConfigureAwait(false);

    private static byte[] ToBytes(float[] vector) =>
        MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    private static float[] FromBytes(byte[] bytes) =>
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).ToArray();
}

/// <summary>
/// Which configuration fingerprint the stored index is complete for (decision A.8).
/// <para>
/// Held in the settings table rather than derived from the rows: a partially built index also has rows, and the
/// difference between "has some vectors" and "usable for retrieval" is exactly the difference this value makes.
/// Because it is a single write, the switch to a freshly rebuilt index is atomic.
/// </para>
/// </summary>
public sealed class AppSettingEmbeddingIndexState : IEmbeddingIndexState
{
    /// <summary>Stable key: it lives in the settings table and therefore in exports and backups.</summary>
    public const string IndexedVersionKey = "embedding.indexVersion";

    private readonly IAppSettingStore _settings;

    public AppSettingEmbeddingIndexState(IAppSettingStore settings) => _settings = settings;

    public async Task<string?> GetIndexedVersionAsync(CancellationToken cancellationToken)
    {
        var value = await _settings.GetAsync(IndexedVersionKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public Task MarkIndexedAsync(string configVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configVersion);
        return _settings.SetAsync(IndexedVersionKey, configVersion, cancellationToken);
    }
}
