using DailyMusings.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// A throwaway SQLite database per test. Real files, not an in-memory substitute: the migration runner, the
/// foreign keys and the guarded updates all behave differently in memory, and those are the parts worth testing.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string rootPath, SqliteConnectionAccessor accessor)
    {
        RootPath = rootPath;
        Accessor = accessor;
    }

    public string RootPath { get; }

    public SqliteConnectionAccessor Accessor { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        var accessor = new SqliteConnectionAccessor(Path.Combine(root, "dailymusings.db"));
        var database = new TestDatabase(root, accessor);

        var migrator = new SqliteMigrator(accessor, NullLogger<SqliteMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);

        return database;
    }

    /// <summary>Counts rows in a table, for asserting that a refused operation wrote nothing.</summary>
    public async Task<long> CountAsync(string table) =>
        await Accessor
            .QuerySingleAsync($"SELECT COUNT(*) FROM {table};", reader => reader.GetInt64(0), CancellationToken.None)
            .ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await Accessor.DisposeAsync().ConfigureAwait(false);

        // Release the pooled handles before deleting, or Windows keeps the file locked.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
