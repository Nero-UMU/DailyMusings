using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Reflections;

/// <summary>One citation, resolved and already checked for drift against the current text (A.6).</summary>
public sealed record SourceReferenceView(
    InputEntryId InputId,
    int BlockIndex,
    int CharStart,
    int CharEnd,
    double Relevance,
    string Reason,
    bool IsHistorical,
    SourceDrift Drift);

/// <summary>A sentence the second-stage check could not trace to any input (§8.4).</summary>
public sealed record UnsourcedClaimView(int BlockIndex, int CharStart, int CharEnd, string Reason);

public sealed record ReflectionVersionView(
    ReflectionVersionId Id,
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    bool HasManualEdits,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EditedAtUtc,
    string? ModelName,
    string? PromptVersion,
    DateTimeOffset? SourcesCheckedAtUtc,
    IReadOnlyList<SourceReferenceView> Sources,
    IReadOnlyList<UnsourcedClaimView> UnsourcedClaims)
{
    public static ReflectionVersionView From(ReflectionVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new ReflectionVersionView(
            version.Id,
            version.Title,
            version.Summary,
            version.Body,
            version.Tags,
            version.Categories,
            version.HasManualEdits,
            version.CreatedAtUtc,
            version.EditedAtUtc,
            version.ModelInfo?.ModelName,
            version.PromptVersion,
            version.SourcesCheckedAtUtc,

            // Drift is computed per read, against the text as it stands now: the client must never highlight a
            // range that the stored hash no longer matches (§6.5). It can never fail — a mismatch downgrades to
            // a whole-paragraph hint rather than disappearing.
            version.CheckSourceDrift()
                .Select(result => new SourceReferenceView(
                    result.Source.InputId,
                    result.Source.BlockIndex,
                    result.Source.CharStart,
                    result.Source.CharEnd,
                    result.Source.Relevance,
                    result.Source.Reason,
                    result.Source.IsHistorical,
                    result.Drift))
                .ToArray(),

            version.UnsourcedClaims
                .Select(claim => new UnsourcedClaimView(claim.BlockIndex, claim.CharStart, claim.CharEnd, claim.Reason))
                .ToArray());
    }
}

/// <summary>
/// A day's draft as the client sees it (§9.3 草稿页): the status, the four slot pointers and the three versions
/// the user can switch between.
/// </summary>
public sealed record ReflectionView(
    ReflectionId Id,
    ContentDate ContentDate,
    ReflectionStatus Status,
    GenerationReason GenerationReason,
    StaleReason? LastStaleReason,
    ReflectionVersionId? InitialVersionId,
    ReflectionVersionId? PreviousVersionId,
    ReflectionVersionId? WorkingVersionId,
    ReflectionVersionId? ConfirmedVersionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ReflectionVersionView? InitialVersion,
    ReflectionVersionView? PreviousVersion,
    ReflectionVersionView? WorkingVersion,
    SemanticSearchState SemanticSearch)
{
    /// <summary>
    /// True when what the user confirmed is no longer what they are editing. The audit pointer is kept (§6.3),
    /// so the honest thing is to say so rather than to pretend the two agree.
    /// </summary>
    public bool ConfirmedVersionIsNotWorking =>
        ConfirmedVersionId is not null && WorkingVersionId is not null && ConfirmedVersionId != WorkingVersionId;
}

