using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections.Sources;

namespace DailyMusings.Domain.Reflections;

public enum WritingLength
{
    Short = 0,
    Medium = 1,
    Long = 2,
}

/// <summary>The writing knobs sent with a generation request (docs/开发指导.md §8.4).</summary>
public sealed record WritingSettings(
    WritingLength Length,
    string Tone,
    bool FirstPerson,
    string? CustomInstructions)
{
    public static WritingSettings Default { get; } = new(WritingLength.Medium, "plain", true, null);
}

/// <summary>Which model produced a version. Configuration and secrets stay on the server (§8.1).</summary>
public sealed record ModelInfo(string ModelName);

/// <summary>
/// An immutable-in-spirit snapshot of one generated (or re-generated) draft
/// (docs/开发指导.md §6.4).
/// <para>
/// Version rows are never deleted — only the three slot pointers on <see cref="Reflection"/> rotate —
/// so <see cref="Reflection.ConfirmedVersionId"/> can never dangle.
/// </para>
/// </summary>
public sealed class ReflectionVersion
{
    private readonly List<string> _tags = [];
    private readonly List<string> _categories = [];
    private readonly List<SourceReference> _sources = [];
    private readonly List<UnsourcedClaim> _unsourcedClaims = [];

    private ReflectionVersion(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ReflectionId = reflectionId;
        Title = title;
        Summary = summary;
        Body = body;
        Settings = settings;
        CreatedAtUtc = createdAtUtc;
    }

    public ReflectionVersionId Id { get; }

    public ReflectionId ReflectionId { get; }

    public string Title { get; private set; }

    public string Summary { get; private set; }

    public string Body { get; private set; }

    public IReadOnlyList<string> Tags => _tags;

    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Paragraph/sentence to input mappings for this exact revision of the body.</summary>
    public IReadOnlyList<SourceReference> Sources => _sources;

    /// <summary>
    /// Sentences the second-stage check could not trace to any input (§8.4). Surfaced as highlights
    /// with a reason; never a hard block — the user may still publish after confirming.
    /// </summary>
    public IReadOnlyList<UnsourcedClaim> UnsourcedClaims => _unsourcedClaims;

    public WritingSettings Settings { get; private set; }

    public ModelInfo? ModelInfo { get; private set; }

    public string? PromptVersion { get; private set; }

    /// <summary>
    /// Set as soon as a human edits the text. Rotation refuses to move such a version out of the
    /// working slot unless the user explicitly confirmed the loss (§6.4).
    /// </summary>
    public bool HasManualEdits { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? EditedAtUtc { get; private set; }

    public IReadOnlyList<string> Paragraphs => ParagraphSplitter.Split(Body);

    public static ReflectionVersion CreateGenerated(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        ModelInfo? modelInfo,
        string? promptVersion,
        DateTimeOffset createdAtUtc,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var version = new ReflectionVersion(
            id,
            reflectionId,
            title ?? string.Empty,
            summary ?? string.Empty,
            body ?? string.Empty,
            settings,
            createdAtUtc)
        {
            ModelInfo = modelInfo,
            PromptVersion = promptVersion,
        };

        version.ReplaceTags(tags, categories);
        return version;
    }

    /// <summary>Rehydrates from storage.</summary>
    public static ReflectionVersion Rehydrate(
        ReflectionVersionId id,
        ReflectionId reflectionId,
        string title,
        string summary,
        string body,
        WritingSettings settings,
        ModelInfo? modelInfo,
        string? promptVersion,
        bool hasManualEdits,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? editedAtUtc,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? categories = null)
    {
        var version = new ReflectionVersion(id, reflectionId, title, summary, body, settings, createdAtUtc)
        {
            ModelInfo = modelInfo,
            PromptVersion = promptVersion,
            HasManualEdits = hasManualEdits,
            EditedAtUtc = editedAtUtc,
        };

        version.ReplaceTags(tags, categories);
        return version;
    }

    /// <summary>
    /// Records a human edit. This is what makes the version protected from silent regeneration, so it
    /// always flips <see cref="HasManualEdits"/> — there is no "edit without marking" overload.
    /// </summary>
    public void Edit(string title, string summary, string body, DateTimeOffset at)
    {
        Title = title ?? string.Empty;
        Summary = summary ?? string.Empty;
        Body = body ?? string.Empty;
        HasManualEdits = true;
        EditedAtUtc = at;

        // Any source mapping or drift analysis produced for the previous text is now stale by
        // definition; clearing it is what makes SourceLocator honest about drift instead of pointing at
        // the wrong sentence.
        _sources.Clear();
        _unsourcedClaims.Clear();
    }

    public void ReplaceTags(IEnumerable<string>? tags, IEnumerable<string>? categories)
    {
        _tags.Clear();
        _tags.AddRange(Clean(tags));

        _categories.Clear();
        _categories.AddRange(Clean(categories));

        static IEnumerable<string> Clean(IEnumerable<string>? values) =>
            (values ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Attaches the source map produced alongside this version's text.</summary>
    public void AttachSources(IEnumerable<SourceReference> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var incoming = sources.ToList();
        foreach (var source in incoming)
        {
            if (source.ReflectionVersionId != Id)
            {
                throw new DomainException(
                    "reflection.version.source_from_other_version",
                    "A source reference may only be attached to the version it was produced for.");
            }
        }

        _sources.Clear();
        _sources.AddRange(incoming);
    }

    /// <summary>Attaches the second-stage findings for this version's text.</summary>
    public void AttachUnsourcedClaims(IEnumerable<UnsourcedClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        _unsourcedClaims.Clear();
        _unsourcedClaims.AddRange(claims);
    }

    /// <summary>
    /// Compares each source reference against the current body and reports which ones still resolve.
    /// Called after an edit to decide between precise highlighting and the §6.5 whole-paragraph
    /// fallback.
    /// </summary>
    public IReadOnlyList<SourceDriftResult> CheckSourceDrift() =>
        _sources.Select(source => new SourceDriftResult(source, SourceLocator.Check(Body, source))).ToList();
}
