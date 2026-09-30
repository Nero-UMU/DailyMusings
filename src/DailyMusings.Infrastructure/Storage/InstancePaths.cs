using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>
/// 两个根目录的绑定（<c>Storage</c> 配置节）。
/// <para>
/// **布局（2026-09-29 起，附录 A.31）**：磁盘上只有两个挂载点，职责按「内容 / 状态」切分，而不是按子目录名。
/// </para>
/// <list type="bullet">
/// <item><see cref="MarkdownRootPath"/>（容器里是 <c>DM_DATA_DIR</c> 映射进来的目录）——**只放 Markdown**。
/// 后台「发布设置」里的稿件输出目录与稿件发布目录都以它为根：映射了 <c>/home/atri/data:/var/lib/dailymusings</c>
/// 之后填 <c>aaa/bbb/posts</c>，文件就落在 <c>/home/atri/data/aaa/bbb/posts</c>。因为它只装稿件，可以整个交给
/// Hexo，也可以整份同步走。</item>
/// <item><see cref="StatePath"/>（容器里是 <c>DM_CONFIG_DIR</c> 映射进来的目录）——**其余全部状态**：SQLite、
/// 录音、可读导出、备份包、DataProtection 密钥环、管理页保存的凭据、运行时覆盖文件。这些是实例的内部状态，
/// 不需要人读，也绝不能和要交给 Hexo 的目录混在一起。</item>
/// </list>
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// 实例状态根目录。除了 Markdown 之外的一切都在它下面，所以容器部署只需要再挂一个 Markdown 目录就完整了。
    /// <para>
    /// 默认值面向本地开发（<c>dotnet run</c>）：落在仓库下的 <c>.dailymusings/</c> 里，一个目录装完，
    /// 不把库、录音、备份散在仓库根（已 gitignore）。容器部署用镜像里的绝对路径覆盖它。
    /// </para>
    /// </summary>
    public string StatePath { get; set; } = ".dailymusings/state";

    /// <summary>
    /// Markdown 根目录，也是后台填写两个输出目录时的基准。**只放 Markdown**。
    /// </summary>
    public string MarkdownRootPath { get; set; } = ".dailymusings/markdown";

    /// <summary>
    /// 密钥环目录。相对路径按 <see cref="StatePath"/> 解析（绝对路径原样使用）。
    /// <para>
    /// 它与 Markdown 目录分开是必须的：密钥环签发管理员登录 Cookie，属凭据等价物，随任何「要给别人看/要同步」
    /// 的目录外流就等于把会话伪造能力一起送出去（决策 A.13/A.14）。
    /// </para>
    /// </summary>
    public string KeyRingPath { get; set; } = "keys";

    /// <summary>
    /// 引导用的覆盖文件（目前只有管理员改过的监听端口，§8.1）。相对路径按 <see cref="StatePath"/> 解析。
    /// <para>
    /// 必须放在状态根而不是 Markdown 根：Web 宿主建起来之前就要读它，所以它不能来自数据库；而它也不该跟着
    /// Markdown 目录被同步走。
    /// </para>
    /// </summary>
    public string RuntimeConfigPath { get; set; } = "runtime.json";
}

/// <summary>
/// 实例的目录契约（docs/开发指导.md §5，2026-09-29 起按「内容 / 状态」重新划分，见附录 A.31）。
/// <para>
/// 集中在这里意味着 compose、备份作业与 Hexo 写入器不可能对「东西放哪儿」各说各话。两个根：
/// <see cref="MarkdownPath"/> 只装稿件（后台填的目录以它为根），<see cref="RootPath"/> 装其余全部状态。
/// </para>
/// </summary>
public sealed class InstancePaths
{
    public const string DatabaseFileName = "dailymusings.db";

