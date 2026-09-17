using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;

namespace DailyMusings.Application.Publishing;

/// <summary>
/// Where one WordPress target writes, as the admin page can override it (docs/开发指导.md §8.1, §11.1).
/// <para>
/// A target's <c>DestinationReference</c> is a key into <c>Publishing:WordPress:Targets:&lt;key&gt;</c>, which means
/// the site address and the secret's name lived only in the compose file. That is fine for one blog configured once,
/// and wrong for the case this exists for: changing a site's address, or pointing a second target at a different
/// blog, without editing and redeploying the deployment. Each field here overrides the configuration for exactly
/// one target; leaving a field empty keeps falling through to the configuration, so an instance that never used
/// this page behaves exactly as before.
/// </para>
/// </summary>
public sealed record WordPressSiteOverride(
    string? BaseUrl,
    string? Username,
    string? SecretName,
    int? TimeoutSeconds)
{
    public static WordPressSiteOverride None { get; } = new(null, null, null, null);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(BaseUrl) &&
        string.IsNullOrWhiteSpace(Username) &&
        string.IsNullOrWhiteSpace(SecretName) &&
        TimeoutSeconds is null;
}

/// <summary>
/// The setting keys, one set per target. Keyed by the target's id rather than its name so renaming a target cannot
/// silently move its credentials to another site.
/// </summary>
public static class WordPressSiteSettingKeys
{
    private static string Prefix(PublishTargetId targetId) => $"publish.wordpress.{targetId.Value:N}";

    public static string BaseUrl(PublishTargetId targetId) => $"{Prefix(targetId)}.baseUrl";

    public static string Username(PublishTargetId targetId) => $"{Prefix(targetId)}.username";

    public static string SecretName(PublishTargetId targetId) => $"{Prefix(targetId)}.secretName";

    public static string TimeoutSeconds(PublishTargetId targetId) => $"{Prefix(targetId)}.timeoutSeconds";
}

/// <summary>Reads and writes the per-target WordPress override.</summary>
public interface IWordPressSiteOverrideStore
{
    Task<WordPressSiteOverride> GetAsync(PublishTargetId targetId, CancellationToken cancellationToken);

    Task SetAsync(PublishTargetId targetId, WordPressSiteOverride value, CancellationToken cancellationToken);

    /// <summary>Removes every key, so the target falls back to the deployment configuration again.</summary>
    Task ClearAsync(PublishTargetId targetId, CancellationToken cancellationToken);
}

/// <summary>
/// Changes one WordPress target's site settings (docs/开发指导.md §8.1).
/// <para>
/// The secret is referenced by <em>name</em> and never by value, exactly as everywhere else: the value stays in
/// Docker Secrets, so it cannot reach the settings table, an export or a backup (§10.4).
/// </para>
/// <para>
/// Validated in full before anything is written, inside one transaction. Half-applied site settings — a new address
/// with the previous secret's name — is a target that authenticates against the wrong blog, which is worse than a
/// rejected save.
/// </para>
/// </summary>
public sealed class UpdateWordPressSiteOverrideUseCase
{
    private const int MinimumTimeoutSeconds = 1;
    private const int MaximumTimeoutSeconds = 600;

    private readonly IPublishTargetRepository _targets;
    private readonly IWordPressSiteOverrideStore _store;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateWordPressSiteOverrideUseCase(
        IPublishTargetRepository targets,
        IWordPressSiteOverrideStore store,
        IUnitOfWork unitOfWork)
    {
        _targets = targets;
        _store = store;
        _unitOfWork = unitOfWork;
    }

    public async Task<WordPressSiteOverride> ExecuteAsync(
        PublishTargetId targetId,
        WordPressSiteOverride update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var target = await _targets.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publish.target.unknown", $"No publish target with id {targetId}.");

        if (target.Type != Domain.Publishing.PublishTargetType.WordPress)
        {
            throw new UseCaseException(
                "publish.site.not_wordpress",
                "Only a WordPress target has a site address and an application password.");
        }

        var next = new WordPressSiteOverride(
            NormalizeBaseUrl(update.BaseUrl),
            NormalizeOptional(update.Username),
            NormalizeSecretName(update.SecretName),
            update.TimeoutSeconds);

        Validate(next);

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (next.IsEmpty)
        {
            await _store.ClearAsync(targetId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.SetAsync(targetId, next, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return next;
    }

    private static void Validate(WordPressSiteOverride value)
    {
        if (value.TimeoutSeconds is { } timeout &&
            timeout is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw new UseCaseException(
                "publish.site.timeout.invalid",
                $"请求超时需要介于 {MinimumTimeoutSeconds} 与 {MaximumTimeoutSeconds} 秒之间。");
        }

        if (value.BaseUrl is { } baseUrl &&
            (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            throw new UseCaseException(
                "publish.site.base_url.invalid",
                "站点地址需要是一个完整的 http/https 地址，例如 https://blog.example.com。");
        }

        if (value.SecretName is { } secretName &&
            (secretName.Contains('/', StringComparison.Ordinal) ||
             secretName.Contains('\\', StringComparison.Ordinal) ||
             secretName is "." or ".."))
        {
            throw new UseCaseException(
                "publish.site.secret_name.invalid",
                "Secret 名只能是文件名（不含路径分隔符）。");
        }
    }

    /// <summary>
    /// A blank field means "stop overriding this one", which is why blank becomes null rather than an empty string:
    /// the configuration has to be able to take over again.
    /// </summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeBaseUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');

    private static string? NormalizeSecretName(string? value) => NormalizeOptional(value);
}
