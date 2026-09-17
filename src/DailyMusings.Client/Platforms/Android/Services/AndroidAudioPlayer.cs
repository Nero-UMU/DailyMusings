using Android.Media;

namespace DailyMusings.Client.Services;

/// <summary>
/// Plays a recording with Android's own <see cref="MediaPlayer"/> (docs/开发指导.md §15.2 step 6).
/// <para>
/// The counterpart of <see cref="AndroidAudioRecorder"/>: one of the two pieces of the audio path that cannot be
/// written once for both clients. Nothing here is logged, and nothing leaves the device — the clip came from the
/// instance and is played from the app's cache.
/// </para>
/// </summary>
public sealed class AndroidAudioPlayer : DailyMusings.Client.Core.Audio.IAudioPlayer
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
            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!);

            player.SetDataSource(filePath);

            // Preparing is asynchronous and a short note is ready almost immediately; the callback keeps the UI thread
            // free so the screen stays responsive while it happens.
            var prepared = new TaskCompletionSource<bool>();
            player.Prepared += (_, _) => prepared.TrySetResult(true);
            player.Error += (_, args) =>
            {
                prepared.TrySetResult(false);
                args.Handled = true;
            };

            player.PrepareAsync();

            _player = player;

            if (!await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
            {
                player.Release();
                _player = null;
                throw new InvalidOperationException("这个录音无法播放。");
            }

            player.Start();
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

                try
                {
                    if (player.IsPlaying)
                    {
                        player.Stop();
                    }
                }
                catch (Java.Lang.IllegalStateException)
                {
                    // Already stopped on its own (the clip ended): releasing is all that is left to do.
                }
                finally
                {
                    player.Release();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
