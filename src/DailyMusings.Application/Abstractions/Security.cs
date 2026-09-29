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
/// exported config, a log line or a backup can never leak a model key or SMTP password (§10.4).
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret's value, or <c>null</c> when it is not provisioned.</summary>
    string? TryGet(string name);

    bool Exists(string name);

    /// <summary>
    /// Where a resolved value came from. With one source this is nearly a boolean, but it is kept as an enum so
    /// "there is no such secret" stays distinguishable from "the operator cleared it" — the difference between
    /// "configure it" and "it is configured".
    /// </summary>
    SecretSource ResolveSource(string name);
}

/// <summary>
/// Where <see cref="ISecretStore.TryGet"/> found (or failed to find) a value.
/// <para>
/// There used to be three sources — the admin page, a mounted secret file, an environment variable. The two
/// deployment-side ones were removed on purpose: a key that can be injected through compose is a key that ends
/// up in plain text on a host (or in <c>docker inspect</c>), and this product's promise is that a credential
/// lives in exactly one place, encrypted. The admin page is now the only way in.
/// </para>
/// </summary>
public enum SecretSource
{
    None = 0,

    /// <summary>Written from the admin page and stored encrypted outside the backup set.</summary>
    Ui = 1,
}

/// <summary>
/// Credentials an operator types into the admin page, encrypted at rest. This is the only way a credential
/// enters the instance.
/// <para>
/// It began as the deliberate relaxation of §10.4's "secrets come from Docker Secrets" — an SMTP password that
/// can only be provisioned by editing compose and recreating the container is a password most users will not
/// set. It is now the only route, and that is strictly stronger: a value injected through compose would sit in
/// plain text on the host (and in <c>docker inspect</c> output), while this one is never in the configuration,
/// never in a log, never in an export or a backup, and never returned to a client — the store lives outside the
/// instance root (the same place, and for the same reason, as the DataProtection key ring).
/// </para>
/// </summary>
public interface IUiSecretStore
{
    /// <summary>The stored value, or <c>null</c> when the name was never set here.</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stores or replaces a value.</summary>
    Task SetAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Removes a value. Idempotent: deleting what is not there is not an error.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);
}
