using System.Data;
using System.Globalization;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence;

/// <summary>
/// Holds the one SQLite connection a request or job uses, and the transaction it may be running in.
/// <para>
/// Registered as a scoped service. Repositories take it rather than opening their own connections, which is
/// what lets a rule spanning two aggregates — redeeming a pairing code registers a device <em>and</em>
/// burns the code — be committed as a single unit.
/// </para>
/// </summary>
public sealed class SqliteConnectionAccessor : IAsyncDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

    public SqliteConnectionAccessor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public bool HasOpenTransaction => _transaction is not null;

    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: ConnectionState.Open })
        {
            return _connection;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL keeps readers from blocking the background executor; foreign keys are off by default in
        // SQLite and the schema relies on them for cascade behaviour.
        await ExecutePragmaAsync(_connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(_connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(_connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);

        return _connection;
    }

    public async Task<SqliteTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already open on this scope.");
        }

        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        _transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        return _transaction;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("There is no open transaction to commit.");
        }

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    /// <summary>Discards the open transaction. Disposing without an explicit commit always lands here.</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    /// <summary>Builds a command already bound to the ambient transaction, if there is one.</summary>
    public async Task<SqliteCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (_transaction is not null)
        {
            command.Transaction = _transaction;
        }

        return command;
    }

    public async Task<int> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = await CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        Bind(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> QuerySingleAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = await CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        Bind(command, parameters);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? map(reader) : default;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = await CreateCommandAsync(sql, cancellationToken).ConfigureAwait(false);
        Bind(command, parameters);

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }

        return results;
    }

    /// <summary>Returns true when at least one row exists for the query.</summary>
    public async Task<bool> ExistsAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters) =>
        await QuerySingleAsync(sql, _ => true, cancellationToken, parameters).ConfigureAwait(false);

    private static void Bind(SqliteCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}

/// <summary>
/// Conversions between domain values and their SQLite representations. Centralized so that no repository
/// invents its own date format — a mismatch here would silently corrupt the §7 time rules.
/// </summary>
public static class SqliteValues
{
    public const string InstantFormat = "o";

    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset? ReadInstant(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    public static DateTimeOffset ReadRequiredInstant(SqliteDataReader reader, int ordinal) =>
        ReadInstant(reader, ordinal)!.Value;

    public static object InstantOrNull(DateTimeOffset? value) =>
        value is null ? DBNull.Value : Instant(value.Value);

    public static string? TextOrNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static object GuidOrNull(Guid? value) => value is null ? DBNull.Value : value.Value.ToString("D");

    public static bool ReadBool(SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) != 0;

    public static string ContentDay(ContentDate value) => value.ToString();

    public static ContentDate ReadContentDay(SqliteDataReader reader, int ordinal) =>
        ContentDate.Parse(reader.GetString(ordinal));
}

/// <summary>Validates a persisted identity value before it is trusted as a typed id.</summary>
public static class SqliteIds
{
    public static InputEntryId InputEntry(string value) => new(Parse(value));

    public static TopicId Topic(string value) => new(Parse(value));

    public static DeviceId Device(string value) => new(Parse(value));

    public static PairingCodeId PairingCode(string value) => new(Parse(value));

    public static AdminAccountId AdminAccount(string value) => new(Parse(value));

    public static Guid Parse(string value) =>
        Guid.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new DomainException("storage.id.malformed", "A persisted identifier is not a valid UUID.");
}
