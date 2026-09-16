using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Reflections;

/// <summary>
/// One day's reflection — the aggregate root that owns the three version slots
/// (docs/开发指导.md §6.3/§6.4).
/// </summary>
public sealed class Reflection
{
    private Reflection(
        ReflectionId id,
        ContentDate contentDate,
        GenerationReason reason,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ContentDate = contentDate;
        GenerationReason = reason;
        Status = ReflectionStatus.PendingInputs;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public ReflectionId Id { get; }

    /// <summary>One reflection per content day, always (§7).</summary>
    public ContentDate ContentDate { get; }

    public ReflectionStatus Status { get; private set; }

    public GenerationReason GenerationReason { get; private set; }

    /// <summary>Audit field. Never used to derive <see cref="Status"/> (A.3).</summary>
    public StaleReason? LastStaleReason { get; private set; }

    /// <summary>The first version ever produced. Never rotates out and never changes once set.</summary>
    public ReflectionVersionId? InitialVersionId { get; private set; }

    /// <summary>The version the working slot held before the most recent regeneration.</summary>
    public ReflectionVersionId? PreviousVersionId { get; private set; }

    /// <summary>The version currently being edited.</summary>
    public ReflectionVersionId? WorkingVersionId { get; private set; }

    /// <summary>The last version a human confirmed. Survives regeneration so the audit trail stays intact.</summary>
    public ReflectionVersionId? ConfirmedVersionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Reflection Create(
        ReflectionId id,
        ContentDate contentDate,
        GenerationReason reason,
        DateTimeOffset createdAtUtc) => new(id, contentDate, reason, createdAtUtc);

    /// <summary>Rehydrates from storage without replaying transitions.</summary>
    public static Reflection Rehydrate(
        ReflectionId id,
        ContentDate contentDate,
        ReflectionStatus status,
        GenerationReason reason,
        StaleReason? lastStaleReason,
        ReflectionVersionId? initialVersionId,
        ReflectionVersionId? previousVersionId,
        ReflectionVersionId? workingVersionId,
        ReflectionVersionId? confirmedVersionId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        return new Reflection(id, contentDate, reason, createdAtUtc)
        {
            Status = status,
            LastStaleReason = lastStaleReason,
            InitialVersionId = initialVersionId,
            PreviousVersionId = previousVersionId,
            WorkingVersionId = workingVersionId,
            ConfirmedVersionId = confirmedVersionId,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    /// <summary>True when the version id is still reachable through one of the four slots.</summary>
    public bool References(ReflectionVersionId versionId) =>
        versionId == InitialVersionId || versionId == PreviousVersionId ||
        versionId == WorkingVersionId || versionId == ConfirmedVersionId;

    /// <summary>The day has material and is waiting for its generation slot.</summary>
    public void MarkReady(DateTimeOffset at) => Transition(ReflectionStatus.Ready, at);

    public void BeginGeneration(GenerationReason reason, DateTimeOffset at)
    {
        Transition(ReflectionStatus.Generating, at);
        GenerationReason = reason;
    }

    /// <summary>
    /// Installs a freshly generated version and rotates the slots: current working becomes previous,
    /// the new version becomes working, and the first version ever produced stays put forever.
    /// </summary>
    /// <param name="newVersionId">The newly persisted version.</param>
    /// <param name="workingVersionHasManualEdits">
    /// Whether the version currently in the working slot carries user edits. Rotation never destroys
    /// rows, but demoting edited work out of the working slot is still a decision the user must make.
    /// </param>
    /// <param name="userConfirmedOverwrite">
    /// Set only when the user explicitly accepted losing the working slot (the client must have warned
    /// them — §6.4 "不得静默覆盖用户手工编辑").
    /// </param>
    public void ApplyGeneratedVersion(
        ReflectionVersionId newVersionId,
        bool workingVersionHasManualEdits,
        bool userConfirmedOverwrite,
        DateTimeOffset at)
    {
        if (newVersionId.IsEmpty)
        {
            throw new DomainException("reflection.version.empty_id", "A generated version must have an id.");
        }

        EnsureStatus(ReflectionStatus.Generating, nameof(ApplyGeneratedVersion));

        if (WorkingVersionId is { } current)
        {
            if (workingVersionHasManualEdits && !userConfirmedOverwrite)
            {
                throw new DomainException(
                    "reflection.regeneration.overwrites_manual_edits",
                    "Regeneration would move a hand-edited version out of the working slot. " +
                    "Explicit user confirmation is required.");
            }

            PreviousVersionId = current;
        }

        InitialVersionId ??= newVersionId;
        WorkingVersionId = newVersionId;

        LastStaleReason = null;
        Transition(ReflectionStatus.ReviewRequired, at);
    }

    /// <summary>Marks the generation attempt as definitively failed (retries already exhausted).</summary>
    public void MarkGenerationFailed(DateTimeOffset at) => Transition(ReflectionStatus.Failed, at);

    /// <summary>Puts a failed draft back in line for generation without changing its versions.</summary>
    public void MarkReadyForRetry(DateTimeOffset at) => Transition(ReflectionStatus.Ready, at);

    /// <summary>Records the user's sign-off on a specific version.</summary>
    public void Confirm(ReflectionVersionId versionId, DateTimeOffset at)
    {
        if (WorkingVersionId is null || versionId != WorkingVersionId)
        {
            throw new DomainException(
                "reflection.confirm.not_working_version",
                "Only the version currently in the working slot can be confirmed.");
        }

        Transition(ReflectionStatus.Confirmed, at);
        ConfirmedVersionId = versionId;
    }

    /// <summary>
    /// Marks the draft stale because same-day material arrived after it was produced.
    /// Only reachable from <see cref="ReflectionStatus.ReviewRequired"/> or
    /// <see cref="ReflectionStatus.Confirmed"/>.
    /// </summary>
    public void MarkStaleByLateInput(DateTimeOffset at)
    {
        Transition(ReflectionStatus.StaleByLateInput, at);
        LastStaleReason = StaleReason.LateInput;
    }

    private void Transition(ReflectionStatus to, DateTimeOffset at)
    {
        ReflectionStatusTransitions.EnsureAllowed(Status, to);
        Status = to;
        UpdatedAtUtc = at;
    }

    private void EnsureStatus(ReflectionStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new DomainException(
                "reflection.status.unexpected",
                $"{operation} requires status {expected} but the draft is {Status}.");
        }
    }
}
