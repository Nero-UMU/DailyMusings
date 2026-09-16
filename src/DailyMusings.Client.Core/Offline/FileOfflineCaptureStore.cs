using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyMusings.Client.Core.Offline;

/// <summary>
/// Captures stored on the device's own filesystem.
/// <para>
/// One JSON file per capture rather than a single manifest. A manifest would have to be rewritten on every change,
/// and a crash during that rewrite would lose the whole queue — exactly the failure §9.2 exists to prevent. With a
/// file per capture, the worst case is losing the capture being written, and the audio is written first anyway.
/// </para>
/// <para>
/// Serialization goes through a source-generated context: the Android release build trims the app, and
/// reflection-based <c>System.Text.Json</c> would fail there in a way no desktop test would catch.
/// </para>
/// </summary>
public sealed class FileOfflineCaptureStore : IOfflineCaptureStore
{
    private const string PartialSuffix = ".partial";
    private const string AudioDirectoryName = "audio";
    private const string RecordDirectoryName = "records";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileOfflineCaptureStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = Path.GetFullPath(rootPath);
        AudioDirectory = Path.Combine(RootPath, AudioDirectoryName);
        RecordDirectory = Path.Combine(RootPath, RecordDirectoryName);

        Directory.CreateDirectory(AudioDirectory);
        Directory.CreateDirectory(RecordDirectory);
    }

    public string RootPath { get; }

    public string AudioDirectory { get; }

    public string RecordDirectory { get; }

    public async Task<string> SaveAudioAsync(
        string captureId,
        Stream content,
        string fileExtension,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentNullException.ThrowIfNull(content);

        var extension = NormalizeExtension(fileExtension);
        var finalPath = Path.Combine(AudioDirectory, $"{captureId}{extension}");
        var partialPath = finalPath + PartialSuffix;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            try
            {
                await using (var target = new FileStream(
                                 partialPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 useAsync: true))
                {
                    await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

                    // The synchronous overload is the only one that flushes to the device rather than the OS.
                    // §9.2 says the user is told "recorded" only after the bytes are safe, so this call is the
                    // moment that promise is kept — worth a blocking fsync.
                    target.Flush(flushToDisk: true);
                }

                File.Move(partialPath, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return finalPath;
    }

    public async Task<PendingCapture?> FindAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = RecordPath(captureId);
            return File.Exists(path) ? await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var captures = new List<PendingCapture>();

            foreach (var path in Directory.EnumerateFiles(RecordDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                captures.Add(await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false));
            }

            // Oldest first: the user's thoughts should reach the server in the order they were had.
            return captures.OrderBy(capture => capture.CreatedAtUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(PendingCapture capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (string.IsNullOrWhiteSpace(capture.Id))
        {
            throw new ArgumentException("A capture must have a local id.", nameof(capture));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteRecordAsync(capture, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string captureId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The record goes first: if the process dies between the two, an unlisted audio file is garbage that a
            // sweep can find, whereas a record pointing at a deleted file would look like a capture that can still
            // be uploaded.
            TryDelete(RecordPath(captureId));

            foreach (var audio in Directory.EnumerateFiles(AudioDirectory, $"{captureId}.*"))
            {
                TryDelete(audio);
            }
        }
        finally
        {
            _gate.Release();
        }

        await Task.CompletedTask;
    }

    private string RecordPath(string captureId) => Path.Combine(RecordDirectory, $"{captureId}.json");

    private static async Task<PendingCapture> ReadRecordAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024, useAsync: true);

        var capture = await JsonSerializer
            .DeserializeAsync(stream, ClientJsonContext.Default.PendingCapture, cancellationToken)
            .ConfigureAwait(false);

        return capture ?? throw new InvalidDataException($"The capture record '{path}' could not be read.");
    }

    private async Task WriteRecordAsync(PendingCapture capture, CancellationToken cancellationToken)
    {
        var finalPath = RecordPath(capture.Id);
        var partialPath = finalPath + PartialSuffix;

        try
        {
            await using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 8 * 1024, useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, capture, ClientJsonContext.Default.PendingCapture, cancellationToken)
                    .ConfigureAwait(false);

                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static string NormalizeExtension(string? fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            return ".bin";
        }

        var trimmed = fileExtension.Trim();

        // Must look like ".ext". Anything else — including a path or a traversal attempt — falls back to a
        // harmless default rather than letting the caller influence where the blob lands.
        if (trimmed[0] != '.' ||
            trimmed.Length is < 2 or > 8 ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) >= 0)
        {
            return ".bin";
        }

        return trimmed;
    }

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
            // Local cleanup must never be the reason a capture fails to upload.
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(PendingCapture))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
