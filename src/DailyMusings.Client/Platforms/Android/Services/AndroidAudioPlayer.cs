using Android.Media;
using DailyMusings.Client.Core.Audio;

namespace DailyMusings.Client.Services;

/// <summary>
/// Plays a recording from the device's own archive with Android's own <see cref="MediaPlayer"/>
/// (docs/开发指导.md §3.2, §15.2 step 6).
/// <para>
/// The counterpart of <see cref="AndroidAudioRecorder"/>: one of the two pieces of the audio path that cannot be
/// written once for every platform. The screens show a draggable progress slider, which is why this class exposes
/// position, duration and seeking — those are <c>MediaPlayer</c> calls (<c>CurrentPosition</c>, <c>Duration</c>,
/// <c>SeekTo</c>) that nothing above the platform boundary could reach otherwise. Nothing here is logged, and
/// nothing leaves the device — the file came from the app's own sandbox.
/// </para>
/// </summary>
public sealed class AndroidAudioPlayer : IAudioPlayer
{
    private readonly object _gate = new();

    private MediaPlayer? _player;
    private bool _prepared;
    private string? _currentPath;

    /// <inheritdoc />
    public event EventHandler? PlaybackCompleted;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _prepared && _player is { } player && SafeQuery(player, static p => p.IsPlaying);
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _prepared && _player is { } player
                    ? TimeSpan.FromMilliseconds(SafeQuery(player, static p => p.CurrentPosition))
                    : TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return _prepared && _player is { } player
                    ? TimeSpan.FromMilliseconds(SafeQuery(player, static p => p.Duration))
                    : TimeSpan.Zero;
            }
        }
    }

    public async Task LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (IsLoaded(filePath))
        {
            return;
        }

        await PrepareAsync(filePath, cancellationToken).ConfigureAwait(true);
    }

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Preparing is what opens the file; a file already loaded (and paused, or played to its end) just starts.
        if (!IsLoaded(filePath))
        {
            await PrepareAsync(filePath, cancellationToken).ConfigureAwait(true);
        }

        lock (_gate)
        {
            if (_prepared && _player is { } player)
            {
                player.Start();
            }
        }
    }

    /// <summary>
    /// Opens the file and waits until the platform says it is ready, without starting it. Shared by
    /// <see cref="LoadAsync"/> and <see cref="PlayAsync"/> so the asynchronous prepare (and its timeout and error
    /// handling) exists once.
    /// </summary>
    private async Task PrepareAsync(string filePath, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(true);

        // Prepared/Error are asynchronous, and awaiting a TaskCompletionSource keeps the UI thread free while the
        // platform opens the file. The continuation deliberately stays on the calling context: the screens touch
        // UI objects right after this returns.
        var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!);

            player.SetDataSource(filePath);

            player.Prepared += (_, _) => prepared.TrySetResult(true);
            player.Error += (_, args) =>
            {
                args.Handled = true;
                prepared.TrySetResult(false);
            };
            player.Completion += OnCompletion;

            player.PrepareAsync();

            _player = player;
            _prepared = false;
            _currentPath = filePath;
        }

        bool ready;

        try
        {
            ready = await prepared.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(true);
            throw;
        }

        if (!ready)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(true);
            throw new InvalidOperationException("这个录音无法播放。");
        }

        lock (_gate)
        {
            if (_player is null)
            {
                throw new InvalidOperationException("播放器已经关闭。");
            }

            _prepared = true;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_prepared && _player is { } player && SafeQuery(player, static p => p.IsPlaying))
            {
                player.Pause();
            }
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_prepared && _player is { } player)
            {
                var clamped = Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, SafeQuery(player, static p => p.Duration)));
                player.SeekTo((int)clamped);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        MediaPlayer? player;

        lock (_gate)
        {
            player = _player;
            _player = null;
            _prepared = false;
            _currentPath = null;
        }

        if (player is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (SafeQuery(player, static p => p.IsPlaying))
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
            player.Completion -= OnCompletion;
            player.Release();
        }

        return Task.CompletedTask;
    }

    private bool IsLoaded(string filePath)
    {
        lock (_gate)
        {
            return _prepared && string.Equals(_currentPath, filePath, StringComparison.Ordinal);
        }
    }

    private void OnCompletion(object? sender, EventArgs e) => PlaybackCompleted?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Reads a property that throws when the player is in the wrong state. A player that was released underneath a
    /// poll (the page disappeared while the timer was running) must report zero, not take the screen down.
    /// </summary>
    private static int SafeQuery(MediaPlayer player, Func<MediaPlayer, int> query)
    {
        try
        {
            return query(player);
        }
        catch (Java.Lang.IllegalStateException)
        {
            return 0;
        }
    }

    private static bool SafeQuery(MediaPlayer player, Func<MediaPlayer, bool> query)
    {
        try
        {
            return query(player);
        }
        catch (Java.Lang.IllegalStateException)
        {
            return false;
        }
    }
}
