using System.Collections.ObjectModel;
using System.Globalization;
using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Services;

namespace DailyMusings.Client.Pages;

/// <summary>One row of the archive: when it was, what it is, what it says, and how far it has got.</summary>
public sealed record CalendarRow(
    string Id,
    string Kind,
    string Time,
    string Summary,
    string State,
    Color StateColor,
    bool ShowDivider);

/// <summary>
/// 日历 (phone spec §3.3): the recordings and notes kept on this device, newest first, with the filter the user
/// picked and a sheet that shows one in full and plays it.
/// <para>
/// Every row carries a time — an archive you cannot place in time is a pile, not a record. The list never depends on
/// the server: it is the device's own store, so it draws the same with the network gone. The one server read here is
/// the quiet backfill of recognition text for recordings whose transcription had not finished when they were
/// uploaded.
/// </para>
/// </summary>
public partial class CalendarPage : ContentPage
{
    private const string FilterNewest = "从新到旧";
    private const string FilterOldest = "从旧到新";
    private const string FilterToday = "今日随想";
    private const string FilterYesterday = "昨日随想";
    private const string FilterWeek = "本周随想";
    private const string FilterMonth = "本月随想";

    private readonly ClientSettings _settings;
    private readonly CaptureController _controller;
    private readonly IAudioPlayer _player;

    private readonly ObservableCollection<CalendarRow> _rows = [];

    private IReadOnlyList<PendingCapture> _all = [];
    private PendingCapture? _detail;
    private string? _detailPlaybackPath;
    private string _filter = FilterNewest;
    private IDispatcherTimer? _playbackTimer;
    private bool _updatingSlider;
    private bool _busy;

