using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Reflections;
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
            update.AudioRetentionDays ?? current.AudioRetentionDays,
            update.ContentRetentionDays ?? current.ContentRetentionDays,
            update.DraftDirectory ?? current.DraftDirectory,
            update.PublishedDirectory ?? current.PublishedDirectory,
            update.HexoFrontMatterTemplate ?? current.HexoFrontMatterTemplate,
            new WritingSettings(
                update.WritingTargetCharacters ?? current.Writing.TargetCharacters,
                update.WritingPerson ?? current.Writing.Person,
                update.WritingRules ?? current.Writing.Rules));

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

        // Same shape and same reasoning as the audio window above. Kept as a separate setting rather than reusing
        // it, because "keep the recording for a month so I can check the transcript" and "delete what I said"
        // are different promises and people want different answers to them.
        if (settings.ContentRetentionDays < -1)
        {
            throw new UseCaseException(
                "content.retention.invalid",
                "Content retention must be a number of days, or -1 to keep captured content forever.");
        }


        ValidateDirectory(settings.DraftDirectory, "publish.draft_directory.invalid");
        ValidateDirectory(settings.PublishedDirectory, "publish.published_directory.invalid");
        new Domain.Publishing.MarkdownTemplate(settings.HexoFrontMatterTemplate).Validate();

        // The writing spec is the half of this record that the model actually reads, so a bad one shows up as a
        // bad article rather than as an error. Bounded here, at the only door it comes through (decision A.24).
        settings.Writing.Validate();
    }

    private static void ValidateDirectory(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            throw new UseCaseException(code, "目录必须是 Markdown 根目录下的相对路径，且不能包含 ..。");
        }
    }
}

/// <summary>Only the fields the caller actually wants to change. Absent means "leave it as it is".</summary>
public sealed record ContentSettingsUpdate(
    string? TimeZoneId,
    TimeOnly? GenerationLocalTime,
    TimeOnly? PublishLocalTime,
    int? PublishWindowMinutes,
    int? AudioRetentionDays,
    int? ContentRetentionDays = null,
    string? DraftDirectory = null,
    string? PublishedDirectory = null,
    string? HexoFrontMatterTemplate = null,
    int? WritingTargetCharacters = null,
    WritingPerson? WritingPerson = null,
    IReadOnlyList<WritingRule>? WritingRules = null);
