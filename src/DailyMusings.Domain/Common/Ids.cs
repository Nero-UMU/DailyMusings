namespace DailyMusings.Domain.Common;

/// <summary>
/// Strongly-typed identifiers. Using a distinct type per aggregate root prevents the classic
/// "passed the wrong Guid" defect that survives every code review.
/// <para>
/// Values are UUID v7 so that primary keys stay time-ordered, which keeps SQLite B-tree inserts
/// append-mostly and makes index rebuilds cheap.
/// </para>
/// </summary>
public readonly record struct InputEntryId(Guid Value)
{
    public static InputEntryId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct TopicId(Guid Value)
{
    public static TopicId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReflectionId(Guid Value)
{
    public static ReflectionId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReflectionVersionId(Guid Value)
{
    public static ReflectionVersionId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct SourceReferenceId(Guid Value)
{
    public static SourceReferenceId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PublishTargetId(Guid Value)
{
    public static PublishTargetId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PublicationId(Guid Value)
{
    public static PublicationId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DeviceId(Guid Value)
{
    public static DeviceId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct PairingCodeId(Guid Value)
{
    public static PairingCodeId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public readonly record struct AdminAccountId(Guid Value)
{
    public static AdminAccountId New() => new(Guid.CreateVersion7());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}
