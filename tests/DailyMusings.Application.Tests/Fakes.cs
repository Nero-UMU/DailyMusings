using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Application.Tests;

/// <summary>A clock the test owns, so time-dependent rules are exercised rather than waited on.</summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class InMemoryAdminAccountRepository : IAdminAccountRepository
{
    private readonly Dictionary<AdminAccountId, AdminAccount> _accounts = [];

    public Task<AdminAccount?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_accounts.Values.SingleOrDefault());

    public Task AddAsync(AdminAccount account, CancellationToken cancellationToken)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AdminAccount account, CancellationToken cancellationToken)
    {
        _accounts[account.Id] = account;
        return Task.CompletedTask;
    }

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => Task.FromResult(_accounts.Count > 0);
}

internal sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly Dictionary<DeviceId, Device> _devices = [];

    public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Device>>(_devices.Values.OrderBy(device => device.CreatedAtUtc).ToArray());

    public Task<Device?> FindByIdAsync(DeviceId id, CancellationToken cancellationToken) =>
        Task.FromResult(_devices.GetValueOrDefault(id));

    public Task<Device?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(_devices.Values.SingleOrDefault(device => device.TokenHash == tokenHash));

    public Task AddAsync(Device device, CancellationToken cancellationToken)
    {
        _devices[device.Id] = device;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(DeviceId id, CancellationToken cancellationToken)
    {
        _devices.Remove(id);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Device device, CancellationToken cancellationToken)
    {
        _devices[device.Id] = device;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Mirrors the guarded-update semantics of the SQLite implementation: a code can be claimed exactly once, and a
/// losing attempt writes nothing.
/// </summary>
internal sealed class InMemoryPairingCodeRepository : IPairingCodeRepository
{
    private readonly Dictionary<string, PairingCode> _codes = [];
    private readonly InMemoryDeviceRepository _devices;

    public InMemoryPairingCodeRepository(InMemoryDeviceRepository devices) => _devices = devices;

    public Task AddAsync(PairingCode code, CancellationToken cancellationToken)
    {
        _codes[code.CodeHash] = code;
        return Task.CompletedTask;
    }

    public Task<PairingCode?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken) =>
        Task.FromResult(_codes.GetValueOrDefault(codeHash));

    public async Task<bool> TryRedeemAsync(
        string codeHash,
        Device device,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (_codes.GetValueOrDefault(codeHash) is not { } code || code.IsRedeemed)
        {
            return false;
        }

        await _devices.AddAsync(device, cancellationToken).ConfigureAwait(false);
        code.Redeem(device.Id, at);
        return true;
    }
}

internal sealed class StubMigrationRunner : IMigrationRunner
{
    public Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["0001_initial"]);

    public Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["0001_initial"]);
}

internal sealed class StubHealthProbe : IHealthProbe
{
    public StubHealthProbe(string name, bool healthy) => (Name, Healthy) = (name, healthy);

    public string Name { get; }

    public bool Healthy { get; }

    public Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Healthy ? HealthProbeResult.Ok(Name) : HealthProbeResult.Fail(Name, "stub failure"));
}

/// <summary>
/// Minimal stand-ins for the security ports. The real PBKDF2 hasher and crypto generator live in
/// Infrastructure and are tested there; using them here would drag a storage dependency into a layer that is
/// supposed to have none.
/// </summary>
internal sealed class TestPasswordHasher : IPasswordHasher
{
    private const string Prefix = "hashed:";

    public string Hash(string password) => Prefix + password;

    public bool Verify(string password, string hash) =>
        !string.IsNullOrEmpty(hash) && string.Equals(hash, Prefix + password, StringComparison.Ordinal);
}

internal sealed class TestSecretGenerator : ISecretGenerator
{
    private int _counter;

    public GeneratedSecret GenerateDeviceToken()
    {
        var token = $"device-token-{++_counter}";
        return new GeneratedSecret(token, HashToken(token));
    }

    public GeneratedSecret GeneratePairingCode()
    {
        var code = $"ABCD-{++_counter:D4}";
        return new GeneratedSecret(code, HashPairingCode(code));
    }

    public string GenerateInitialPassword() => "InitialPassword123456";

    public string HashToken(string token) => "token-hash:" + token;

    /// <summary>Same normalization contract as the real generator: casing and the separator are ignored.</summary>
    public string HashPairingCode(string code) =>
        "code-hash:" + code.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

/// <summary>
/// Stands in for the encrypted store the admin page writes credentials into. Kept in memory because the
/// encryption itself is the infrastructure layer's business and is tested there; what these tests care about is
/// which name a value was filed under and whether it could be read back.
/// </summary>
internal sealed class InMemoryUiSecretStore : IUiSecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Snapshot() => _values;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

    public Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        _values[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        _values.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_values.ContainsKey(name));
}

/// <summary>Builds a use-case graph backed by the in-memory fakes above.</summary>
internal sealed class TestHarness
{
    public TestHarness(DateTimeOffset? now = null)
    {
        Clock = new FixedClock(now ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        Devices = new InMemoryDeviceRepository();
        Accounts = new InMemoryAdminAccountRepository();
        PairingCodes = new InMemoryPairingCodeRepository(Devices);
        PasswordHasher = new TestPasswordHasher();
        SecretGenerator = new TestSecretGenerator();
    }

    public FixedClock Clock { get; }

    public InMemoryAdminAccountRepository Accounts { get; }

    public InMemoryDeviceRepository Devices { get; }

    public InMemoryPairingCodeRepository PairingCodes { get; }

    public IPasswordHasher PasswordHasher { get; }

    public ISecretGenerator SecretGenerator { get; }
}