    public InstancePaths(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(options.StatePath) ? "." : options.StatePath);
        MarkdownPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.MarkdownRootPath) ? "markdown" : options.MarkdownRootPath);
        KeyRingPath = ResolveUnder(RootPath, options.KeyRingPath, "keys");
        RuntimeConfigPath = ResolveUnder(RootPath, options.RuntimeConfigPath, "runtime.json");
    }

    /// <summary>
    /// Resolves the layout from configuration. Callers that need a path before the container is built — the
    /// DataProtection key ring being the only one so far — use this instead of resolving services prematurely.
    /// </summary>
    public static InstancePaths FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
            ?? new StorageOptions();

        return new InstancePaths(options);
    }

    /// <summary>
    /// 状态根：SQLite、录音、导出、备份、密钥环、运行时覆盖的父目录（容器里是 <c>DM_CONFIG_DIR</c>）。
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Markdown 根：**只装稿件**，后台填的两个输出目录都以它为根（容器里是 <c>DM_DATA_DIR</c>）。
    /// </summary>
    public string MarkdownPath { get; }

    public string KeyRingPath { get; }

    /// <summary>
    /// The bootstrap overrides file. Not part of <see cref="ManagedDirectories"/> and not part of the backup set:
    /// it is a file, and it must stay out of the archives.
    /// </summary>
    public string RuntimeConfigPath { get; }

    /// <summary>
    /// Credentials an operator typed into the admin page, encrypted at rest.
    /// <para>
    /// Under the key ring directory on purpose, and for the same two reasons: it is credential-equivalent
    /// material, so it must stay out of the backup set, and it is encrypted with the DataProtection key ring, so
    /// it must live and die with the keys that can read it. Anywhere else would either put a credential into every
    /// backup or leave ciphertext nothing can decrypt after a restore.
    /// </para>
    /// </summary>
    public string UiSecretsPath => Path.Combine(KeyRingPath, "ui-secrets");

    public string DatabasePath => Path.Combine(RootPath, DatabaseFileName);

    /// <summary>Audio blobs. The retention sweep (decision A.1) deletes files here and nothing else.</summary>
    public string MediaPath => Path.Combine(RootPath, "media");

    /// <summary>Human-readable export output (§15.1).</summary>
    public string ExportPath => Path.Combine(RootPath, "exports");

    /// <summary>Scheduled backup output (§15.2).</summary>
    public string BackupPath => Path.Combine(RootPath, "backups");

    /// <summary>
    /// Every directory the instance creates. Two roots plus their subdirectories: the markdown root is created
    /// alongside the state root so a fresh deployment with two empty mounts starts cleanly.
    /// </summary>
    public IReadOnlyList<string> ManagedDirectories =>
    [
        RootPath,
        MediaPath,
        ExportPath,
        BackupPath,
        MarkdownPath,
        KeyRingPath,
    ];

    /// <summary>The directories a backup must contain. Deliberately excludes the key ring.</summary>
    public IReadOnlyList<string> BackedUpDirectories =>
    [
        MediaPath,
        ExportPath,
        BackupPath,
    ];

    /// <summary>Creates the directory contract. Safe to call on every start.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in ManagedDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        // Not in ManagedDirectories: it is a child of the key ring directory, and listing it separately would
        // suggest it is a backup-worthy location rather than the opposite.
        Directory.CreateDirectory(UiSecretsPath);
    }

    /// <summary>Resolves a stored relative media path against the media root.</summary>
    public string ResolveMediaFile(string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(MediaPath, storedPath);

    /// <summary>
    /// 相对路径按状态根解析，绝对路径原样用。这样镜像里可以只写 <c>Storage__KeyRingPath=keys</c>，既不依赖
    /// 进程的工作目录，也不会因为换个 cwd 就把密钥环写到别处。
    /// </summary>
    private static string ResolveUnder(string root, string? value, string fallback)
    {
        var effective = string.IsNullOrWhiteSpace(value) ? fallback : value;

        return Path.GetFullPath(
            Path.IsPathRooted(effective) ? effective : Path.Combine(root, effective));
    }
}
