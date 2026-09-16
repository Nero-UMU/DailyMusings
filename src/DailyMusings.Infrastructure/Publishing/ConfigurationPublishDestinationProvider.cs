using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Publishing;

/// <summary>
/// Resolves where a target writes (docs/开发指导.md §11.1, §11.2).
/// <para>
/// A WordPress target stores a configuration <em>key</em>; that key is looked up under
/// <c>Publishing:WordPress:Targets:&lt;key&gt;</c> with <c>Publishing:WordPress:Defaults</c> as the fallback, so an
/// instance with a single blog does not have to name it. A Markdown target stores a directory
/// <em>relative to</em> the instance's markdown root, which is the mounted volume.
/// </para>
/// <para>
/// Relative rather than absolute on purpose: the compose file decides what that volume is mounted to, and a
/// stored absolute path would either be meaningless inside the container or, worse, allow a target to be pointed
/// at any file on the host.
/// </para>
/// </summary>
public sealed class ConfigurationPublishDestinationProvider : IPublishDestinationProvider
{
    public const string DefaultsSectionName = "Publishing:WordPress:Defaults";
    public const string TargetsSectionName = "Publishing:WordPress:Targets";

    private readonly IConfiguration _configuration;
    private readonly InstancePaths _paths;

    public ConfigurationPublishDestinationProvider(IConfiguration configuration, InstancePaths paths)
    {
        _configuration = configuration;
        _paths = paths;
    }

    public Task<PublishDestination> ResolveAsync(PublishTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult(target.Type switch
        {
            PublishTargetType.Markdown => PublishDestination.ForMarkdown(ResolveMarkdownDirectory(target)),
            _ => PublishDestination.ForWordPress(ResolveWordPressSite(target)),
        });
    }

    /// <summary>
    /// The key names a section; an empty key means the shared defaults. A target whose section does not exist
    /// gets the defaults rather than an exception, because that is what "I configured one blog" looks like.
    /// </summary>
    private WordPressSite ResolveWordPressSite(PublishTarget target)
    {
        var defaults = _configuration.GetSection(DefaultsSectionName);

        var section = string.IsNullOrWhiteSpace(target.DestinationReference)
            ? defaults
            : _configuration.GetSection($"{TargetsSectionName}:{target.DestinationReference}");

        if (!section.Exists())
        {
            section = defaults;
        }

        var fallback = WordPressSite.Default;

        return new WordPressSite(
            BaseUrl: Read(section, "BaseUrl", defaults, fallback.BaseUrl),
            Username: Read(section, "Username", defaults, fallback.Username),
            SecretName: Read(section, "SecretName", defaults, fallback.SecretName),
            Timeout: TimeSpan.FromSeconds(
                section.GetValue<int?>("TimeoutSeconds")
                ?? defaults.GetValue<int?>("TimeoutSeconds")
                ?? (int)fallback.Timeout.TotalSeconds));
    }

    private string Read(IConfigurationSection section, string key, IConfigurationSection defaults, string fallback) =>
        section.GetValue<string?>(key) ?? defaults.GetValue<string?>(key) ?? fallback;

    /// <summary>
    /// A directory under the markdown root. A reference that tries to escape it is refused rather than
    /// normalized away: silently rewriting somebody's configured path would hide a mistake, and the mistake here
    /// is one that writes files outside the mounted volume.
    /// </summary>
    private string ResolveMarkdownDirectory(PublishTarget target)
    {
        var reference = target.DestinationReference;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return _paths.MarkdownPath;
        }

        var trimmed = reference.Trim().Replace('\\', '/');

        if (Path.IsPathRooted(trimmed))
        {
            throw new UseCaseException(
                "publish.markdown.path_not_relative",
                "A Markdown target's directory must be relative to the instance's markdown root.");
        }

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                throw new UseCaseException(
                    "publish.markdown.path_escapes_root",
                    "A Markdown target may not point outside the instance's markdown root.");
            }
        }

        var resolved = Path.GetFullPath(Path.Combine(_paths.MarkdownPath, trimmed));
        var root = Path.GetFullPath(_paths.MarkdownPath);

        if (!resolved.StartsWith(root, StringComparison.Ordinal))
        {
            throw new UseCaseException(
                "publish.markdown.path_escapes_root",
                "A Markdown target may not point outside the instance's markdown root.");
        }

        return resolved;
    }
}
