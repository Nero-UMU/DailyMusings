using System.Collections.ObjectModel;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Services;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Pages;

/// <summary>One row of the local queue.</summary>
public sealed record PendingRow(string Id, string Title, string Detail, bool CanRetry);

/// <summary>One row of the server's view of today.</summary>
public sealed record TodayRow(string Title, string Status);

/// <summary>
/// The capture screen (docs/开发指导.md §9.1): record, type, see what is waiting, and see what the server has.
/// <para>
/// The screen never treats an upload as done before the server says so, and never deletes a local recording before
/// then — the pending list is the honest state of the day's thoughts until the server has them.
/// </para>
/// </summary>
public partial class TodayPage : ContentPage
{
    private readonly ClientSettings _settings;
    private readonly CaptureController _controller;
    private readonly IAudioRecorder _recorder;
    private readonly DynamicCaptureApiClient _api;
    private readonly SecureDeviceTokenProvider _tokens;

    private readonly ObservableCollection<PendingRow> _pending = [];
    private readonly ObservableCollection<TodayRow> _today = [];

    private IDispatcherTimer? _elapsedTimer;
    private IDispatcherTimer? _transcriptionTimer;
    private int _transcriptionPolls;
    private DateTimeOffset _recordingStartedAt;
    private bool _busy;

    /// <summary>
    /// How long to keep checking whether a pending transcription has finished.
    /// <para>
    /// Bounded on purpose. A server with transcription switched off leaves an entry pending forever, and polling it
    /// indefinitely would spend the radio to learn nothing — so the screen gives up, says so, and leaves the manual
    /// sync button in charge.
    /// </para>
    /// </summary>
    private const int MaxTranscriptionPolls = 40;

    private static readonly TimeSpan TranscriptionPollInterval = TimeSpan.FromSeconds(3);

