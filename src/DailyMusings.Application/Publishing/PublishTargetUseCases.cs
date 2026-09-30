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
/// Turns unattended publishing on or off for one target (docs/开发指导.md §11.1, decision A.25).
/// <para>
/// Two things have to be true, and both live here rather than in a controller so they can be tested as one rule:
/// only an administrator may do it — the API gives a device token no route to this use case — and the switch
/// records who set it, so an instance that starts writing `draft: false` on its own can always be traced back to
/// a deliberate act.
/// </para>
/// <para>
/// The re-authentication §11.1 originally asked for is gone (A.25). The person who can reach this page is already
/// signed in as the only administrator the instance has, so retyping that same password bought no protection —
/// it only made the switch annoying enough to be left in whatever state it happened to be in. The risk notice
/// stays in the interface; it was always the client's job to show it.
/// </para>
/// </summary>
public sealed class SetAutomaticPublishUseCase
{
    private readonly IPublishTargetRepository _targets;
    private readonly IAdminAccountRepository _accounts;
    private readonly IClock _clock;

    public SetAutomaticPublishUseCase(
        IPublishTargetRepository targets,
        IAdminAccountRepository accounts,
        IClock clock)
    {
        _targets = targets;
        _accounts = accounts;
        _clock = clock;
    }

    public async Task<PublishTarget> ExecuteAsync(
        PublishTargetId targetId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var target = await UpdatePublishTargetUseCase
            .RequireAsync(_targets, targetId, cancellationToken)
            .ConfigureAwait(false);

        // The audit record needs a name, and §4.1 gives an instance exactly one administrator, so the account is
        // the honest source for it — better than trusting a name that arrived in the request body.
        var account = await _accounts.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("admin.not_initialized", "这个实例还没有管理员。");

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
