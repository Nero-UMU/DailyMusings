using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>
/// Audio blobs on the instance's media volume (docs/开发指导.md §8.2, §15.1).
/// <para>
/// Two decisions worth stating. The stored extension is derived from the <em>content type</em>, never from a
/// client-supplied file name — that removes path traversal and hostile extensions as a category rather than
/// filtering for them. And a blob is written to a <c>.partial</c> file and moved into place, so a truncated
/// upload can never masquerade as durable audio.
/// </para>
/// </summary>
public sealed class FileAudioStore : IAudioStore
{
    private const string PartialSuffix = ".partial";

    private readonly InstancePaths _paths;

    public FileAudioStore(InstancePaths paths) => _paths = paths;

    public async Task<StoredAudio> SaveAsync(
        InputEntryId inputId,
        ContentDate contentDate,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedContentType = NormalizeContentType(contentType);
        var relativePath = BuildRelativePath(inputId, contentDate, normalizedContentType);
        var absolutePath = _paths.ResolveMediaFile(relativePath);
        var directory = Path.GetDirectoryName(absolutePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partialPath = absolutePath + PartialSuffix;
        long byteCount;

        try
        {
            await using (var target = File.Create(partialPath))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                byteCount = target.Length;
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (byteCount == 0)
            {
                throw new InvalidOperationException("The uploaded audio was empty.");
            }

            // Atomic within the volume: after this the blob either exists in full or not at all.
            File.Move(partialPath, absolutePath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        return new StoredAudio(relativePath, byteCount, normalizedContentType);
    }

    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken)
    {
        var absolutePath = _paths.ResolveMediaFile(storedPath);

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The stored audio no longer exists.", absolutePath);
        }

        Stream stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(_paths.ResolveMediaFile(storedPath)));

    public Task DeleteAsync(string storedPath, CancellationToken cancellationToken)
    {
        // Idempotent by design: deleting an entry twice must not fail the second time (§9.2 retries).
        TryDelete(_paths.ResolveMediaFile(storedPath));
        return Task.CompletedTask;
    }

    public Task<long?> GetSizeAsync(string storedPath, CancellationToken cancellationToken)
    {
        var file = new FileInfo(_paths.ResolveMediaFile(storedPath));
        return Task.FromResult(file.Exists ? file.Length : (long?)null);
    }

    /// <summary>
    /// <c>media/yyyy/MM/&lt;inputId&gt;.&lt;ext&gt;</c>. The date partition keeps a directory listing usable and
    /// lets a retention sweep work on a month at a time.
    /// </summary>
    private static string BuildRelativePath(InputEntryId inputId, ContentDate contentDate, string contentType)
    {
        var year = contentDate.Value.Year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        var month = contentDate.Value.Month.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

        // Forward slashes on purpose: the value is persisted and must mean the same thing on Windows and Linux.
        return $"{year}/{month}/{inputId}{ExtensionFor(contentType)}";
    }

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "application/octet-stream";
        }

        // Strip parameters such as "; codecs=..." that some Android recorders append.
        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var bare = separator >= 0 ? contentType[..separator] : contentType;

        return bare.Trim().ToLowerInvariant();
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/webm" => ".webm",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/3gpp" => ".3gp",
        "audio/amr" => ".amr",
        _ => ".bin",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A blob we could not delete is a retention problem, not a request failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning: never fail the caller over cleanup.
        }
    }
}
