namespace DailyMusings.Client.Core.Audio;

/// <summary>
/// Audio a screen has fetched from the instance and wants to play (docs/开发指导.md §15.2 step 6, decision A.1).
/// <para>
/// Bytes rather than a stream: what the instance serves is one short note, and holding it whole means the caller can
/// retry a failed write to disk without re-asking the server. Playback itself stays platform-specific — the one other
/// piece of the capture path that cannot be written once for both clients.
/// </para>
/// </summary>
public sealed record AudioClip(byte[] Bytes, string ContentType)
{
    /// <summary>A file extension for a cache file, derived from the content type the server reported.</summary>
    public string FileExtension => ContentType switch
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
}

/// <summary>
/// Plays a local audio file. Implemented per platform: neither MAUI nor .NET has a player that both clients share.
/// </summary>
public interface IAudioPlayer
{
    /// <summary>
    /// Plays the file, replacing whatever is playing. Returns when playback has *started*, not when it ends — the
    /// screen stays usable while a note plays.
    /// </summary>
    Task PlayAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>Stops playback. Idempotent: stopping nothing is not an error.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Writes fetched audio into the app's cache so the platform player has a file to open.
/// </summary>
public sealed class AudioClipCache
{
    private readonly string _directory;

    public AudioClipCache(string directory) => _directory = directory;

    /// <summary>
    /// Saves the clip under <paramref name="name"/> and returns its path. Written to a temporary name and moved into
    /// place, so a half-written file can never be handed to a player.
    /// </summary>
    public async Task<string> SaveAsync(string name, AudioClip clip, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(clip);

        Directory.CreateDirectory(_directory);

        var safeName = string.Concat(name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        var path = Path.Combine(_directory, safeName + clip.FileExtension);
        var partial = path + ".partial";

        await File.WriteAllBytesAsync(partial, clip.Bytes, cancellationToken).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);

        return path;
    }
}
