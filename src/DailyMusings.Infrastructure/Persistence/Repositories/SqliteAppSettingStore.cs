using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// Instance configuration as key/value rows (docs/开发指导.md §4.1: the admin page owns content time zone
/// and schedule configuration).
/// <para>
/// Only non-secret settings live here. Model keys, SMTP passwords and WordPress application passwords are
/// resolved by name from Docker secrets and never touch this table — which is what allows a backup to be
/// handed around without carrying credentials (§10.4, decision A.13).
/// </para>
/// </summary>
public sealed class SqliteAppSettingStore : IAppSettingStore
{
    private readonly SqliteConnectionAccessor _accessor;
    private readonly IClock _clock;

    public SqliteAppSettingStore(SqliteConnectionAccessor accessor, IClock clock)
    {
        _accessor = accessor;
        _clock = clock;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return await _accessor.QuerySingleAsync(
            "SELECT value FROM app_setting WHERE setting_key = $key;",
            reader => reader.GetString(0),
            cancellationToken,
            ("$key", key)).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _accessor.ExecuteAsync(
            """
            INSERT INTO app_setting (setting_key, value, updated_at_utc)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT (setting_key) DO UPDATE SET value = excluded.value, updated_at_utc = excluded.updated_at_utc;
            """,
            cancellationToken,
            ("$key", key),
            ("$value", value),
            ("$updatedAt", SqliteValues.Instant(_clock.UtcNow))).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await _accessor.QueryAsync(
            "SELECT setting_key, value FROM app_setting;",
            reader => (Key: reader.GetString(0), Value: reader.GetString(1)),
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }
}

/// <summary>Reads the content settings snapshot, falling back to the documented defaults when unset.</summary>
public sealed class AppSettingContentSettingsProvider : IContentSettingsProvider
{
    private readonly IAppSettingStore _store;

    public AppSettingContentSettingsProvider(IAppSettingStore store) => _store = store;

    public async Task<ContentSettings> GetAsync(CancellationToken cancellationToken)
    {
        var values = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return ContentSettings.FromValues(values);
    }
}

/// <summary>
/// Supplies a calendar bound to the configured content time zone. It resolves the time zone per call rather than
/// caching it, so an operator changing the content time zone takes effect on the next capture — while already
/// stored content days stay exactly as they were (decision A.5).
/// </summary>
public sealed class AppSettingContentCalendarProvider : IContentCalendarProvider
{
    private readonly IContentSettingsProvider _settings;

    public AppSettingContentCalendarProvider(IContentSettingsProvider settings) => _settings = settings;

    public async Task<DailyMusings.Domain.Time.ContentCalendar> GetCalendarAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return settings.CreateCalendar();
    }
}
