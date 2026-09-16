namespace DailyMusings.Contracts;

/// <summary>Wire names for the seven draft statuses, mirroring the domain enum (§6.3, decision A.3).</summary>
public static class ReflectionStatusNames
{
    public const string PendingInputs = "pendingInputs";
    public const string Ready = "ready";
    public const string Generating = "generating";
    public const string ReviewRequired = "reviewRequired";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
    public const string StaleByLateInput = "staleByLateInput";
}

/// <summary>How the draft came to be written (§6.3).</summary>
public static class GenerationReasonNames
{
    public const string Scheduled = "scheduled";
    public const string Backfill = "backfill";
    public const string LateInputRegeneration = "lateInputRegeneration";
    public const string Manual = "manual";
}

/// <summary>Whether a citation still resolves against the text as it stands (§6.5, decision A.6).</summary>
public static class SourceDriftNames
{
    /// <summary>The quoted range still holds exactly the text it held at generation time.</summary>
    public const string Exact = "exact";

    /// <summary>The text moved: the client must fall back to a whole-paragraph hint.</summary>
    public const string Drifted = "drifted";

    /// <summary>The paragraph is gone. Treated as drifted for display.</summary>
    public const string Unresolvable = "unresolvable";
}

public sealed record TopicDto(
    string Id,
    string Name,
    string CreatedAtUtc,
    string? MergedIntoId,
    string? MergedAtUtc);

public sealed record TopicListResponse(IReadOnlyList<TopicDto> Items);

public sealed record CreateTopicRequest(string Name);

public sealed record RenameTopicRequest(string Name);

public sealed record MergeTopicsRequest(string SourceTopicId, string TargetTopicId);

/// <summary>
/// Result of a merge. <c>RemappedInputs</c> is how many entries changed hands; the merge never touches a
/// historical source map (decision A.9).
/// </summary>
public sealed record TopicMergeResponse(TopicDto Source, int RemappedInputs);

/// <summary>The assignment as stored, echoed back so the client can render it without a second read.</summary>
public sealed record InputTopicAssignmentResponse(
    string InputId,
    string? PrimaryTopicId,
    IReadOnlyList<string> SecondaryTopicIds);

/// <summary>
/// Filing an input under topics. `PrimaryTopicId` may be null; at most one primary is allowed (§6.2).
/// </summary>
public sealed record AssignInputTopicsRequest(string? PrimaryTopicId, IReadOnlyList<string>? SecondaryTopicIds);

/// <summary>One resolved citation. Offsets are the server's, computed against the current text.</summary>
public sealed record SourceReferenceDto(
    string InputId,
    int BlockIndex,
    int CharStart,
    int CharEnd,
    double Relevance,
    string Reason,
    bool IsHistorical,
    string Drift);

/// <summary>A sentence the second-stage check could not trace to any input (§8.4).</summary>
public sealed record UnsourcedClaimDto(int BlockIndex, int CharStart, int CharEnd, string Reason);

public sealed record ReflectionVersionDto(
    string Id,
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    bool HasManualEdits,
    string CreatedAtUtc,
    string? EditedAtUtc,
    string? ModelName,
    string? PromptVersion,

    /// <summary>Null means the source check has not completed. It never means "clean" (§8.4).</summary>
    string? SourcesCheckedAtUtc,
    IReadOnlyList<SourceReferenceDto> Sources,
    IReadOnlyList<UnsourcedClaimDto> UnsourcedClaims);

/// <summary>Whether semantic history retrieval is usable, so the client can say "语义检索重建中" (§8.3).</summary>
public sealed record SemanticSearchDto(bool Enabled, bool Available, bool Rebuilding);

/// <summary>One day's draft (§9.3). Version bodies are only present when the draft itself is being viewed.</summary>
public sealed record ReflectionDto(
    string Id,
    string ContentDate,
    string Status,
    string GenerationReason,
    string? LastStaleReason,
    string? InitialVersionId,
    string? PreviousVersionId,
    string? WorkingVersionId,
    string? ConfirmedVersionId,
    bool ConfirmedVersionIsNotWorking,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    ReflectionVersionDto? InitialVersion,
    ReflectionVersionDto? PreviousVersion,
    ReflectionVersionDto? WorkingVersion,
    SemanticSearchDto SemanticSearch);

public sealed record ReflectionListResponse(IReadOnlyList<ReflectionDto> Items);

/// <summary>
/// A generation request. Both flags are user decisions that must be explicit: §7 lets the user go ahead despite
/// failed transcriptions, and §6.4 requires them to accept losing hand edits before a regeneration may rotate
/// the working slot.
/// </summary>
public sealed record GenerateReflectionRequest(
    bool IgnoreTranscriptionFailures,
    bool AllowOverwriteOfManualEdits);

public sealed record ConfirmReflectionRequest(bool AcceptedUnsourcedClaims);

public sealed record SwitchWorkingVersionRequest(string VersionId);

public sealed record EditReflectionRequest(string Title, string Summary, string Body);

/// <summary>
/// What happened to a generation request. <c>Queued</c> is false when the day is not eligible, in which case
/// <c>Code</c> says why (§7's rules are reported, never guessed at).
/// </summary>
public sealed record ReflectionGenerationResponse(
    string ContentDate,
    bool Queued,
    string? Code,
    string? Detail,
    JobDto? Job);

public sealed record ReflectionSourcesResponse(
    string ContentDate,
    string VersionId,
    string? CheckedAtUtc,
    IReadOnlyList<SourceReferenceDto> Sources,
    IReadOnlyList<UnsourcedClaimDto> UnsourcedClaims,
    SemanticSearchDto SemanticSearch);
