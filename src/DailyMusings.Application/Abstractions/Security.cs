namespace DailyMusings.Application.Abstractions;

/// <summary>
/// Hashes the administrator password (§10.1). Slow and salted by design — the input is low-entropy, so
/// the cost is what protects it.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}

/// <summary>A freshly generated secret together with the value that gets persisted.</summary>
public sealed record GeneratedSecret(string Plaintext, string Hash);

/// <summary>
/// Generates and indexes high-entropy secrets: device bearer tokens and pairing codes (§10.2).
/// <para>
/// These are looked up on every request, so they are digested with a fast, deterministic hash rather than
/// a password KDF. That is safe here <em>because</em> the secrets are long random values — there is
/// nothing to brute-force. Passwords take the opposite trade-off; see <see cref="IPasswordHasher"/>.
/// </para>
/// </summary>
public interface ISecretGenerator
{
    /// <summary>A 256-bit device token, shown to the device exactly once.</summary>
    GeneratedSecret GenerateDeviceToken();

    /// <summary>
    /// A pairing code that a human reads off the admin screen or scans as a QR code (§10.2), so it stays
    /// short — short life plus single use is what makes that acceptable.
    /// </summary>
    GeneratedSecret GeneratePairingCode();

    /// <summary>A cryptographically random initial administrator password (§10.1).</summary>
    string GenerateInitialPassword();

    /// <summary>The lookup hash for a device token. Case-sensitive: base64url is not case-insensitive.</summary>
    string HashToken(string token);

    /// <summary>
    /// The lookup hash for a pairing code, normalized first so that a human retyping it with different
    /// casing or with the visual separator gets the same result.
    /// </summary>
    string HashPairingCode(string code);
}

/// <summary>
/// Resolves secrets by <em>name</em>. Configuration stores only the name or path — never the value — so an
/// exported config, a log line or a backup can never leak a model key, SMTP password or WordPress
/// application password (§10.4).
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret's value, or <c>null</c> when it is not provisioned.</summary>
    string? TryGet(string name);

    bool Exists(string name);

    /// <summary>Names of the secrets currently provisioned. Safe to show: names are not secrets.</summary>
    IReadOnlyList<string> ListNames();
}
