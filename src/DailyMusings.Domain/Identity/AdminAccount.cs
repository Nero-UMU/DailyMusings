using System.Text.RegularExpressions;
using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Identity;

/// <summary>
/// The single administrator account (docs/开发指导.md §10.1). This product is single-user by design
/// (§3.2), so there is no user table, no registration flow and no roles.
/// </summary>
public sealed partial class AdminAccount
{
    /// <summary>The account name created on first start, before the forced rename.</summary>
    public const string InitialUsername = "admin";

    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 64;
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    private AdminAccount(
        AdminAccountId id,
        string username,
        string passwordHash,
        bool mustChangePassword,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Username = username;
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
        CreatedAtUtc = createdAtUtc;
    }

    public AdminAccountId Id { get; }

    public string Username { get; private set; }

    /// <summary>An opaque, algorithm-tagged hash. The product never stores or logs a plaintext password.</summary>
    public string PasswordHash { get; private set; }

    /// <summary>
    /// True until the operator replaces both the generated username and password (§10.1). While set,
    /// the admin UI allows nothing but the credential-change form.
    /// </summary>
    public bool MustChangePassword { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? CredentialsChangedAtUtc { get; private set; }

    public static AdminAccount CreateInitial(
        AdminAccountId id,
        string passwordHash,
        DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new AdminAccount(id, InitialUsername, passwordHash, mustChangePassword: true, at);
    }

    public static AdminAccount Rehydrate(
        AdminAccountId id,
        string username,
        string passwordHash,
        bool mustChangePassword,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? credentialsChangedAtUtc) =>
        new(id, username, passwordHash, mustChangePassword, createdAtUtc)
        {
            CredentialsChangedAtUtc = credentialsChangedAtUtc,
        };

    /// <summary>
    /// Replaces both credentials at once. The username changes together with the password because §10.1
    /// says the first login must change <em>账号和密码</em> — leaving the well-known <c>admin</c> name in
    /// place would defeat half of that.
    /// </summary>
    public void ChangeCredentials(string newUsername, string newPasswordHash, DateTimeOffset at)
    {
        ValidateUsername(newUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);

        Username = newUsername.Trim();
        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        CredentialsChangedAtUtc = at;
    }

    /// <summary>Forces a credential change at the next login (administrative recovery).</summary>
    public void RequireCredentialChange() => MustChangePassword = true;

    public static void ValidateUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var trimmed = username.Trim();
        if (trimmed.Length is < MinUsernameLength or > MaxUsernameLength)
        {
            throw new DomainException(
                "admin.username.length",
                $"A username must be between {MinUsernameLength} and {MaxUsernameLength} characters.");
        }

        if (!UsernamePattern().IsMatch(trimmed))
        {
            throw new DomainException(
                "admin.username.charset",
                "A username may contain only letters, digits, dot, underscore and hyphen.");
        }
    }

    /// <summary>
    /// Minimum bar for a user-chosen password. Strength beyond this is the password hasher's job, not a
    /// character-class checklist.
    /// </summary>
    public static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (password.Length < MinPasswordLength)
        {
            throw new DomainException(
                "admin.password.too_short",
                $"A password must be at least {MinPasswordLength} characters long.");
        }

        if (password.Length > MaxPasswordLength)
        {
            throw new DomainException(
                "admin.password.too_long",
                $"A password may not exceed {MaxPasswordLength} characters.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
