using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// An in-memory stand-in for the encrypted credential store.
/// <para>
/// The encryption and the file layout are <see cref="EncryptedUiSecretStore"/>'s own business and are tested
/// against that class directly; these tests are about precedence and about which name a value is filed under,
/// which is what the in-memory form makes visible.
/// </para>
/// </summary>
internal sealed class TestUiSecretStore : IUiSecretStore
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

/// <summary>Builds an encrypted store over a throwaway instance directory, for the tests that need the real one.</summary>
internal static class TestUiSecretStoreFactory
{
    public static (EncryptedUiSecretStore Store, InstancePaths Paths) CreateReal(string rootPath)
    {
        var paths = new InstancePaths(new StorageOptions
        {
            RootPath = rootPath,
            SecretsPath = Path.Combine(rootPath, "secrets"),

            // Beside the instance root, exactly like production: the point of the store is that a backup of the
            // instance cannot carry it.
            KeyRingPath = Path.Combine(rootPath, "keys"),
        });

        paths.EnsureCreated();

        return (new EncryptedUiSecretStore(paths), paths);
    }

    /// <summary>A logger factory the file-secret tests can hand to types that only log.</summary>
    public static NullLogger<T> Logger<T>() => NullLogger<T>.Instance;
}
