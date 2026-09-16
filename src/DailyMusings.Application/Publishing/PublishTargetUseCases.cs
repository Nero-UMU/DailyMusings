using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;

namespace DailyMusings.Application.Publishing;

/// <summary>Lists the destinations an instance publishes to (§6.7, §13).</summary>
public sealed class ListPublishTargetsUseCase
{
    private readonly IPublishTargetRepository _targets;

    public ListPublishTargetsUseCase(IPublishTargetRepository targets) => _targets = targets;

    public Task<IReadOnlyList<PublishTarget>> ExecuteAsync(CancellationToken cancellationToken) =>
        _targets.ListAsync(cancellationToken);
}

/// <summary>
/// Creates a named destination. Idempotent by name: asking for the same name twice returns the existing target
/// rather than producing a second one that differs only in spelling.
/// </summary>
public sealed class CreatePublishTargetUseCase
{
    private readonly IPublishTargetRepository _targets;

    public CreatePublishTargetUseCase(IPublishTargetRepository targets) => _targets = targets;

    public async Task<(PublishTarget Target, bool Created)> ExecuteAsync(
        string name,
        PublishTargetType type,
        string? destinationReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = await _targets.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, false);
        }

        var target = PublishTarget.Create(PublishTargetId.New(), name, type, destinationReference);
        await _targets.AddAsync(target, cancellationToken).ConfigureAwait(false);

        return (target, true);
    }
}

/// <summary>Renames a target or points it somewhere else.</summary>
public sealed class UpdatePublishTargetUseCase
{
    private readonly IPublishTargetRepository _targets;

    public UpdatePublishTargetUseCase(IPublishTargetRepository targets) => _targets = targets;

    public async Task<PublishTarget> ExecuteAsync(
        PublishTargetId targetId,
        string? name,
        string? destinationReference,
        CancellationToken cancellationToken)
    {
        var target = await RequireAsync(_targets, targetId, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(name))
        {
            target.Rename(name);
        }

        if (destinationReference is not null)
        {
            target.SetDestinationReference(destinationReference);
        }

        await _targets.UpdateAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    internal static async Task<PublishTarget> RequireAsync(
        IPublishTargetRepository targets,
        PublishTargetId targetId,
        CancellationToken cancellationToken) =>
        await targets.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false)
        ?? throw new UseCaseException("publish.target.unknown", $"No publish target with id {targetId}.");
}

/// <summary>
/// Turns unattended publishing on or off for one target (docs/开发指导.md §11.1, decision A.7).
/// <para>
/// Three things have to be true at once, and all three live here rather than in a controller so they can be
/// tested as one rule: only an administrator may do it, they must re-enter their password at the moment they do
/// it, and the switch records who set it. A device token cannot reach this use case at all — the API gives it no
/// route — and it could not satisfy the password requirement if it could.
/// </para>
/// </summary>
public sealed class SetAutomaticPublishUseCase
{
    private readonly IPublishTargetRepository _targets;
    private readonly IAdminAccountRepository _accounts;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;

    public SetAutomaticPublishUseCase(
        IPublishTargetRepository targets,
        IAdminAccountRepository accounts,
        IPasswordHasher hasher,
        IClock clock)
    {
        _targets = targets;
        _accounts = accounts;
        _hasher = hasher;
        _clock = clock;
    }

    public async Task<PublishTarget> ExecuteAsync(
        PublishTargetId targetId,
        bool enabled,
        string currentPassword,
        CancellationToken cancellationToken)
    {
        var target = await UpdatePublishTargetUseCase
            .RequireAsync(_targets, targetId, cancellationToken)
            .ConfigureAwait(false);

        var account = await _accounts.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("admin.not_initialized", "The instance has no administrator yet.");

        if (string.IsNullOrEmpty(currentPassword) || !_hasher.Verify(currentPassword, account.PasswordHash))
        {
            // The re-authentication §11.1 asks for. Without it, an unattended session left open on a shared
            // machine could hand the instance the ability to publish to the public internet by itself.
            throw new UseCaseException(
                "publish.automatic.password_rejected",
                "Enabling automatic publishing requires the administrator's current password.");
        }

        if (enabled)
        {
            target.EnableAutomaticPublish(account.Username, _clock.UtcNow);
        }
        else
        {
            target.DisableAutomaticPublish();
        }

        await _targets.UpdateAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }
}
