namespace DailyMusings.Domain.Common;

/// <summary>
/// Raised when an operation would violate a domain invariant.
/// <para>
/// The <see cref="Code"/> is a stable, non-localized identifier. It is what reaches logs and
/// API error payloads (see docs/开发指导.md §16: only error codes and redacted summaries are
/// logged by default), while <see cref="Exception.Message"/> is developer-facing English and is
/// never surfaced to end users verbatim.
/// </para>
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Stable machine-readable identifier, e.g. <c>reflection.regeneration.overwrites_manual_edits</c>.</summary>
    public string Code { get; }
}
