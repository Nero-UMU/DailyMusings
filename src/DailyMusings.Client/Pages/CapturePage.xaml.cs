using System.Globalization;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DailyMusings.Client.Pages;

/// <summary>
/// 今日随想 (phone spec §3.2): record a thought or type one, read back what the server recognised, play the
/// recording, and decide whether to send it.
/// <para>
/// Three promises are kept on this screen. The recording is on disk before it says "saved" (§9.2). Nothing is sent
/// anywhere until the upload button is pressed — the device is the source of truth for the user's own archive, and
/// the server is told only on request. And a failure never becomes a lie: an unreachable server leaves the thought
/// on the phone and says so, and every <c>async void</c> handler has a floor under it, because the first version of
/// this screen took the whole process down when the server was off.
/// </para>
/// </summary>
public partial class CapturePage : ContentPage
{
    /// <summary>
    /// How many times to ask whether a recording's text has arrived, and how far apart.
    /// <para>
    /// Bounded on purpose: an instance with transcription switched off leaves an entry pending forever, and polling
    /// it indefinitely would spend the radio to learn nothing. After this the screen says so and stops.
    /// </para>
    /// </summary>
    private const int MaxTranscriptionPolls = 10;

    private static readonly TimeSpan TranscriptionPollInterval = TimeSpan.FromSeconds(3);

    private readonly ClientSettings _settings;
    private readonly CaptureController _controller;
    private readonly IAudioRecorder _recorder;
    private readonly IAudioPlayer _player;
    private readonly SecureDeviceTokenProvider _tokens;

    private IDispatcherTimer? _recordTimer;
    private IDispatcherTimer? _playbackTimer;
    private DateTimeOffset _recordingStartedAt;
    private PendingCapture? _current;
    private string? _loadedPlaybackPath;
    private bool _manualMode;
    private bool _busy;
    private bool _updatingSlider;

    /// <summary>True when the address is set and the device is paired — the two conditions an upload needs.</summary>
    private bool _canUpload;

    public CapturePage(
        ClientSettings settings,
        CaptureController controller,
        IAudioRecorder recorder,
        IAudioPlayer player,
        SecureDeviceTokenProvider tokens)
    {
        InitializeComponent();

        _settings = settings;
        _controller = controller;
        _recorder = recorder;
        _player = player;
        _tokens = tokens;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _player.PlaybackCompleted += OnPlaybackCompleted;

        TodayLabel.Text = DateTime.Now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        ApplyMode();
        SizeCircle();

        // Nothing in an async void handler may throw: this one reads the local archive, and an exception here used
        // to take the process down rather than tell the user anything.
        try
        {
            await LoadAsync();
        }
        catch (Exception)
        {
            UploadStatus.Text = "暂时无法读取本机内容，请关闭页面后重试。";
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _player.PlaybackCompleted -= OnPlaybackCompleted;

        StopRecordTimer();
        StopPlaybackTimer();

        // A recording must not keep playing over another screen.
        _ = _player.StopAsync(CancellationToken.None);
        _loadedPlaybackPath = null;
    }

    /// <summary>The circle is the screen's centre of gravity, so it scales with the display rather than being a
    /// fixed pixel size that would be a dot on a tablet and a wall on a small phone.</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        SizeCircle();
    }

