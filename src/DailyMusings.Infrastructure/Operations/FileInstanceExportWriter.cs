using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// Writes a readable export into the instance's export directory (docs/开发指导.md §15.1).
/// <para>
/// Each export lands in its own timestamped directory and is written to a <c>.partial</c> name first, so a listing
/// never shows a half-written package as if it were complete. Nothing here deletes anything: an export is the user's
/// copy of their own data, and tidying it up on their behalf would be the opposite of the point.
/// </para>
/// </summary>
public sealed class FileInstanceExportWriter : IInstanceExportWriter
{
    private const string ManifestName = "manifest.json";

    private readonly InstancePaths _paths;
    private readonly ILogger<FileInstanceExportWriter> _logger;

    public FileInstanceExportWriter(InstancePaths paths, ILogger<FileInstanceExportWriter> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<InstanceExportResult> WriteAsync(
        InstanceExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = ResolveFreeRoot(request.Stamp);
        var staging = root + ".partial";

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        var entries = new List<ExportedEntry>(request.Files.Count + request.Blobs.Count);

        foreach (var file in request.Files)
        {
            entries.Add(await WriteTextAsync(staging, file, cancellationToken).ConfigureAwait(false));
        }

        var copied = 0;

        foreach (var blob in request.Blobs)
        {
            var source = _paths.ResolveMediaFile(blob.StoredPath);
            if (!File.Exists(source))
            {
                // Verified upstream, so this is a race with the retention sweep rather than a bad request. Skipped
                // instead of failing the whole export: losing one recording out of a package is better than
                // producing no package at all.
                _logger.LogWarning("Skipped a recording that disappeared during the export.");
                continue;
            }

            entries.Add(await CopyBlobAsync(staging, source, blob.RelativePath, cancellationToken).ConfigureAwait(false));
            copied++;
        }

        // No existing package is ever deleted: an export is the user's own copy of their own data, and a second
        // export in the same second must not take the first one with it.
        Directory.Move(staging, root);

        var total = entries.Sum(entry => entry.ByteCount);

        _logger.LogInformation(
            "Export written with {FileCount} file(s) and {BlobCount} recording(s).",
            entries.Count,
            copied);

        return new InstanceExportResult(Path.GetFileName(root), entries, total, copied);
    }

    /// <summary>
    /// An export directory name for this run. The stamp has one-second resolution, so two exports can ask for the
    /// same name; the second one gets a suffix instead of replacing the first.
    /// </summary>
    private string ResolveFreeRoot(string stamp)
    {
        var candidate = Path.Combine(_paths.ExportPath, stamp);
        var suffix = 1;

        while (Directory.Exists(candidate) || Directory.Exists(candidate + ".partial"))
        {
            suffix++;
            candidate = Path.Combine(_paths.ExportPath, $"{stamp}-{suffix}");

            if (suffix > 100)
            {
                candidate = Path.Combine(_paths.ExportPath, $"{stamp}-{Guid.NewGuid():N}");
                break;
            }
        }

        return candidate;
    }

    /// <summary>
    /// Lists the exports already on disk. The retention policy comes from each package's own manifest, because it is
    /// the policy in force <em>when that export was taken</em> — which is what §15.1 wants the package to state.
    /// </summary>
    public Task<IReadOnlyList<InstanceExportSummary>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.ExportPath))
        {
            return Task.FromResult<IReadOnlyList<InstanceExportSummary>>([]);
        }

        var summaries = new List<InstanceExportSummary>();

        foreach (var directory in Directory.EnumerateDirectories(_paths.ExportPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (name.EndsWith(".partial", StringComparison.Ordinal))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            var manifest = ReadManifest(directory);

            summaries.Add(new InstanceExportSummary(
                name,
                Directory.GetCreationTimeUtc(directory),
                files.Length,
                files.Sum(path => new FileInfo(path).Length),
                manifest));
        }

        return Task.FromResult<IReadOnlyList<InstanceExportSummary>>(
            PackageFileName.NewestFirst(summaries, summary => summary.RelativeRoot).ToArray());
    }

    private static string ReadManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestName);

        if (!File.Exists(manifestPath))
        {
            return "(未记录)";
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));

            return document.RootElement.TryGetProperty("audioRetention", out var retention)
                ? retention.GetString() ?? "(未记录)"
                : "(未记录)";
        }
        catch (System.Text.Json.JsonException)
        {
            return "(清单不可读)";
        }
    }

    private static async Task<ExportedEntry> WriteTextAsync(
        string root,
        ExportedFile file,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // No BOM: an export is meant to be opened by other tools, and a BOM at the head of a JSON file is a
        // needless incompatibility.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(file.Content);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        return new ExportedEntry(file.RelativePath, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static async Task<ExportedEntry> CopyBlobAsync(
        string root,
        string source,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(destination);
        var hash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);

        return new ExportedEntry(relativePath, info.Length, hash);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
