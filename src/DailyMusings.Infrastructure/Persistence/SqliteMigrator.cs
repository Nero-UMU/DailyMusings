using System.Globalization;
using System.Reflection;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Persistence;

/// <summary>
/// Applies the embedded forward-only migrations at startup (docs/开发指导.md §15.3).
/// <para>
/// Deliberately has no "down" path: the product promises automatic forward migration and explicitly does
/// not promise automatic downgrade, so the honest implementation is the smaller one — keep the old image
/// and the backup.
/// </para>
/// </summary>
public sealed class SqliteMigrator : IMigrationRunner
{
    private const string ResourceSuffix = ".sql";

    private readonly SqliteConnectionAccessor _accessor;
    private readonly ILogger<SqliteMigrator> _logger;

    public SqliteMigrator(SqliteConnectionAccessor accessor, ILogger<SqliteMigrator> logger)
    {
        _accessor = accessor;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken cancellationToken)
    {
        var connection = await _accessor.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    migration_id   TEXT NOT NULL PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _accessor
            .QueryAsync(
                "SELECT migration_id FROM schema_migrations ORDER BY migration_id;",
                reader => reader.GetString(0),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        var applied = (await GetAppliedAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var appliedNow = new List<string>();

        foreach (var (id, sql) in LoadMigrations())
        {
            if (applied.Contains(id))
            {
                continue;
            }

            // Each migration is its own transaction: a failure half way through the set leaves the
            // instance on a consistent, known schema rather than a partially upgraded one.
            await using var transaction = await _accessor.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await _accessor.ExecuteAsync(sql, cancellationToken).ConfigureAwait(false);
            await _accessor.ExecuteAsync(
                "INSERT INTO schema_migrations (migration_id, applied_at_utc) VALUES ($id, $at);",
                cancellationToken,
                ("$id", id),
                ("$at", SqliteValues.Instant(DateTimeOffset.UtcNow))).ConfigureAwait(false);

            await _accessor.CommitAsync(cancellationToken).ConfigureAwait(false);

            appliedNow.Add(id);

            // Only the identifier is logged — never schema contents or data (§16).
            _logger.LogInformation("Applied database migration {MigrationId}.", id);
        }

        return appliedNow;
    }

    /// <summary>Migration scripts embedded in this assembly, ordered by their numeric prefix.</summary>
    private static IEnumerable<(string Id, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteMigrator).Assembly;

        return assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => (Id: ToMigrationId(name), Sql: ReadResource(assembly, name)))
            .ToArray();
    }

    private static string ToMigrationId(string resourceName)
    {
        // DailyMusings.Infrastructure.Persistence.Migrations.0001_initial.sql -> 0001_initial
        var trimmed = resourceName[..^ResourceSuffix.Length];
        var lastSeparator = trimmed.LastIndexOf('.');
        return lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Embedded migration '{name}' could not be read."));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