/// <summary>Builds the client-facing view of a draft, loading only the versions it actually shows.</summary>
public sealed class GetReflectionUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly HistoryRetrievalUseCase _retrieval;

    public GetReflectionUseCase(IReflectionRepository reflections, HistoryRetrievalUseCase retrieval)
    {
        _reflections = reflections;
        _retrieval = retrieval;
    }

    public async Task<ReflectionView?> ExecuteAsync(ContentDate contentDate, CancellationToken cancellationToken)
    {
        var reflection = await _reflections
            .FindByContentDateAsync(contentDate, cancellationToken)
            .ConfigureAwait(false);

        return reflection is null
            ? null
            : await ToViewAsync(reflection, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ReflectionView> ToViewAsync(Reflection reflection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reflection);

        var semantic = await _retrieval.GetSemanticSearchStateAsync(cancellationToken).ConfigureAwait(false);

        return new ReflectionView(
            reflection.Id,
            reflection.ContentDate,
            reflection.Status,
            reflection.GenerationReason,
            reflection.LastStaleReason,
            reflection.InitialVersionId,
            reflection.PreviousVersionId,
            reflection.WorkingVersionId,
            reflection.ConfirmedVersionId,
            reflection.CreatedAtUtc,
            reflection.UpdatedAtUtc,
            await LoadAsync(reflection.InitialVersionId, cancellationToken).ConfigureAwait(false),
            await LoadAsync(reflection.PreviousVersionId, cancellationToken).ConfigureAwait(false),
            await LoadAsync(reflection.WorkingVersionId, cancellationToken).ConfigureAwait(false),
            semantic);
    }

    private async Task<ReflectionVersionView?> LoadAsync(
        ReflectionVersionId? versionId,
        CancellationToken cancellationToken)
    {
        if (versionId is not { } id)
        {
            return null;
        }

        var version = await _reflections.FindVersionAsync(id, cancellationToken).ConfigureAwait(false);
        return version is null ? null : ReflectionVersionView.From(version);
    }
}

/// <summary>Lists drafts over a date range, for the calendar page (§9.3).</summary>
public sealed class ListReflectionsUseCase
{
    private readonly IReflectionRepository _reflections;

    public ListReflectionsUseCase(IReflectionRepository reflections) => _reflections = reflections;

    /// <summary>
    /// Summaries only: the calendar needs status per day, not three bodies per day. Bodies are fetched one day
    /// at a time through the draft page.
    /// </summary>
    public async Task<IReadOnlyList<ReflectionView>> ExecuteAsync(
        ContentDate fromInclusive,
        ContentDate toInclusive,
        CancellationToken cancellationToken)
    {
        var reflections = await _reflections
            .ListByDateRangeAsync(fromInclusive, toInclusive, cancellationToken)
            .ConfigureAwait(false);

        return reflections
            .OrderByDescending(reflection => reflection.ContentDate)
            .Select(reflection => new ReflectionView(
                reflection.Id,
                reflection.ContentDate,
                reflection.Status,
                reflection.GenerationReason,
                reflection.LastStaleReason,
                reflection.InitialVersionId,
                reflection.PreviousVersionId,
                reflection.WorkingVersionId,
                reflection.ConfirmedVersionId,
                reflection.CreatedAtUtc,
                reflection.UpdatedAtUtc,
                null,
                null,
                null,
                SemanticSearchState.Disabled))
            .ToArray();
    }
}

/// <summary>
/// Confirms the working version (§6.3, §13 POST /api/reflections/{date}/confirm).
/// </summary>
/// <param name="acceptedUnsourcedClaims">
/// §8.4: the check's findings are warnings, not a block — "用户确认后仍可发布". This flag is the record that the
/// user saw them. It does not change the stored status; it exists so a caller cannot confirm a draft with
/// outstanding findings without saying so.
/// </param>
public sealed record ConfirmReflectionRequest(bool AcceptedUnsourcedClaims);

public sealed class ConfirmReflectionUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IClock _clock;
    private readonly GetReflectionUseCase _view;

    public ConfirmReflectionUseCase(IReflectionRepository reflections, IClock clock, GetReflectionUseCase view)
    {
        _reflections = reflections;
        _clock = clock;
        _view = view;
    }

    public async Task<ReflectionView> ExecuteAsync(
        ContentDate contentDate,
        ConfirmReflectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reflection = await RequireAsync(_reflections, contentDate, cancellationToken).ConfigureAwait(false);

        if (reflection.WorkingVersionId is not { } workingId)
        {
            throw new UseCaseException("reflection.confirm.no_version", "This day has no draft to confirm yet.");
        }

        var version = await _reflections.FindVersionAsync(workingId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.confirm.no_version", "The working version no longer exists.");

        if (version.UnsourcedClaims.Count > 0 && !request.AcceptedUnsourcedClaims)
        {
            // Not a prohibition — §8.4 is explicit that the user may publish anyway — but the client has to
            // have shown the findings, and this is the only way it can prove that it did.
            throw new UseCaseException(
                "reflection.confirm.unsourced_claims_not_acknowledged",
                $"This draft has {version.UnsourcedClaims.Count} sentence(s) the source check could not trace. "
                + "Acknowledge them to continue.");
        }

        reflection.Confirm(workingId, _clock.UtcNow);
        await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);

        return await _view.ToViewAsync(reflection, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Reflection> RequireAsync(
        IReflectionRepository reflections,
        ContentDate contentDate,
        CancellationToken cancellationToken) =>
        await reflections.FindByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false)
        ?? throw new UseCaseException("reflection.unknown", $"No reflection for {contentDate}.");
}

