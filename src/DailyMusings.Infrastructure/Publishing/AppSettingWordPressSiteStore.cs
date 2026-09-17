using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;

namespace DailyMusings.Infrastructure.Publishing;

/// <summary>
/// The per-target WordPress override, kept in the settings table (docs/开发指导.md §8.1).
/// <para>
/// One row per field rather than a JSON blob, so a backup, a readable export and the settings page all show the
/// same plain key/value shape the rest of the configuration uses — and so a future migration can read it without
/// knowing this class.
/// </para>
/// </summary>
public sealed class AppSettingWordPressSiteStore : IWordPressSiteOverrideStore
{
    private readonly IAppSettingStore _settings;

    public AppSettingWordPressSiteStore(IAppSettingStore settings) => _settings = settings;

    public async Task<WordPressSiteOverride> GetAsync(
        PublishTargetId targetId,
        CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new WordPressSiteOverride(
            Read(stored, WordPressSiteSettingKeys.BaseUrl(targetId)),
            Read(stored, WordPressSiteSettingKeys.Username(targetId)),
            Read(stored, WordPressSiteSettingKeys.SecretName(targetId)),
            ReadInt(stored, WordPressSiteSettingKeys.TimeoutSeconds(targetId)));
    }

    public async Task SetAsync(
        PublishTargetId targetId,
        WordPressSiteOverride value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Blank rather than absent for a field the operator cleared: the key has to exist so that the read path
        // treats it as "explicitly none" instead of falling back to the deployment configuration.
        await _settings.SetAsync(WordPressSiteSettingKeys.BaseUrl(targetId), value.BaseUrl ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        await _settings.SetAsync(WordPressSiteSettingKeys.Username(targetId), value.Username ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        await _settings.SetAsync(WordPressSiteSettingKeys.SecretName(targetId), value.SecretName ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        await _settings.SetAsync(
                WordPressSiteSettingKeys.TimeoutSeconds(targetId),
                value.TimeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ClearAsync(PublishTargetId targetId, CancellationToken cancellationToken) =>
        SetAsync(targetId, WordPressSiteOverride.None, cancellationToken);

    private static string? Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? ReadInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(
            Read(values, key),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
}