    private void SizeCircle()
    {
        var available = Width > 0 ? Math.Min(Width, 560) : 360;
        var diameter = Math.Clamp(available * 0.55, 150, 250);

        RecordCircle.WidthRequest = diameter;
        RecordCircle.HeightRequest = diameter;
        RecordCircle.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(diameter / 2) };
    }

    private async Task LoadAsync()
    {
        _canUpload = _settings.IsConfigured && await _tokens.HasTokenAsync(CancellationToken.None);

        if (_current is null)
        {
            // Coming back to a screen that was killed mid-capture: show the one still waiting to go out, so a
            // thought that never reached the server is not invisible.
            var local = await _controller.ListLocalAsync(CancellationToken.None);
            _current = local.FirstOrDefault(capture => capture.NeedsUpload);
        }

        RenderUploadAvailability();
        RenderCurrent();
    }

    private void ApplyMode()
    {
        RecordPanel.IsVisible = !_manualMode;
        ManualPanel.IsVisible = _manualMode;
        ModeButton.Text = _manualMode ? "录音" : "手动输入";

        TodayHintLabel.Text = _manualMode
            ? "写下随想，保存到本机"
            : "点按开始录音";
    }

    private void OnToggleModeClicked(object? sender, EventArgs e)
    {
        _manualMode = !_manualMode;
        ApplyMode();
    }

    // ---------------------------------------------------------------- recording

    private async void OnRecordTapped(object? sender, TappedEventArgs e) => await GuardAsync(async () =>
    {
        if (_busy)
        {
            return;
        }

        if (_recorder.IsRecording)
        {
            await StopAndSaveAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    });

    private async Task StartRecordingAsync()
    {
        try
        {
            await _recorder.StartAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A recorder that cannot start must not take the app down: text capture still works.
            RecordStatus.Text = exception.Message;
            return;
        }

        _recordingStartedAt = DateTimeOffset.UtcNow;

        RecordCircle.Stroke = ColorFromResource("Danger", Colors.OrangeRed);
        CircleLabel.Text = "停止";
        RecordStatus.Text = "正在录音…";

        StartRecordTimer();
    }

    private async Task StopAndSaveAsync()
    {
        StopRecordTimer();
        ResetCircle();

        RecordedAudio recording;

        try
        {
            recording = await _recorder.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            RecordStatus.Text = exception.Message;
            return;
        }

        PendingCapture capture;

        try
        {
            await using var content = recording.Content;

            // The promise §9.2 makes: once this returns the recording is safe on the device, and only then does the
            // UI say so. Whether it reaches the server is a separate matter, decided by the upload button.
            capture = await _controller.SaveVoiceCaptureAsync(
                content,
                recording.FileExtension,
                recording.ContentType,
                recording.DurationSeconds,
                CancellationToken.None);
        }
        catch (Exception)
        {
            RecordStatus.Text = "录音没有保存成功，请检查麦克风权限和本机存储空间后重试。";
            return;
        }

        RecordStatus.Text = $"已保存到本机（{capture.DurationSeconds:F1} 秒）。";
        UploadStatus.Text = "已保存到本机，尚未上传。";

        _current = capture;

        RenderUploadAvailability();
        RenderCurrent();
    }

    private void StartRecordTimer()
    {
        StopRecordTimer();

        _recordTimer = Dispatcher.CreateTimer();
        _recordTimer.Interval = TimeSpan.FromMilliseconds(500);
        _recordTimer.Tick += (_, _) =>
        {
            var seconds = (DateTimeOffset.UtcNow - _recordingStartedAt).TotalSeconds;
            CircleLabel.Text = $"停止\n{seconds:F0} 秒";
            RecordStatus.Text = $"正在录音… {seconds:F0} 秒";
        };
        _recordTimer.Start();
    }

    private void StopRecordTimer()
    {
        if (_recordTimer is { } timer)
        {
            timer.Stop();
            _recordTimer = null;
        }
    }

    private void ResetCircle()
    {
        RecordCircle.Stroke = ColorFromResource("Accent", Colors.DodgerBlue);
        CircleLabel.Text = "记录随想";
    }

    // ---------------------------------------------------------------- typed notes

    private async void OnSaveTextClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var text = TextEditor.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            ManualStatus.Text = "还没有写内容。";
            return;
        }

        PendingCapture capture;

        try
        {
            capture = await _controller.SaveTextCaptureAsync(text, CancellationToken.None);
        }
        catch (Exception)
        {
            ManualStatus.Text = "文字没有保存成功，请检查本机存储空间后重试。";
            return;
        }

        TextEditor.Text = string.Empty;
        ManualStatus.Text = "已保存到本机。";
        _current = capture;

        RenderUploadAvailability();
        RenderCurrent();

        if (_canUpload)
        {
            UploadStatus.Text = "已保存到本机，尚未上传。";
        }
    });

    // ---------------------------------------------------------------- the current thought

    private void RenderCurrent()
    {
        if (_current is not { } capture)
        {
            CurrentCard.IsVisible = false;
            return;
        }

        CurrentCard.IsVisible = true;

        CurrentHeadline.Text = capture.IsVoice
            ? $"录音 · {LocalTime(capture)} · {(capture.DurationSeconds ?? 0):F1} 秒"
            : $"手写 · {LocalTime(capture)}";

        CurrentState.Text = DescribeState(capture);
        CurrentState.TextColor = capture.State switch
        {
            CaptureUploadState.Uploaded => ColorFromResource("AccentGreen", Colors.MediumSeaGreen),
            CaptureUploadState.Failed => ColorFromResource("Danger", Colors.OrangeRed),
            _ => ColorFromResource("TextSecondary", Colors.Gray),
        };

        // 录音与手写用同一句：动作是同一个（删掉本机这一条），措辞不该按类型分叉。
        DeleteButton.Text = "删除这段随想";

        // Playback is only offered for a recording that is still on this device.
        var playable = capture.IsVoice && capture.LocalAudioPath is { Length: > 0 } path && File.Exists(path);

        PlaybackPanel.IsVisible = playable;

        if (playable)
        {
            // Load it (without starting it) so the slider shows the real length before the user presses play.
            if (_loadedPlaybackPath != capture.LocalAudioPath)
            {
                _ = PrimePlaybackAsync(capture.LocalAudioPath!);
            }
        }
        else if (_loadedPlaybackPath is not null)
        {
            _loadedPlaybackPath = null;
            StopPlaybackTimer();
            _ = _player.StopAsync(CancellationToken.None);
        }

        var text = capture.DisplayText;

        TranscriptScroll.IsVisible = !string.IsNullOrWhiteSpace(text);
        TranscriptLabel.Text = text ?? string.Empty;

        UpdatePlaybackUi();
    }

    private void RenderUploadAvailability()
    {
        var hasCurrent = _current is not null;

        UploadButton.Text = _current switch
        {
            null => "上传",
            { IsVoice: true } => "上传本录音",
            _ => "上传这条手写随想",
        };

        UploadButton.IsEnabled = hasCurrent && _canUpload && !_busy;

        if (!hasCurrent)
        {
            UploadStatus.Text = "先录音或写下随想。";
            return;
        }

        if (!_settings.IsConfigured)
        {
            UploadStatus.Text = "尚未填写服务器地址。请到设置页填写后再上传。";
            return;
        }

        if (!_canUpload)
        {
            UploadStatus.Text = "尚未配对。请到设置页输入配对码后再上传。";
        }
    }

    private static string DescribeState(PendingCapture capture) => capture.State switch
    {
        CaptureUploadState.Uploaded => "已上传",
        CaptureUploadState.Failed => "上传失败",
        _ => "未上传",
    };

    private static string LocalTime(PendingCapture capture) =>
        capture.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- upload

    private async void OnUploadClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_current is not { } capture)
        {
            UploadStatus.Text = "先录一段或写一句。";
            return;
        }

        if (!_canUpload)
        {
            UploadStatus.Text = "无法连接服务器，记录仍保存在本机。";
            return;
        }

        SetBusy(true, "正在上传…");

        try
        {
            var outcome = await _controller.UploadAsync(capture.Id, CancellationToken.None);

            _current = await _controller.FindAsync(capture.Id, CancellationToken.None) ?? capture;
            RenderCurrent();

            if (!outcome.Uploaded)
            {
                UploadStatus.Text = $"上传失败：{DescribeFailure(outcome.FailureCode)}。记录仍在本机，可以再试。";
                return;
            }

            if (!string.IsNullOrWhiteSpace(outcome.Transcript))
            {
                // A typed note has no 识别 step, so the same sentence would be a small lie on that path.
                UploadStatus.Text = capture.IsVoice
                    ? "已上传，识别完成。"
                    : "已上传。";
                return;
            }

            UploadStatus.Text = "已上传到服务器，正在等待识别结果…";
            await PollTranscriptionAsync(capture.Id);
        }
        finally
        {
            SetBusy(false);
        }
    });

    /// <summary>
    /// The second half of §0.1's contract: the server recognises voice inline, but when it hands back a pending
    /// answer the text arrives a moment later, and the archive on this device has to end up holding it.
    /// </summary>
    private async Task PollTranscriptionAsync(string captureId)
    {
        for (var attempt = 0; attempt < MaxTranscriptionPolls; attempt++)
        {
            await Task.Delay(TranscriptionPollInterval);

            if (_current?.Id != captureId)
            {
                return;
            }

            var refreshed = await _controller.RefreshFromServerAsync(captureId, CancellationToken.None);

            if (refreshed is null)
            {
                return;
            }

            _current = refreshed;
            RenderCurrent();

            if (!string.IsNullOrWhiteSpace(refreshed.Transcript))
            {
                UploadStatus.Text = "识别完成，文字在上面。";
                return;
            }
        }

        UploadStatus.Text = "正在识别。稍后到日历页查看文字。";
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        Busy.IsVisible = busy;
        Busy.IsRunning = busy;

        if (message is not null)
        {
            UploadStatus.Text = message;
        }

        RenderUploadAvailability();
    }

    // ---------------------------------------------------------------- playback

    private async void OnPlayPauseClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_current?.LocalAudioPath is not { Length: > 0 } path || !File.Exists(path))
        {
            UploadStatus.Text = "这段录音在本机已经不在了。";
            return;
        }

        if (_player.IsPlaying)
        {
            await _player.PauseAsync(CancellationToken.None);
            UpdatePlaybackUi();
            return;
        }

        // PlayAsync prepares the file when it is not already loaded, and continues from the pause position when it
        // is — so the same call serves both "play" and "carry on".
        await _player.PlayAsync(path, CancellationToken.None);
        _loadedPlaybackPath = path;

        StartPlaybackTimer();
        UpdatePlaybackUi();
    });

    /// <summary>
    /// Opens the recording without playing it, so the slider and the total time are real before the user presses
    /// play. A failure here is not reported: the recording is still on disk, and the play button will say what is
    /// wrong when the user actually asks to hear it.
    /// </summary>
    private async Task PrimePlaybackAsync(string path)
    {
        try
        {
            await _player.LoadAsync(path, CancellationToken.None);
            _loadedPlaybackPath = path;
            UpdatePlaybackUi();
        }
        catch (Exception)
        {
            _loadedPlaybackPath = null;
        }
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StopPlaybackTimer();
            _loadedPlaybackPath = null;
            UpdatePlaybackUi();
        });

    private void StartPlaybackTimer()
    {
        StopPlaybackTimer();

        _playbackTimer = Dispatcher.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(400);
        _playbackTimer.Tick += (_, _) =>
        {
            if (!_player.IsPlaying)
            {
                StopPlaybackTimer();
            }

            UpdatePlaybackUi();
        };
        _playbackTimer.Start();
    }

    private void StopPlaybackTimer()
    {
        if (_playbackTimer is { } timer)
        {
            timer.Stop();
            _playbackTimer = null;
        }
    }

    private void UpdatePlaybackUi()
    {
        var duration = _player.Duration;
        var position = _player.Position;

        PlayPauseButton.Text = _player.IsPlaying ? "⏸" : "▶";

        _updatingSlider = true;

        try
        {
            ProgressSlider.Maximum = duration > TimeSpan.Zero ? duration.TotalSeconds : 1;
            ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, ProgressSlider.Maximum);
        }
        finally
        {
            _updatingSlider = false;
        }

        PositionLabel.Text = $"{Clock(position)} / {Clock(duration)}";
    }

    private void OnSeekStarted(object? sender, EventArgs e) => StopPlaybackTimer();

    private async void OnSeekCompleted(object? sender, EventArgs e)
    {
        if (_current?.IsVoice != true)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(ProgressSlider.Value), CancellationToken.None);
            UpdatePlaybackUi();
        });
    }

    private async void OnSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        // Programmatic updates (the position timer, a new file's length) must not be echoed back as a seek, but a
        // tap on the track has to move playback just like a drag does — so the guard is a flag, not a drag state.
        if (_updatingSlider || _current?.IsVoice != true)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(e.NewValue), CancellationToken.None);
            PositionLabel.Text = $"{Clock(TimeSpan.FromSeconds(e.NewValue))} / {Clock(_player.Duration)}";
        });
    }

    private static string Clock(TimeSpan value) =>
        value < TimeSpan.Zero
            ? "0:00"
            : value.TotalHours >= 1
                ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- deleting

    private async void OnDeleteClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_current is not { } capture)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "删除这段随想？",
            "只删本机的这一份。服务器上已经上传的内容不受影响，也不会被一起删除。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        await _player.StopAsync(CancellationToken.None);
        _loadedPlaybackPath = null;
        StopPlaybackTimer();

        var deleted = await _controller.DeleteLocalAsync(capture.Id, CancellationToken.None);

        _current = null;

        RenderUploadAvailability();
        RenderCurrent();

        UploadStatus.Text = deleted is null ? "这条记录已经不在本机了。" : "已从本机删除。";
    });

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Runs one handler body and reports instead of propagating.
    /// <para>
    /// Only the top of an <c>async void</c> handler can do this, and it is the difference between "the app told me
    /// something went wrong" and "the app vanished". A real device with the server switched off found the second.
    /// </para>
    /// </summary>
    private async Task GuardAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception)
        {
            UploadStatus.Text = "操作没有完成，请稍后重试。";
        }
    }

    private static string DescribeFailure(string? failureCode) => failureCode switch
    {
        "client.network_unreachable" => "连不上服务器",
        "client.timeout" => "服务器响应超时",
        "client.upload_failed" => "上传失败，稍后再试",
        "client.not_configured" => "未配置服务器",
        "client.not_paired" => "设备未配对",
        "auth.device_token_rejected" or "auth.unauthenticated" => "设备令牌已失效，需要重新配对",
        "client.audio_missing" => "本地录音已丢失",
        "transcription.public_audio_url_required" => "当前语音配置需要公网音频 URL，请让管理员改用文件上传或 Chat 音频协议",
        "transcription.api_type_invalid" => "语音模型的 API 类型无效，请让管理员重新配置",
        "transcription.request_rejected" => "转写服务拒绝了录音，请让管理员检查模型名称和接口地址",
        "transcription.timeout" => "转写服务响应超时，可以稍后重试",
        null => "未知原因",
        _ => "服务器暂时无法完成这个请求",
    };

    private static Color ColorFromResource(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;
}