    public CalendarPage(ClientSettings settings, CaptureController controller, IAudioPlayer player)
    {
        InitializeComponent();

        _settings = settings;
        _controller = controller;
        _player = player;

        RecordList.ItemsSource = _rows;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _player.PlaybackCompleted += OnPlaybackCompleted;

        // Nothing in an async void handler may throw.
        try
        {
            await LoadAsync();
            await BackfillTranscriptsAsync();
        }
        catch (Exception)
        {
            StatusLabel.Text = "暂时无法读取本机记录，请关闭页面后重试。";
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _player.PlaybackCompleted -= OnPlaybackCompleted;

        StopPlaybackTimer();
        CloseDetail();
    }

    private async Task LoadAsync()
    {
        _all = await _controller.ListLocalAsync(CancellationToken.None);
        ApplyFilter();
    }

    /// <summary>
    /// Fills in the text of recordings whose inline transcription had not finished. It is one classified read, and
    /// a server that cannot be reached simply changes nothing — the archive is already complete without it.
    /// </summary>
    private async Task BackfillTranscriptsAsync()
    {
        var updated = await _controller.RefreshMissingTranscriptsAsync(CancellationToken.None);

        if (updated > 0)
        {
            _all = await _controller.ListLocalAsync(CancellationToken.None);

            if (_detail is not null)
            {
                _detail = _all.FirstOrDefault(capture => capture.Id == _detail.Id) ?? _detail;
                RenderDetail();
            }

            ApplyFilter();
        }
    }

    private async void OnFilterClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        // The action sheet cannot draw a tick beside the current choice, so the choice carries its own.
        var options = new[] { FilterNewest, FilterOldest, FilterToday, FilterYesterday, FilterWeek, FilterMonth }
            .Select(option => option == _filter ? $"✓ {option}" : option)
            .ToArray();

        var choice = await DisplayActionSheetAsync("筛选往期随想", "取消", null, options);

        if (choice is null || choice == "取消")
        {
            return;
        }

        _filter = choice.StartsWith("✓ ", StringComparison.Ordinal) ? choice[2..] : choice;
        ApplyFilter();
    });

    private void ApplyFilter()
    {
        var filtered = _all.Where(Matches).ToList();

        if (_filter is FilterOldest)
        {
            filtered.Reverse();
        }

        _rows.Clear();

        for (var index = 0; index < filtered.Count; index++)
        {
            var capture = filtered[index];
            _rows.Add(new CalendarRow(
                capture.Id,
                capture.IsVoice ? "录音" : "手写",
                FormatTime(capture.CreatedAtUtc),
                Summarize(capture),
                DescribeState(capture),
                StateColor(capture),
                index < filtered.Count - 1));
        }

        FilterButton.Text = $"筛选 · {_filter}";

        StatusLabel.Text = _rows.Count == 0
            ? _all.Count == 0
                ? "本机还没有随想。"
                : $"「{_filter}」没有记录。"
            : $"共 {_rows.Count} 条（本机共 {_all.Count} 条）。";
    }

    private bool Matches(PendingCapture capture)
    {
        var created = capture.CreatedAtUtc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        return _filter switch
        {
            FilterToday => created.Date == today,
            FilterYesterday => created.Date == today.AddDays(-1),
            FilterWeek => created.Date >= WeekStart(today) && created.Date <= today,
            FilterMonth => created.Year == today.Year && created.Month == today.Month,
            _ => true,
        };
    }

    /// <summary>The week starts on Monday, which is what a Chinese calendar shows.</summary>
    private static DateTime WeekStart(DateTime today)
    {
        var offset = today.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)today.DayOfWeek - (int)DayOfWeek.Monday;
        return today.AddDays(-offset);
    }

    private static string FormatTime(DateTimeOffset createdAtUtc)
    {
        var local = createdAtUtc.ToLocalTime();

        // Today's rows are the ones a person reads at a glance, so they get the short form; everything else has to
        // carry its date, or the list would be unplaceable in time.
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Summarize(PendingCapture capture)
    {
        var text = capture.DisplayText;

        if (string.IsNullOrWhiteSpace(text))
        {
            return capture.IsVoice
                ? $"（{(capture.DurationSeconds ?? 0):F1} 秒，还没有识别文字）"
                : "（空）";
        }

        var trimmed = text.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }

    private static string DescribeState(PendingCapture capture) => capture.State switch
    {
        CaptureUploadState.Uploaded => "已上传",
        CaptureUploadState.Failed => "失败",
        _ => "未上传",
    };

    private static Color StateColor(PendingCapture capture) => capture.State switch
    {
        CaptureUploadState.Uploaded => ColorFromResource("AccentGreen", Colors.MediumSeaGreen),
        CaptureUploadState.Failed => ColorFromResource("Danger", Colors.OrangeRed),
        _ => ColorFromResource("TextSecondary", Colors.Gray),
    };

    // ---------------------------------------------------------------- detail sheet

    private async void OnRowTapped(object? sender, TappedEventArgs e) => await GuardAsync(async () =>
    {
        // 双保险：正常情况下背板已经把点击消费掉了，但即使某个平台上没挡住，弹层开着时也不许从列表里
        // 打开另一条——否则用户会以为自己点的是弹层里的内容，实际上却换了对象。
        if (DetailOverlay.IsVisible)
        {
            return;
        }

        if (e.Parameter is not string captureId)
        {
            return;
        }

        var capture = _all.FirstOrDefault(item => item.Id == captureId);

        if (capture is null)
        {
            return;
        }

        await _player.StopAsync(CancellationToken.None);
        _detailPlaybackPath = null;
        StopPlaybackTimer();

        _detail = capture;
        RenderDetail();

        DetailOverlay.IsVisible = true;
    });

    private void RenderDetail()
    {
        if (_detail is not { } capture)
        {
            return;
        }

        var local = capture.CreatedAtUtc.ToLocalTime();

        DetailTitle.Text = capture.IsVoice
            ? $"录音 · {local:yyyy-MM-dd HH:mm} · {(capture.DurationSeconds ?? 0):F1} 秒 · {DescribeState(capture)}"
            : $"手写 · {local:yyyy-MM-dd HH:mm} · {DescribeState(capture)}";

        var playable = capture.IsVoice && capture.LocalAudioPath is { Length: > 0 } path && File.Exists(path);

        DetailPlayback.IsVisible = playable;

        // 删除按钮对录音与手写都显示。底层 DeleteAsync 是类型无关的：先删记录，再按 {id}.* 扫音频文件，
        // 手写没有匹配的音频，那个循环自然什么都不做。此前这里是 IsVisible = capture.IsVoice，
        // 于是手写记录根本没有删除入口。

        if (playable)
        {
            // Load without playing, so the sheet shows the recording's length before the user asks to hear it.
            var audioPath = capture.LocalAudioPath!;

            if (_detailPlaybackPath != audioPath)
            {
                _ = PrimePlaybackAsync(audioPath);
            }
        }

        DetailText.Text = string.IsNullOrWhiteSpace(capture.DisplayText)
            ? capture.IsVoice
                ? "暂无识别文字。上传后可在此查看结果。"
                : "（空）"
            : capture.DisplayText!;

        UpdateDetailPlaybackUi();
    }

    private async Task PrimePlaybackAsync(string path)
    {
        try
        {
            await _player.LoadAsync(path, CancellationToken.None);
            _detailPlaybackPath = path;
            UpdateDetailPlaybackUi();
        }
        catch (Exception)
        {
            // Priming is only for showing the length; the play button reports a file that will not open.
            _detailPlaybackPath = null;
        }
    }

    private void CloseDetail()
    {
        DetailOverlay.IsVisible = false;
        _detail = null;
        _detailPlaybackPath = null;
        StopPlaybackTimer();
        _ = _player.StopAsync(CancellationToken.None);
    }

    private void OnCloseDetailClicked(object? sender, EventArgs e) => CloseDetail();

    /// <summary>
    /// 弹层背板上的点击：**故意什么都不做**。它存在的唯一目的是把触摸消费掉，让它到不了下层的列表——
    /// 否则点弹层后面另一条随想会把它打开（用户报的缺陷）。要让「点外面就关掉」生效，把方法体换成
    /// <see cref="CloseDetail"/> 即可。
    /// </summary>
    private void OnBackdropTapped(object? sender, TappedEventArgs e)
    {
    }

    private async void OnDetailPlayPauseClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_detail?.LocalAudioPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }

        if (_player.IsPlaying)
        {
            await _player.PauseAsync(CancellationToken.None);
            UpdateDetailPlaybackUi();
            return;
        }

        await _player.PlayAsync(path, CancellationToken.None);
        _detailPlaybackPath = path;

        StartPlaybackTimer();
        UpdateDetailPlaybackUi();
    });

    private void OnPlaybackCompleted(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StopPlaybackTimer();
            _detailPlaybackPath = null;
            UpdateDetailPlaybackUi();
        });

    private void OnDetailSeekStarted(object? sender, EventArgs e) => StopPlaybackTimer();

    private async void OnDetailSeekCompleted(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        await _player.SeekAsync(TimeSpan.FromSeconds(DetailSlider.Value), CancellationToken.None);
        UpdateDetailPlaybackUi();
    });

    private async void OnDetailSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_updatingSlider || _detail?.IsVoice != true)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(e.NewValue), CancellationToken.None);
            DetailPosition.Text = $"{Clock(TimeSpan.FromSeconds(e.NewValue))} / {Clock(_player.Duration)}";
        });
    }

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

            UpdateDetailPlaybackUi();
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

    private void UpdateDetailPlaybackUi()
    {
        var duration = _player.Duration;
        var position = _player.Position;

        DetailPlayButton.Text = _player.IsPlaying ? "⏸" : "▶";

        _updatingSlider = true;

        try
        {
            DetailSlider.Maximum = duration > TimeSpan.Zero ? duration.TotalSeconds : 1;
            DetailSlider.Value = Math.Clamp(position.TotalSeconds, 0, DetailSlider.Maximum);
        }
        finally
        {
            _updatingSlider = false;
        }

        DetailPosition.Text = $"{Clock(position)} / {Clock(duration)}";
    }

    private async void OnDeleteDetailClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_detail is not { } capture)
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

        await _controller.DeleteLocalAsync(capture.Id, CancellationToken.None);

        CloseDetail();

        _all = await _controller.ListLocalAsync(CancellationToken.None);
        ApplyFilter();

        StatusLabel.Text = "已从本机删除。";
    });

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Runs one handler body and reports instead of propagating: an exception escaping an <c>async void</c> handler
    /// kills the process, which is how a real phone lost this app once already.
    /// </summary>
    private async Task GuardAsync(Func<Task> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            await work();
        }
        catch (Exception)
        {
            StatusLabel.Text = "操作没有完成，请稍后重试。";
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Clock(TimeSpan value) =>
        value < TimeSpan.Zero
            ? "0:00"
            : value.TotalHours >= 1
                ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static Color ColorFromResource(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;
}
