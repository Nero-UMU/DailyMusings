using System.Globalization;
using System.Security.Cryptography;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 for the administrator password (docs/开发指导.md §10.1: 服务器端只保存强密码哈希).
/// <para>
/// The stored format is self-describing and algorithm-tagged, so the iteration count can be raised later
/// without invalidating existing hashes:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c>.
/// </para>
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>OWASP's floor for PBKDF2-HMAC-SHA256. Raised over time as hardware improves.</summary>
    public const int DefaultIterations = 210_000;

    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "Iterations must be positive.");
        }

        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            _iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Algorithm}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}");
    }

    public bool Verify(string password, string hash)
    {
        if (password is null || string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var parts = hash.Split('$');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], Algorithm, StringComparison.Ordinal) ||
            !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations) ||
            iterations < 1)
        {
            // A malformed stored hash is a failure to authenticate, never an exception: an exception here
            // would leak storage state to the caller and could be used to probe the database.
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