/// <summary>Switches which of the three versions is being edited (§6.4, §9.3).</summary>
public sealed class SwitchReflectionVersionUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IClock _clock;
    private readonly GetReflectionUseCase _view;

    public SwitchReflectionVersionUseCase(IReflectionRepository reflections, IClock clock, GetReflectionUseCase view)
    {
        _reflections = reflections;
        _clock = clock;
        _view = view;
    }

    public async Task<ReflectionView> ExecuteAsync(
        ContentDate contentDate,
        ReflectionVersionId versionId,
        CancellationToken cancellationToken)
    {
        var reflection = await ConfirmReflectionUseCase
            .RequireAsync(_reflections, contentDate, cancellationToken)
            .ConfigureAwait(false);

        reflection.SwitchWorkingVersion(versionId, _clock.UtcNow);
        await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);

        return await _view.ToViewAsync(reflection, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Records a hand edit on the working version (§6.4, §9.3).
/// <para>
/// Editing clears the version's source map and its check result: both described the previous text, and keeping
/// them would make the "verify my sources" feature point at sentences that no longer exist. The client can
/// regenerate afterwards if it wants the mapping back.
/// </para>
/// </summary>
public sealed class EditReflectionVersionUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly GetReflectionUseCase _view;

    public EditReflectionVersionUseCase(
        IReflectionRepository reflections,
        IUnitOfWork unitOfWork,
        IClock clock,
        GetReflectionUseCase view)
    {
        _reflections = reflections;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _view = view;
    }

    public async Task<ReflectionView> ExecuteAsync(
        ContentDate contentDate,
        string title,
        string summary,
        string body,
        CancellationToken cancellationToken)
    {
        var reflection = await ConfirmReflectionUseCase
            .RequireAsync(_reflections, contentDate, cancellationToken)
            .ConfigureAwait(false);

        if (reflection.WorkingVersionId is not { } workingId)
        {
            throw new UseCaseException("reflection.edit.no_version", "This day has no draft to edit yet.");
        }

        var version = await _reflections.FindVersionAsync(workingId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.edit.no_version", "The working version no longer exists.");

        version.Edit(title, summary, body, _clock.UtcNow);

        // The text change and the removal of the map that described the old text commit together: a draft whose
        // provenance survived its own rewrite would point the verification screen at sentences that are gone.
        await using (var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await _reflections.UpdateVersionAsync(version, cancellationToken).ConfigureAwait(false);
            await _reflections.ClearSourcesAndFindingsAsync(version.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _view.ToViewAsync(reflection, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The source map of a day's working version (§13 GET /api/reflections/{date}/sources), already compared
/// against the current text so drift is visible as data rather than as a client-side guess.
/// </summary>
public sealed record ReflectionSourcesView(
    ContentDate ContentDate,
    ReflectionVersionId VersionId,
    DateTimeOffset? CheckedAtUtc,
    IReadOnlyList<SourceReferenceView> Sources,
    IReadOnlyList<UnsourcedClaimView> UnsourcedClaims,
    SemanticSearchState SemanticSearch);

public sealed class GetReflectionSourcesUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly HistoryRetrievalUseCase _retrieval;

    public GetReflectionSourcesUseCase(IReflectionRepository reflections, HistoryRetrievalUseCase retrieval)
    {
        _reflections = reflections;
        _retrieval = retrieval;
    }

    public async Task<ReflectionSourcesView> ExecuteAsync(ContentDate contentDate, CancellationToken cancellationToken)
    {
        var reflection = await ConfirmReflectionUseCase
            .RequireAsync(_reflections, contentDate, cancellationToken)
            .ConfigureAwait(false);

        if (reflection.WorkingVersionId is not { } workingId)
        {
            throw new UseCaseException("reflection.sources.no_version", "This day has no draft yet.");
        }

        var version = await _reflections.FindVersionAsync(workingId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.sources.no_version", "The working version no longer exists.");

        var view = ReflectionVersionView.From(version);
        var semantic = await _retrieval.GetSemanticSearchStateAsync(cancellationToken).ConfigureAwait(false);

        return new ReflectionSourcesView(
            contentDate,
            version.Id,
            version.SourcesCheckedAtUtc,
            view.Sources,
            view.UnsourcedClaims,
            semantic);
    }
}

/// <summary>
/// Runs §8.4's second stage against one version and stores the findings.
/// <para>
/// A separate use case from generation because it is a separate job: the draft is already usable when this
/// runs, so a failing check must not cost the user the draft. The <c>SourcesCheckedAtUtc</c> stamp is the
/// difference between "no findings" and "not checked yet".
/// </para>
/// </summary>
public sealed class RunUnsourcedStatementCheckUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IInputEntryRepository _inputs;
    private readonly IUnsourcedStatementChecker _checker;
    private readonly IClock _clock;

    public RunUnsourcedStatementCheckUseCase(
        IReflectionRepository reflections,
        IInputEntryRepository inputs,
        IUnsourcedStatementChecker checker,
        IClock clock)
    {
        _reflections = reflections;
        _inputs = inputs;
        _checker = checker;
        _clock = clock;
    }

    /// <returns>How many findings were recorded.</returns>
    public async Task<int> ExecuteAsync(ReflectionVersionId versionId, CancellationToken cancellationToken)
    {
        var version = await _reflections.FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            throw new UseCaseException("reflection.version.unknown", $"No version with id {versionId}.");
        }

        var reflection = await _reflections.FindByIdAsync(version.ReflectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.unknown", "The version's reflection no longer exists.");

        var dayInputs = await _inputs
            .ListByContentDateAsync(reflection.ContentDate, cancellationToken)
            .ConfigureAwait(false);

        // The checker sees exactly what the writer saw: the day's material plus the inputs this version actually
        // cites. Offering it the whole archive would let it call a sentence sourced from material that was never
        // in the prompt.
        var cited = await LoadCitedMaterialAsync(version, cancellationToken).ConfigureAwait(false);

        var findings = await _checker
            .CheckAsync(
                new UnsourcedCheckRequest(
                    reflection.ContentDate,
                    version.Body,
                    GenerationRules.SelectDayMaterial(dayInputs),
                    cited),
                cancellationToken)
            .ConfigureAwait(false);

        var claims = new List<UnsourcedClaim>();

        foreach (var finding in findings)
        {
            // Same rule as citations: offsets are derived from the draft, never taken from the model (A.6).
            if (SourceQuoteLocator.Locate(version.Body, finding.Quote) is { } located)
            {
                claims.Add(new UnsourcedClaim(located.BlockIndex, located.CharStart, located.CharEnd, finding.Reason));
            }
        }

        await _reflections
            .ReplaceUnsourcedClaimsAsync(version.Id, claims, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return claims.Count;
    }

    private async Task<IReadOnlyList<Domain.Retrieval.RetrievedMaterial>> LoadCitedMaterialAsync(
        ReflectionVersion version,
        CancellationToken cancellationToken)
    {
        var material = new List<Domain.Retrieval.RetrievedMaterial>();
        var seen = new HashSet<InputEntryId>();

        foreach (var source in version.Sources)
        {
            if (!seen.Add(source.InputId))
            {
                continue;
            }

            var entry = await _inputs.FindByIdAsync(source.InputId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            material.Add(new Domain.Retrieval.RetrievedMaterial(
                entry,
                source.Relevance,
                source.Reason,
                source.IsHistorical));
        }

        return material;
    }
}
