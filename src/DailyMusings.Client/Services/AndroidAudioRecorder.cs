using Android.Media;
using DailyMusings.Client.Core;

namespace DailyMusings.Client.Services;

/// <summary>
/// Records short voice notes with the platform recorder (docs/开发指导.md §3.2: 短语音, not meeting audio).
/// <para>
/// MediaRecorder straight to AAC-in-MP4: the smallest file the transcription endpoints accept, and the format every
/// Android device can produce without extra codecs.
/// </para>
/// <para>
/// This is the one part of the capture path that cannot be verified without a device. Failures surface as an
/// exception the capture screen turns into a message, so a recorder that cannot start degrades to text capture
/// rather than taking the app down.
/// </para>
/// </summary>
public sealed class AndroidAudioRecorder : IAudioRecorder
{
    private MediaRecorder? _recorder;
    private string? _outputPath;
    private DateTimeOffset _startedAt;

    public bool IsRecording => _recorder is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRecording)
        {
            return;
        }

        var granted = await Permissions
            .RequestAsync<Permissions.Microphone>()
            .ConfigureAwait(false);

        if (granted != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("需要麦克风权限才能录音。可以在系统设置里授予后重试。");
        }

        var path = Path.Combine(FileSystem.CacheDirectory, $"capture-{Guid.CreateVersion7():N}.m4a");

        var recorder = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new MediaRecorder(Android.App.Application.Context)
            : new MediaRecorder();

        try
        {
            recorder.SetAudioSource(AudioSource.Mic);
            recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetAudioEncoder(AudioEncoder.Aac);
            recorder.SetAudioSamplingRate(44_100);
            recorder.SetAudioEncodingBitRate(96_000);
            recorder.SetOutputFile(path);
            recorder.Prepare();
            recorder.Start();
        }
        catch
        {
            recorder.Release();
            TryDelete(path);
            throw;
        }

        _recorder = recorder;
        _outputPath = path;
        _startedAt = DateTimeOffset.UtcNow;
    }

    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        if (_recorder is not { } recorder || _outputPath is not { } path)
        {
            throw new InvalidOperationException("当前没有正在进行的录音。");
        }

        _recorder = null;

        try
        {
            // Stop is what finalises the MP4 container; without it the file is unreadable.
            recorder.Stop();
        }
        finally
        {
            recorder.Release();
        }

        var duration = DateTimeOffset.UtcNow - _startedAt;

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("录音文件没有生成。");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        // A recording of less than a second is almost always an accidental double tap, and no transcription
        // endpoint will do anything useful with it.
        if (duration < TimeSpan.FromMilliseconds(500) || stream.Length == 0)
        {
            stream.Dispose();
            TryDelete(path);
            throw new InvalidOperationException("录音太短了，请按住多说一会儿。");
        }

        return Task.FromResult(new RecordedAudio(stream, ".m4a", "audio/mp4", duration.TotalSeconds));
    }

    public Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_recorder is { } recorder)
        {
            _recorder = null;

            try
            {
                recorder.Stop();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Stopping a recorder that never really started throws; the file is discarded either way.
            }
            finally
            {
                recorder.Release();
            }
        }

        if (_outputPath is { } path)
        {
            _outputPath = null;
            TryDelete(path);
        }

        return Task.CompletedTask;
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
            // The cache directory is the platform's to reclaim.
        }
    }
}
