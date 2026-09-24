using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// The only source of "now" in the application. Injected so that every time-dependent rule (§7's
/// content days, §11.1's publish window) can be exercised deterministically in tests.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>There is exactly one administrator account in this product (§3.2), hence no id lookup.</summary>
public interface IAdminAccountRepository
{
    Task<AdminAccount?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(AdminAccount account, CancellationToken cancellationToken);

    Task UpdateAsync(AdminAccount account, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);
}

public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken);

    Task<Device?> FindByIdAsync(DeviceId id, CancellationToken cancellationToken);

    /// <summary>Looks a device up by the SHA-256 hash of its bearer token.</summary>
    Task<Device?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task AddAsync(Device device, CancellationToken cancellationToken);

    Task UpdateAsync(Device device, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the row entirely. Only ever called for a device that is already revoked: revoking is the security
    /// action (it takes effect on the next request), deleting is housekeeping — it drops a dead credential's hash
    /// out of the database so the revoked list stays a record of what happened rather than one that grows forever.
    /// </summary>
    Task DeleteAsync(DeviceId id, CancellationToken cancellationToken);
}

public interface IPairingCodeRepository
{
    Task AddAsync(PairingCode code, CancellationToken cancellationToken);

    Task<PairingCode?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken);

    /// <summary>
    /// Burns the code and registers the device in a single transaction.
    /// <para>
    /// Returns <c>false</c> when the code had already been redeemed. This is the concurrency guard that
    /// makes redemption genuinely single-use: a check-then-write in the use case alone would let two
    /// simultaneous requests both succeed.
    /// </para>
    /// </summary>
    Task<bool> TryRedeemAsync(
        string codeHash,
        Device device,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>
/// Non-secret instance configuration (content time zone, schedule times, retention). Secrets never pass
/// through here — those are resolved from Docker secrets by name (§10.4).
/// </summary>
public interface IAppSettingStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Groups several writes into one transaction. Needed wherever a rule spans two aggregates — redeeming a
/// pairing code must register the device and burn the code together, or a crash in between would leave a
/// usable code next to an already-paired device.
/// </summary>
public interface IUnitOfWork
{
    Task<ITransaction> BeginAsync(CancellationToken cancellationToken);
}

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Applies forward-only schema migrations at startup (§15.3). Deliberately has no "down": the product
/// does not promise automatic downgrades.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>Applies anything outstanding and returns the migration identifiers applied, in order.</summary>
    Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Raised for application-level failures that are not domain rule violations — wrong current password,
/// an operation attempted before the instance was initialized, and similar orchestration problems.
/// <para>
/// Named to avoid the legacy <see cref="System.ApplicationException"/>, which carries no useful semantics.
/// As with <see cref="DomainException"/>, the <see cref="Code"/> is the stable identifier that reaches
/// logs and API payloads.
/// </para>
/// </summary>
public sealed class UseCaseException : Exception
{
    public UseCaseException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
