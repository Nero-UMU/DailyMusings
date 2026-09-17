using DailyMusings.Client.Core;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace DailyMusings.Client.Services;

/// <summary>
/// Records short voice notes with the Windows media capture API (docs/开发指导.md §18: 完成 Windows MAUI 适配).
/// <para>
/// AAC in an MP4 container, the same shape the Android recorder produces, so everything downstream — the offline
/// queue, the upload, the transcription endpoint — sees one format regardless of which client captured it. That
/// sameness is the point of keeping the recorder the only platform-specific piece of the capture path.
/// </para>
/// <para>
/// The microphone capability in <c>Package.appxmanifest</c> is what makes this work at all: without it
/// <see cref="MediaCapture.InitializeAsync"/> fails, and the failure looks like a hardware problem rather than a
/// packaging one.
/// </para>
/// </summary>
public sealed class WindowsAudioRecorder : IAudioRecorder
{
    private MediaCapture? _capture;
    private StorageFile? _file;
    private DateTimeOffset _startedAt;

    public bool IsRecording => _capture is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRecording)
        {
            return;
        }

        var capture = new MediaCapture();

        try
        {
            // Audio only: this product captures thoughts, not video, and asking for a camera would demand a
            // permission the app has no use for.
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                MediaCategory = MediaCategory.Speech,
            }).AsTask(cancellationToken).ConfigureAwait(true);

            // NOT ApplicationData.Current: an unpackaged Windows app (the zip we ship, WindowsPackageType=None) has no
            // package identity, and that API throws "Operation is not valid due to the current state of the object"
            // there — found by pressing 开始录音 in the unpackaged build, where the failure looked like a microphone
            // problem. A directory under the user's temp path works both packaged and unpackaged.
            var directory = Path.Combine(Path.GetTempPath(), "DailyMusings");
            Directory.CreateDirectory(directory);

            var folder = await StorageFolder.GetFolderFromPathAsync(directory).AsTask(cancellationToken).ConfigureAwait(true);

            var file = await folder
                .CreateFileAsync($"capture-{Guid.CreateVersion7():N}.m4a", CreationCollisionOption.GenerateUniqueName)
                .AsTask(cancellationToken).ConfigureAwait(true);

            // 96 kbps mono at 44.1 kHz matches the Android settings. Speech needs no more, and a bigger file only
            // costs upload time on the connection the user actually has.
            var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
            profile.Audio.Bitrate = 96_000;
            profile.Audio.ChannelCount = 1;
            profile.Audio.SampleRate = 44_100;

            await capture
                .StartRecordToStorageFileAsync(profile, file)
                .AsTask(cancellationToken)
                .ConfigureAwait(true);

            _capture = capture;
            _file = file;
            _startedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception)
        {
            // A capture that failed to initialise still holds the device. Releasing it here is what lets the next
            // attempt succeed, rather than reporting the microphone as busy forever.
            capture.Dispose();
            throw;
        }
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        if (_capture is not { } capture || _file is not { } file)
        {
            throw new InvalidOperationException("当前没有正在进行的录音。");
        }

        _capture = null;
        _file = null;

        var duration = DateTimeOffset.UtcNow - _startedAt;

        try
        {
            // Stop is what finalises the container; without it the file is unreadable.
            await capture.StopRecordAsync().AsTask(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            capture.Dispose();
        }

        var stream = await file.OpenStreamForReadAsync().ConfigureAwait(true);

        // Under half a second is almost always an accidental double tap, and no transcription endpoint does anything
        // useful with it. Same rule as the Android recorder, for the same reason.
        if (duration < TimeSpan.FromMilliseconds(500) || stream.Length == 0)
        {
            stream.Dispose();
            await TryDeleteAsync(file).ConfigureAwait(true);
            throw new InvalidOperationException("录音太短了，请按住多说一会儿。");
        }

        return new RecordedAudio(stream, ".m4a", "audio/mp4", duration.TotalSeconds);
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_capture is { } capture)
        {
            _capture = null;

            try
            {
                await capture.StopRecordAsync().AsTask(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Stopping a capture that never really started throws; the file is discarded either way.
            }
            finally
            {
                capture.Dispose();
            }
        }

        if (_file is { } file)
        {
            _file = null;
            await TryDeleteAsync(file).ConfigureAwait(true);
        }
    }

    private static async Task TryDeleteAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The temporary folder is the platform's to reclaim.
        }
    }
}
