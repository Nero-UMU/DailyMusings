using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Configuration;

/// <summary>
/// Changes the instance's content and schedule settings (docs/开发指导.md §4.1, §7, §11.1, §15.1).
/// <para>
/// Every value is validated before anything is written, and the whole set is written in one transaction: a
/// half-applied schedule — a new time zone with the old generation time — would silently move when drafts appear,
/// which is the sort of change nobody notices until a day is missing.
/// </para>
/// <para>
/// Changing the content time zone deliberately does <em>not</em> recompute anything. Decision A.5 makes
/// <c>ContentDate</c> immutable once written, and that immutability is what makes "one reflection per day",
/// backfill eligibility and a published article's date all stable. The new zone applies to new captures.
/// </para>
/// </summary>
public sealed class UpdateContentSettingsUseCase
{
    private readonly IAppSettingStore _settings;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateContentSettingsUseCase(IAppSettingStore settings, IUnitOfWork unitOfWork)
    {
        _settings = settings;
        _unitOfWork = unitOfWork;
    }

    public async Task<ContentSettings> ExecuteAsync(
        ContentSettingsUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = ContentSettings.FromValues(
            await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false));

        var next = new ContentSettings(
            update.TimeZoneId ?? current.TimeZoneId,
            update.GenerationLocalTime ?? current.GenerationLocalTime,
            update.PublishLocalTime ?? current.PublishLocalTime,
            update.PublishWindowMinutes ?? current.PublishWindowMinutes,
            update.AudioRetentionDays ?? current.AudioRetentionDays);

        Validate(next);

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in next.ToValues())
        {
            await _settings.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return next;
    }

    /// <summary>
    /// Resolves the time zone and bounds the numbers. Resolving it here means an operator who mistypes a zone id
    /// gets an error at the moment they save, rather than a capture failing hours later.
    /// </summary>
    private static void Validate(ContentSettings settings)
    {
        ContentTimeZone.FromId(settings.TimeZoneId);

        if (settings.PublishWindowMinutes < 1)
        {
            throw new UseCaseException(
                "content.publish_window.invalid",
                "The publish window must be at least one minute.");
        }

        // -1 means "keep forever" (decision A.1); anything below that is a typo.
        if (settings.AudioRetentionDays < -1)
        {
            throw new UseCaseException(
                "content.retention.invalid",
                "Audio retention must be a number of days, or -1 to keep recordings forever.");
        }
    }
}

/// <summary>Only the fields the caller actually wants to change. Absent means "leave it as it is".</summary>
public sealed record ContentSettingsUpdate(
    string? TimeZoneId,
    TimeOnly? GenerationLocalTime,
    TimeOnly? PublishLocalTime,
    int? PublishWindowMinutes,
    int? AudioRetentionDays);
