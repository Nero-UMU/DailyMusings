using DailyMusings.Client.Core.Audio;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace DailyMusings.Client.Services;

/// <summary>
/// Plays a recording with the Windows media player (docs/开发指导.md §15.2 step 6).
/// <para>
/// The counterpart of <see cref="WindowsAudioRecorder"/>. The clip is played from the file the screen cached, so an
/// unpackaged build (no package identity) works exactly like a packaged one — the trap the recorder already fell into
/// once with <c>ApplicationData.Current</c>.
/// </para>
/// </summary>
public sealed class WindowsAudioPlayer : IAudioPlayer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaPlayer? _player;

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await StopAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
            player.Source = MediaSource.CreateFromUri(new Uri(filePath));
            player.Play();

            _player = player;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_player is { } player)
            {
                _player = null;
                player.Pause();
                player.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
