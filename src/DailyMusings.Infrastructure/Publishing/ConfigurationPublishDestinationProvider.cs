using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Infrastructure.Storage;

namespace DailyMusings.Infrastructure.Publishing;

/// <summary>
/// Resolves where a target writes (docs/开发指导.md §11.2).
/// <para>
/// A target stores a directory <em>relative to</em> the instance's markdown root, which is the mounted volume.
/// Relative rather than absolute on purpose: the compose file decides what that volume is mounted to, and a
/// stored absolute path would either be meaningless inside the container or, worse, allow a target to be pointed
/// at any file on the host.
/// </para>
/// </summary>
public sealed class ConfigurationPublishDestinationProvider : IPublishDestinationProvider
{
    private readonly InstancePaths _paths;

    public ConfigurationPublishDestinationProvider(InstancePaths paths) => _paths = paths;

    public Task<PublishDestination> ResolveAsync(PublishTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult(new PublishDestination(ResolveMarkdownDirectory(target)));
    }

    /// <summary>
    /// A directory under the markdown root. A reference that tries to escape it is refused rather than
    /// normalized away: silently rewriting somebody's configured path would hide a mistake, and the mistake here
    /// is one that writes files outside the mounted volume.
    /// </summary>
    /// <summary>
    /// 把后台填的目录解析成绝对路径。基准是**映射进来的数据目录**（<see cref="InstancePaths.MarkdownPath"/>）：
    /// 用户映射了 <c>/home/atri/data:/var/lib/dailymusings</c> 并填 <c>aaa/bbb/posts</c>，文件就落在
    /// <c>/home/atri/data/aaa/bbb/posts</c>——填什么就是宿主上看到的那条相对路径，不需要理解任何中间层级
    /// （附录 A.31）。
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
                "目录要写成相对于数据目录的路径，例如 aaa/bbb/posts；不要写绝对路径。");
        }

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                throw new UseCaseException(
                    "publish.markdown.path_escapes_root",
                    "目录不能跳出数据目录（不允许 ..）。");
            }
        }

        var resolved = Path.GetFullPath(Path.Combine(_paths.MarkdownPath, trimmed));
        var root = Path.GetFullPath(_paths.MarkdownPath);

        if (!resolved.StartsWith(root, StringComparison.Ordinal))
        {
            throw new UseCaseException(
                "publish.markdown.path_escapes_root",
                "目录不能跳出数据目录（不允许 ..）。");
        }

        return resolved;
    }
}