    public TodayPage(
        ClientSettings settings,
        CaptureController controller,
        IAudioRecorder recorder,
        DynamicCaptureApiClient api,
        SecureDeviceTokenProvider tokens)
    {
        InitializeComponent();

        _settings = settings;
        _controller = controller;
        _recorder = recorder;
        _api = api;
        _tokens = tokens;

        PendingList.ItemsSource = _pending;
        TodayList.ItemsSource = _today;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Nothing in an async void handler may throw: this one used to take the whole process down when the server
        // was unreachable, which is the one situation §9.2 requires the screen to handle gracefully.
        try
        {
            InsecureBanner.IsVisible = _settings.IsInsecureConnection;

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            SyncStatus.Text = $"本机内容读取失败：{exception.Message}";
        }

        // Sync on open, but never block the screen on it: the queue is the source of truth the user sees.
        _ = SyncQuietlyAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopElapsedTimer();
        StopTranscriptionTimer();
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            await GuardAsync(async () =>
            {
                if (_recorder.IsRecording)
                {
                    await StopAndSaveAsync();
                }
                else
                {
                    await StartRecordingAsync();
                }
            });
        }
        finally
        {
            _busy = false;
        }
    }

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
        catch (Exception exception)
        {
            RecordStatus.Text = $"操作失败：{exception.Message}";
        }
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            await _recorder.StartAsync(CancellationToken.None);

            _recordingStartedAt = DateTimeOffset.UtcNow;
            RecordButton.Text = "停止并保存";
            RecordButton.BackgroundColor = Color.FromArgb("#B42318");
            RecordStatus.Text = "正在录音…";

            StartElapsedTimer();
        }
        catch (Exception exception)
        {
            // A recorder that cannot start must not take the app down: text capture still works.
            RecordStatus.Text = exception.Message;
        }
    }

    private async Task StopAndSaveAsync()
    {
        StopElapsedTimer();

        RecordedAudio recording;

        try
        {
            recording = await _recorder.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ResetRecordButton();
            RecordStatus.Text = exception.Message;
            return;
        }

        PendingCapture capture;

        try
        {
            await using var content = recording.Content;

            // The promise §9.2 makes: once this returns, the recording is safe on the device, and only then does the
            // UI say so. Whether it reaches the server yet is a separate matter.
            capture = await _controller.SaveVoiceCaptureAsync(
                content,
                recording.FileExtension,
                recording.ContentType,
                recording.DurationSeconds,
                CancellationToken.None);

            ResetRecordButton();
            RecordStatus.Text = $"已保存到本机（{capture.DurationSeconds:F1} 秒），正在上传…";
        }
        catch (Exception exception)
        {
            ResetRecordButton();
            RecordStatus.Text = $"录音没有保存成功：{exception.Message}";
            return;
        }

        await RefreshAsync();
        await SyncQuietlyAsync();
        await ReportCaptureOutcomeAsync(capture.Id);
    }

    private async void OnSaveTextClicked(object? sender, EventArgs e)
    {
        await GuardAsync(async () =>
        {
            var text = TextEditor.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                RecordStatus.Text = "还没有写内容。";
                return;
            }

            PendingCapture capture;

            try
            {
                capture = await _controller.SaveTextCaptureAsync(text, CancellationToken.None);
                TextEditor.Text = string.Empty;
                RecordStatus.Text = "已保存到本机，正在上传…";
            }
            catch (Exception exception)
            {
                RecordStatus.Text = $"保存失败：{exception.Message}";
                return;
            }

            await RefreshAsync();
            await SyncQuietlyAsync();
            await ReportCaptureOutcomeAsync(capture.Id);
        });
    }

    /// <summary>
    /// Replaces "正在上传…" with what actually happened.
    /// <para>
    /// Found by testing on a device: the transient line was written when the capture was saved and never updated,
    /// so the screen went on claiming an upload was in progress long after it had finished. The app's whole promise
    /// is that it only says what the server has confirmed, and a stale "uploading…" is the same lie in the other
    /// direction.
    /// </para>
    /// </summary>
    private async Task ReportCaptureOutcomeAsync(string? captureId)
    {
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return;
        }

        var queue = await _controller.GetQueueAsync(CancellationToken.None);
        var capture = queue.FirstOrDefault(item => item.Id == captureId);

        RecordStatus.Text = capture?.State switch
        {
            // Gone from the queue: the server took it, which is the only reason the local copy is deleted.
            null => "已上传到服务器。",

            CaptureUploadState.Failed =>
                $"已保存到本机，上传失败（{DescribeFailure(capture.FailureCode)}），可在下面重试。",

            _ => "已保存到本机，等待上传。",
        };
    }

    private async void OnSyncClicked(object? sender, EventArgs e) => await GuardAsync(SyncQuietlyAsync);

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string captureId })
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _controller.RetryAsync(captureId, CancellationToken.None);
            await RefreshAsync();
            await SyncQuietlyAsync();
        });
    }

    private async void OnDiscardClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string captureId })
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "删除这条？",
            "这条内容还没有上传到服务器，删除后本机也不会保留。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _controller.DiscardAsync(captureId, CancellationToken.None);
            await RefreshAsync();
        });
    }

    private async Task SyncQuietlyAsync()
    {
        if (!_settings.IsConfigured)
        {
            SyncStatus.Text = "尚未配置服务器地址，内容会一直留在本机。";
            return;
        }

        if (!await _tokens.HasTokenAsync(CancellationToken.None))
        {
            SyncStatus.Text = "尚未配对设备，请到设置页输入配对码。";
            return;
        }

        try
        {
            var outcome = await _controller.SyncAsync(CancellationToken.None);

            SyncStatus.Text = outcome switch
            {
                { Uploaded: 0, Failed: 0 } => $"已全部上传，待上传 {outcome.StillQueued} 条。",
                { Failed: 0 } => $"已上传 {outcome.Uploaded} 条。",
                _ => $"已上传 {outcome.Uploaded} 条，{outcome.Failed} 条失败，可在下面重试。",
            };
        }
        catch (Exception exception)
        {
            SyncStatus.Text = $"同步失败：{exception.Message}";
        }

        // The refresh is inside the same guard: a failed read must not escape from here either.
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            SyncStatus.Text = $"本机内容读取失败：{exception.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        var queue = await _controller.GetQueueAsync(CancellationToken.None);

        _pending.Clear();

        foreach (var capture in queue)
        {
            _pending.Add(new PendingRow(
                capture.Id,
                capture.Kind == CaptureKind.Voice
                    ? $"语音 {capture.CreatedAtUtc.ToLocalTime():HH:mm}"
                    : $"文字 {capture.CreatedAtUtc.ToLocalTime():HH:mm}",
                DescribeQueueDetail(capture),
                CanRetry: capture.State == CaptureUploadState.Failed));
        }

        await RefreshTodayAsync();
    }
    private static string DescribeQueueDetail(PendingCapture capture)
    {
        var preview = capture.Kind == CaptureKind.Text
            ? Truncate(capture.Text, 40)
            : $"{(capture.DurationSeconds ?? 0):F1} 秒";

        return capture.State switch
        {
            CaptureUploadState.Queued => $"{preview} · 等待上传",
            CaptureUploadState.Failed =>
                $"{preview} · 上传失败（{DescribeFailure(capture.FailureCode)}，已尝试 {capture.AttemptCount} 次）",
            _ => preview,
        };
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
        null => "未知原因",
        _ => failureCode,
    };

    private async Task RefreshTodayAsync()
    {
        _today.Clear();

        if (!_settings.IsConfigured || !await _tokens.HasTokenAsync(CancellationToken.None))
        {
            TodayStatus.Text = "连接并配对后会显示服务器上的内容。";
            return;
        }

        var contentDate = DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var result = await _api.GetInputsAsync(contentDate, CancellationToken.None);

        // "Nothing on the server today" and "the server could not be reached" are different facts, and saying the
        // first when the second is true is the kind of quiet lie this screen must not tell.
        if (!result.ServerReached)
        {
            UpdateTranscriptionPolling(false);
            TodayStatus.Text = result.FailureCode switch
            {
                "client.not_paired" => "尚未配对设备，请到设置页输入配对码。",
                "client.timeout" => "服务器响应超时，稍后点「立即同步」再试。",
                "client.not_configured" => "尚未配置服务器地址。",
                _ => "连不上服务器，暂时看不到服务器上的内容；本机待上传的内容仍在下面。",
            };

            return;
        }

        if (result.FailureCode == "auth.device_token_rejected")
        {
            UpdateTranscriptionPolling(false);
            TodayStatus.Text = "设备令牌已失效，请到设置页重新配对。";
            return;
        }

        foreach (var item in result.Items)
        {
            _today.Add(new TodayRow(DescribeTitle(item), DescribeStatus(item)));
        }

        TodayStatus.Text = result.FailureCode is null
            ? result.Items.Count == 0
                ? "今天还没有上传任何内容。"
                : $"共 {result.Items.Count} 条。"
            : $"读取今天的内容失败（{result.FailureCode}）。";

        // A freshly uploaded recording is transcribed asynchronously, so the screen has to look again — otherwise it
        // says "正在转写…" until the user thinks to press sync, which is what testing on a device showed.
        UpdateTranscriptionPolling(result.Items.Any(IsAwaitingTranscription));
    }

    private static bool IsAwaitingTranscription(InputDto item) =>
        item.TranscriptionStatus is TranscriptionStatusNames.Pending or TranscriptionStatusNames.InProgress;

    private void UpdateTranscriptionPolling(bool awaiting)
    {
        if (!awaiting)
        {
            StopTranscriptionTimer();
            return;
        }

        if (_transcriptionTimer is not null)
        {
            return;
        }

        _transcriptionPolls = 0;
        _transcriptionTimer = Dispatcher.CreateTimer();
        _transcriptionTimer.Interval = TranscriptionPollInterval;
        _transcriptionTimer.Tick += OnTranscriptionTick;
        _transcriptionTimer.Start();
    }

    private async void OnTranscriptionTick(object? sender, EventArgs e)
    {
        _transcriptionPolls++;

        if (_transcriptionPolls > MaxTranscriptionPolls)
        {
            StopTranscriptionTimer();
            TodayStatus.Text = "转写还在进行中；稍后可以点「立即同步」再看。";
            return;
        }

        try
        {
            await RefreshTodayAsync();
        }
        catch (Exception exception)
        {
            // A failed poll must not spam the user; the next tick tries again, and the manual sync is always there.
            StopTranscriptionTimer();
            TodayStatus.Text = $"刷新转写状态失败：{exception.Message}";
        }
    }

    private void StopTranscriptionTimer()
    {
        if (_transcriptionTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTranscriptionTick;
            _transcriptionTimer = null;
        }
    }

    private static string DescribeTitle(InputDto item)
    {
        var text = item.Transcript ?? item.RevisedTranscript ?? item.OriginalTranscript;

        if (!string.IsNullOrWhiteSpace(text))
        {
            return Truncate(text, 60);
        }

        return item.SourceType == InputSourceNames.Voice ? "（语音，等待转写）" : "（空）";
    }

    private static string DescribeStatus(InputDto item) => item.TranscriptionStatus switch
    {
        TranscriptionStatusNames.Succeeded => "已转写",
        TranscriptionStatusNames.Pending or TranscriptionStatusNames.InProgress => "正在转写…",
        TranscriptionStatusNames.Failed => $"转写失败（{item.FailureCode ?? "未知原因"}）",
        _ => item.SourceType == InputSourceNames.Text ? "文字" : "—",
    };

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= length ? trimmed : trimmed[..length] + "…";
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();

        _elapsedTimer = Dispatcher.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromMilliseconds(500);
        _elapsedTimer.Tick += (_, _) =>
            RecordStatus.Text = $"正在录音… {(DateTimeOffset.UtcNow - _recordingStartedAt).TotalSeconds:F0} 秒";
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        if (_elapsedTimer is { } timer)
        {
            timer.Stop();
            _elapsedTimer = null;
        }
    }

    private void ResetRecordButton()
    {
        RecordButton.Text = "开始录音";
        RecordButton.BackgroundColor = Color.FromArgb("#512BD4");
    }
}
