using System.Security.Cryptography;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Publishing;

/// <summary>
/// Writes Markdown into a mounted directory (docs/开发指导.md §11.2).
/// <para>
/// The whole of this class exists to make one promise true: 不得静默覆盖已存在或被外部修改的文件. So it never
/// writes over a name it has not written before, and it only writes over its own file while that file still hashes
/// to exactly what it wrote. The export directory is usually a person's blog repository, where the file may have
/// been edited by hand — and that copy exists nowhere else.
/// </para>
/// <para>
/// Writes go through a temporary file and a move, so a crash mid-write cannot leave a half-written post for a
/// generator to pick up.
/// </para>
/// </summary>
public sealed class FileMarkdownWriter : IMarkdownWriter
{
    private readonly InstancePaths _paths;
    private readonly ILogger<FileMarkdownWriter> _logger;

    public FileMarkdownWriter(InstancePaths paths, ILogger<FileMarkdownWriter> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<MarkdownWriteResult> WriteAsync(
        MarkdownWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = ResolveDirectory(request.Directory);
        Directory.CreateDirectory(directory);

        var previousName = request.PreviousFileName;
        var exists = previousName is { Length: > 0 } && File.Exists(Path.Combine(directory, previousName));
        var recorded = request.PreviousContentHash;

        var existingHash = exists
            ? await ComputeFileHashAsync(Path.Combine(directory, previousName!), cancellationToken).ConfigureAwait(false)
            : null;

        // Ours means: the file is still exactly what this target wrote. A name we have no recorded hash for is not
        // ours, whatever it contains — we cannot prove we wrote it, and "probably fine" is not good enough to
        // overwrite somebody's blog post.
        var fileIsOurs = exists && recorded is { Length: > 0 } &&
                         string.Equals(existingHash, recorded, StringComparison.Ordinal);

        var externallyModified = exists && recorded is { Length: > 0 } && !fileIsOurs;

        var plan = MarkdownWritePolicy.Decide(
            fileExists: exists,
            fileIsOurs: fileIsOurs,
            externallyModified: externallyModified,
            userConfirmedReplace: request.ReplaceExisting);

        if (plan is MarkdownWritePlan.RefuseUnowned or MarkdownWritePlan.RefuseExternallyModified)
        {
            _logger.LogWarning(
                "Refused to write {FileName} in the export directory: {Plan}.",
                previousName ?? request.BaseName,
                plan);

            return new MarkdownWriteResult(previousName ?? request.BaseName, existingHash ?? string.Empty, plan, existingHash);
        }

        var fileName = plan == MarkdownWritePlan.ReplaceExistingFile && previousName is { Length: > 0 }
            ? previousName
            : MarkdownFileName.NextVersionedName(
                request.BaseName,
                candidate => File.Exists(Path.Combine(directory, candidate)));

        await WriteAtomicallyAsync(Path.Combine(directory, fileName), request.Content, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Exported a draft as {FileName}.", fileName);

        return new MarkdownWriteResult(fileName, MarkdownFileHash.Of(request.Content), plan, existingHash);
    }

    public async Task<string?> ReadHashAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var path = Path.Combine(ResolveDirectory(directory), fileName);

        return File.Exists(path)
            ? await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".partial";

        // No BOM and no added newline: the file must hold exactly the string whose hash is recorded, or the next
        // comparison would report a change nobody made.
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Durable before the move: outside the database this file is the only copy of that text.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Resolves the target directory and refuses anything outside the instance's markdown root.
    /// <para>
    /// The application layer already validated the configured reference; this is the second check, at the point
    /// where a path is about to be opened. A traversal bug here does not corrupt a row — it writes a file into
    /// somebody's filesystem — so it is worth checking twice.
    /// </para>
    /// </summary>
    private string ResolveDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var full = Path.GetFullPath(directory);
        var root = Path.GetFullPath(_paths.MarkdownPath);

        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new DomainException(
                "markdown.path.escapes_root",
                "The export directory resolves outside the instance's markdown root.");
        }

        return full;
    }
}
