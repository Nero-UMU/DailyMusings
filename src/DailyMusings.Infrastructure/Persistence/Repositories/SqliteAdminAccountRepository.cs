using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// There is exactly one administrator row in this product (§3.2), so every query here is a singleton read
/// or write. The UNIQUE constraint on <c>username</c> is what stops a second account from being created by
/// accident.
/// </summary>
public sealed class SqliteAdminAccountRepository : IAdminAccountRepository
{
    private const string Columns =
        "id, username, password_hash, must_change_password, created_at_utc, credentials_changed_at_utc";

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteAdminAccountRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task<AdminAccount?> GetAsync(CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM admin_account ORDER BY created_at_utc LIMIT 1;",
            Map,
            cancellationToken).ConfigureAwait(false);

    public async Task<bool> AnyAsync(CancellationToken cancellationToken) =>
        await _accessor.ExistsAsync("SELECT 1 FROM admin_account LIMIT 1;", cancellationToken).ConfigureAwait(false);

    public async Task AddAsync(AdminAccount account, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            INSERT INTO admin_account (id, username, password_hash, must_change_password, created_at_utc, credentials_changed_at_utc)
            VALUES ($id, $username, $passwordHash, $mustChange, $createdAt, $changedAt);
            """,
            cancellationToken,
            ("$id", account.Id.ToString()),
            ("$username", account.Username),
            ("$passwordHash", account.PasswordHash),
            ("$mustChange", account.MustChangePassword ? 1 : 0),
            ("$createdAt", SqliteValues.Instant(account.CreatedAtUtc)),
            ("$changedAt", SqliteValues.InstantOrNull(account.CredentialsChangedAtUtc))).ConfigureAwait(false);

    public async Task UpdateAsync(AdminAccount account, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE admin_account
               SET username = $username,
                   password_hash = $passwordHash,
                   must_change_password = $mustChange,
                   credentials_changed_at_utc = $changedAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", account.Id.ToString()),
            ("$username", account.Username),
            ("$passwordHash", account.PasswordHash),
            ("$mustChange", account.MustChangePassword ? 1 : 0),
            ("$changedAt", SqliteValues.InstantOrNull(account.CredentialsChangedAtUtc))).ConfigureAwait(false);

    private static AdminAccount Map(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        AdminAccount.Rehydrate(
            SqliteIds.AdminAccount(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            SqliteValues.ReadBool(reader, 3),
            SqliteValues.ReadRequiredInstant(reader, 4),
            SqliteValues.ReadInstant(reader, 5));
}
