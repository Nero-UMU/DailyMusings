using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>Where the instance keeps its state. Bound from the <c>Storage</c> configuration section.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// The instance root. Everything the product writes lives underneath it, so a deployment only needs one
    /// volume to be durable — which is what makes the §15.2 backup story tractable.
    /// </summary>
    public string RootPath { get; set; } = ".";

    /// <summary>Directory the secret files are mounted into, e.g. <c>/run/secrets</c> (§10.4).</summary>
    public string SecretsPath { get; set; } = "/run/secrets";

    /// <summary>
    /// Where the DataProtection key ring lives.
    /// <para>
    /// Deliberately <em>not</em> under <see cref="RootPath"/>. The key ring signs the administrator's auth
    /// cookies, so it is credential-equivalent material: putting it inside the instance root would mean every
    /// backup carries a way to forge a session, which contradicts §10.4 and decision A.13. It still needs to
    /// outlive a container, because otherwise every redeploy silently logs the operator out.
    /// </para>
    /// </summary>
    public string KeyRingPath { get; set; } = "keys";
}

/// <summary>
/// The instance's directory contract (docs/开发指导.md §5 "deploy" + §4 "音频与导出文件目录").
/// <para>
/// Centralizing the layout here means the compose file, the backup job and the Hexo writer cannot disagree
/// about where things live. Sub-directories keep the concerns separable: <c>media</c> is purgeable by the
/// retention job, <c>exports</c> is regenerable, <c>backups</c> is what a restore needs.
/// </para>
/// </summary>
public sealed class InstancePaths
{
    public const string DatabaseFileName = "dailymusings.db";

    public InstancePaths(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(options.RootPath) ? "." : options.RootPath);
        SecretsPath = options.SecretsPath;
        KeyRingPath = Path.GetFullPath(string.IsNullOrWhiteSpace(options.KeyRingPath) ? "keys" : options.KeyRingPath);
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

    public string RootPath { get; }

    public string SecretsPath { get; }

    public string KeyRingPath { get; }

    public string DataDirectory => Path.Combine(RootPath, "data");

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>Audio blobs. The retention sweep (decision A.1) deletes files here and nothing else.</summary>
    public string MediaPath => Path.Combine(RootPath, "media");

    /// <summary>Human-readable export output (§15.1).</summary>
    public string ExportPath => Path.Combine(RootPath, "exports");

    /// <summary>Default destination for Hexo Markdown targets (§11.2); each target may override it.</summary>
    public string MarkdownPath => Path.Combine(RootPath, "markdown");

    /// <summary>Scheduled backup output (§15.2).</summary>
    public string BackupPath => Path.Combine(RootPath, "backups");

    /// <summary>
    /// Everything under <see cref="RootPath"/> is the backup set (§15.2). <see cref="KeyRingPath"/> is created
    /// alongside but excluded from it on purpose.
    /// </summary>
    public IReadOnlyList<string> ManagedDirectories =>
    [
        DataDirectory,
        MediaPath,
        ExportPath,
        MarkdownPath,
        BackupPath,
        KeyRingPath,
    ];

    /// <summary>The directories a backup must contain. Deliberately excludes the key ring.</summary>
    public IReadOnlyList<string> BackedUpDirectories =>
    [
        DataDirectory,
        MediaPath,
        ExportPath,
        MarkdownPath,
        BackupPath,
    ];

    /// <summary>Creates the directory contract. Safe to call on every start.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in ManagedDirectories)
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Resolves a stored relative media path against the media root.</summary>
    public string ResolveMediaFile(string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(MediaPath, storedPath);
}
