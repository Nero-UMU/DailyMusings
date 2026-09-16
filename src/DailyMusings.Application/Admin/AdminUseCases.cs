using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;

namespace DailyMusings.Application.Admin;

/// <summary>Outcome of the first-start bootstrap. The plaintext password is returned exactly once.</summary>
public sealed record AdminInitializationResult(bool Created, string Username, string? GeneratedPassword);

/// <summary>
/// Creates the administrator account on first start (docs/开发指导.md §10.1).
/// <para>
/// The generated password is returned to the caller and <em>never</em> logged: §10.1 requires it to appear
/// only on the startup terminal. The caller (the host) owns that decision, and §16's logging rules make it
/// the only permitted destination.
/// </para>
/// </summary>
public sealed class InitializeAdminUseCase
{
    private readonly IAdminAccountRepository _accounts;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISecretGenerator _secretGenerator;
    private readonly IClock _clock;

    public InitializeAdminUseCase(
        IAdminAccountRepository accounts,
        IPasswordHasher passwordHasher,
        ISecretGenerator secretGenerator,
        IClock clock)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _secretGenerator = secretGenerator;
        _clock = clock;
    }

    public async Task<AdminInitializationResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var existing = await _accounts.GetAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new AdminInitializationResult(false, existing.Username, null);
        }

        var password = _secretGenerator.GenerateInitialPassword();
        AdminAccount.ValidatePassword(password);

        var account = AdminAccount.CreateInitial(
            AdminAccountId.New(),
            _passwordHasher.Hash(password),
            _clock.UtcNow);

        await _accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

        return new AdminInitializationResult(true, account.Username, password);
    }
}

public enum SignInOutcome
{
    Succeeded = 0,
    InvalidCredentials = 1,
    NotInitialized = 2,
}

public sealed record SignInResult(SignInOutcome Outcome, AdminAccount? Account)
{
    public bool Succeeded => Outcome == SignInOutcome.Succeeded;

    /// <summary>When true the UI must show nothing but the forced credential-change form (§10.1).</summary>
    public bool MustChangeCredentials => Account?.MustChangePassword ?? false;
}

/// <summary>Verifies administrator credentials.</summary>
public sealed class AdminSignInUseCase
{
    private readonly IAdminAccountRepository _accounts;
    private readonly IPasswordHasher _passwordHasher;

    public AdminSignInUseCase(IAdminAccountRepository accounts, IPasswordHasher passwordHasher)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
    }

    public async Task<SignInResult> ExecuteAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.GetAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new SignInResult(SignInOutcome.NotInitialized, null);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return new SignInResult(SignInOutcome.InvalidCredentials, null);
        }

        var usernameMatches = string.Equals(account.Username, username.Trim(), StringComparison.Ordinal);

        // The hash is always verified, even when the username is already known to be wrong, so that
        // response time does not distinguish "wrong name" from "wrong password".
        var passwordMatches = _passwordHasher.Verify(password, account.PasswordHash);

        return usernameMatches && passwordMatches
            ? new SignInResult(SignInOutcome.Succeeded, account)
            : new SignInResult(SignInOutcome.InvalidCredentials, null);
    }
}

/// <summary>
/// Replaces the administrator's username and password. Required before anything else on first login
/// (§10.1), and always requires the current password so a stolen session cookie is not enough.
/// </summary>
public sealed class ChangeAdminCredentialsUseCase
{
    private readonly IAdminAccountRepository _accounts;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;

    public ChangeAdminCredentialsUseCase(
        IAdminAccountRepository accounts,
        IPasswordHasher passwordHasher,
        IClock clock)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _clock = clock;
    }

    public async Task<AdminAccount> ExecuteAsync(
        string currentPassword,
        string newUsername,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("admin.not_initialized", "The administrator account does not exist yet.");

        if (!_passwordHasher.Verify(currentPassword, account.PasswordHash))
        {
            throw new UseCaseException("admin.current_password.invalid", "The current password is incorrect.");
        }

        AdminAccount.ValidateUsername(newUsername);
        AdminAccount.ValidatePassword(newPassword);

        if (string.Equals(newUsername.Trim(), account.Username, StringComparison.Ordinal) &&
            _passwordHasher.Verify(newPassword, account.PasswordHash))
        {
            throw new DomainException(
                "admin.credentials.unchanged",
                "The new credentials are identical to the current ones.");
        }

        account.ChangeCredentials(newUsername, _passwordHasher.Hash(newPassword), _clock.UtcNow);
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        return account;
    }
}
