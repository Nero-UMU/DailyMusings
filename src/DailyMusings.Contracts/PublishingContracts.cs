namespace DailyMusings.Contracts;

/// <summary>Wire names for the publication lifecycle (docs/开发指导.md §11.1).</summary>
public static class PublicationStatusNames
{
    public const string Queued = "queued";
    public const string InProgress = "inProgress";
    public const string DraftUploaded = "draftUploaded";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

public static class PublicationTriggerNames
{
    public const string Automatic = "automatic";
    public const string Manual = "manual";
}

public static class PublicationVisibilityNames
{
    public const string Draft = "draft";
    public const string Public = "public";
}

public static class PublishTargetTypeNames
{
    public const string Markdown = "markdown";
}

/// <summary>
/// A destination the instance can publish to. There is exactly one kind: a Markdown directory the user has
/// mounted, which is what Hexo reads (§11.2). Nothing here carries a credential.
/// </summary>
public sealed record PublishTargetDto(
    string Id,
    string Name,
    string Type,

    /// <summary>A directory relative to the markdown root.</summary>
    string? DestinationReference,
    bool AutomaticPublishEnabled,
    string? AutomaticPublishEnabledBy,
    string? AutomaticPublishEnabledAtUtc);

public sealed record PublishTargetListResponse(IReadOnlyList<PublishTargetDto> Items);

public sealed record CreatePublishTargetRequest(string Name, string Type, string? DestinationReference);

public sealed record UpdatePublishTargetRequest(string? Name, string? DestinationReference);

/// <summary>
/// Turning unattended publishing on or off (§11.1, decision A.25). No password: the route is administrator-only,
/// so the caller is already the instance's one administrator, and re-typing that password proved nothing while
/// making the switch tedious. The audit record of who set it is written server-side from the account.
/// </summary>
public sealed record SetAutomaticPublishRequest(bool Enabled);

/// <summary>One publication, with everything the draft screen needs to explain itself.</summary>
public sealed record PublicationDto(
    string Id,
    string ReflectionId,
    string ReflectionVersionId,
    string PublishTargetId,
    string TargetName,
    string TargetType,
    string Trigger,
    string Status,
    string RequestedVisibility,
    string? RemoteId,
    int AttemptCount,
    string ScheduledAtUtc,
    string? TriggeredBy,
    string? TriggeredAtUtc,
    string? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorSummary,

    /// <summary>True when the remote has been read at least once, so "in sync" means something (§11.1).</summary>
    bool RemoteChecked,
    bool LocalChanged,
    bool RemoteChanged,

    /// <summary>How many times this version has gone to this target; a re-export is the next round.</summary>
    int ExportRound);

public sealed record PublicationListResponse(IReadOnlyList<PublicationDto> Items);

/// <summary>
/// A publish request. <c>Visibility</c> is what the user chose; whether an unattended run may honour
/// <c>public</c> is the per-target opt-in's business, not the request's.
/// </summary>
public sealed record PublishRequest(string Visibility, bool ReplaceExistingFile);

/// <summary>The refusal code and detail when a publish request could not be queued (§7, §11.1).</summary>
public sealed record PublishResponse(bool Queued, string? Code, string? Detail, PublicationDto? Publication);

/// <summary>What the user decided about a remote that has drifted from the draft.</summary>
public sealed record ResolveRemoteRequest(string Action, bool AllowOverwriteOfManualEdits);

/// <summary>
/// What the difference check found. The remote here is a file, so there is no title, status or link to report —
/// the exported name travels as the publication's <c>RemoteId</c> — only the two hashes that were compared.
/// </summary>
public sealed record RemoteCheckResponse(
    PublicationDto Publication,
    bool RemoteChecked,
    bool LocalChanged,
    bool RemoteChanged,
    string? RemoteContentHash,
    string? LocalContentHash);

/// <summary>Which notification events are sent, and where (§12).</summary>
public sealed record NotificationSettingsDto(
    bool SmtpConfigured,
    string ToAddress,
    string? InstanceUrl,
    bool DraftReady,
    bool JobFailed,
    bool AutomaticPublication);

public sealed record UpdateNotificationSettingsRequest(
    string? ToAddress,
    string? InstanceUrl,
    bool? DraftReady,
    bool? JobFailed,
    bool? AutomaticPublication);
