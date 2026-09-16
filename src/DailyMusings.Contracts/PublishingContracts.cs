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
    public const string WordPress = "wordPress";
    public const string Markdown = "markdown";
}

/// <summary>
/// A destination the instance can publish to. Never carries a credential: the WordPress site's password lives in
/// the secret store and is referenced by name (§10.4).
/// </summary>
public sealed record PublishTargetDto(
    string Id,
    string Name,
    string Type,

    /// <summary>A configuration key for WordPress, or a directory relative to the markdown root.</summary>
    string? DestinationReference,
    bool AutomaticPublishEnabled,
    string? AutomaticPublishEnabledBy,
    string? AutomaticPublishEnabledAtUtc);

public sealed record PublishTargetListResponse(IReadOnlyList<PublishTargetDto> Items);

public sealed record CreatePublishTargetRequest(string Name, string Type, string? DestinationReference);

public sealed record UpdatePublishTargetRequest(string? Name, string? DestinationReference);

/// <summary>
/// Turning unattended publishing on or off (§11.1). The password is required even to turn it <em>off</em>: the
/// switch is a safety control, and a session that could disable it silently is a session that could publish.
/// </summary>
public sealed record SetAutomaticPublishRequest(bool Enabled, string CurrentPassword);

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

public sealed record RemoteCheckResponse(
    PublicationDto Publication,
    bool RemoteChecked,
    bool LocalChanged,
    bool RemoteChanged,
    string? RemoteTitle,
    string? RemoteStatus,
    string? RemoteLink,
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
