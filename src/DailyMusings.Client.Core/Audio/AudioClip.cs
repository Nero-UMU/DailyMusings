namespace DailyMusings.Client.Core.Audio;

/// <summary>
/// Plays a recording from the device's own archive (docs/开发指导.md §3.2, §15.2 step 6).
/// <para>
/// Extended beyond "play this file" for the phone's 今日随想 / 日历 pages, which show a progress slider the user can
/// drag. The old interface could only start and stop, so the two screens could not show where they were in a note or
/// let the user jump; position, duration and seeking are capabilities of the platform player
/// (<c>MediaPlayer.CurrentPosition</c> / <c>Duration</c> / <c>SeekTo</c>) that the controller had no way to reach.
/// Nothing here is platform-specific, so it still lives in the shared project and is implemented once per platform.
/// </para>
/// </summary>
public interface IAudioPlayer
{
    /// <summary>True while a file is playing. Paused is not playing.</summary>
    bool IsPlaying { get; }

    /// <summary>How far into the current file playback is. <see cref="TimeSpan.Zero"/> when nothing is loaded.</summary>
    TimeSpan Position { get; }

    /// <summary>How long the current file is, as the platform player measured it. Zero when nothing is loaded.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Raised when a file plays to its end. The screens use it to put the play button back, since nothing else tells
    /// them playback finished on its own.
    /// </summary>
    event EventHandler? PlaybackCompleted;

    /// <summary>
    /// Loads the file without playing it, so a screen can show its length and let the user drag to a position before
    /// deciding to listen. Without this the total duration would read 0:00 until the user pressed play — which is the
    /// wrong way round for a progress slider.
    /// </summary>
    Task LoadAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Plays the file, replacing whatever is playing. Returns when playback has *started*, not when it ends — the
    /// screen stays usable while a note plays. A file that is already loaded and paused continues from where it was;
    /// one that has played to its end starts again.
    /// </summary>
    Task PlayAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>Pauses playback, keeping the position so the next <see cref="PlayAsync"/> can continue from there.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Moves playback to <paramref name="position"/>, clamped to the file's length.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    /// <summary>Stops playback and releases the file. Idempotent: stopping nothing is not an error.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
