using DailyMusings.Client.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Client.Services;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Pages;

/// <summary>One of the three version slots (§6.4).</summary>
public sealed record VersionRow(string Slot, string Detail, string VersionId, bool CanSwitch);

/// <summary>One resolved citation, as the reviewer sees it (§8.4, decision A.6).</summary>
public sealed record SourceRow(string Quote, string Origin, string DriftNote, bool HasDriftNote);

/// <summary>One sentence the source check could not trace (§8.4).</summary>
public sealed record UnsourcedRow(string Quote, string Reason);

/// <summary>One publication of this version (§11.1).</summary>
public sealed record PublicationRow(string Summary, string Detail, bool HasDetail, string PublicationId, bool CanCheckRemote, bool IsInFlight);

/// <summary>
/// The draft screen (docs/开发指导.md §9.3 草稿：编辑、来源核验、版本切换和发布).
/// <para>
/// The rules it enforces live in <see cref="ReflectionReview"/>, which is tested on its own: this page reads the
/// state, draws what those rules say, and sends the user's answers back. Two of those answers are the product's
/// promises — a hand-edited version is only rotated after an explicit confirmation (§6.4), and sentences the source
/// check could not trace are only confirmed after the user accepted them (§8.4) — so both are asked for here and
/// both are also enforced by the server.
/// </para>
/// </summary>
public partial class DraftPage : ContentPage, IQueryAttributable
{
    private readonly ClientSettings _settings;
    private readonly DynamicReflectionApiClient _api;
    private readonly SecureDeviceTokenProvider _tokens;

    private readonly ObservableCollection<string> _warnings = [];
    private readonly ObservableCollection<VersionRow> _versions = [];
    private readonly ObservableCollection<SourceRow> _sources = [];
    private readonly ObservableCollection<UnsourcedRow> _unsourced = [];
    private readonly ObservableCollection<PublicationRow> _publications = [];

    private readonly List<PublishTargetDto> _targets = [];

    private ReflectionDto? _reflection;
    private bool _busy;
    private IDispatcherTimer? _publicationTimer;
    private int _publicationPolls;

    /// <summary>
    /// How long to keep looking at a publication after asking for one.
    /// <para>
    /// Bounded, and for the same reason the capture screen bounds its transcription polling: publishing is a queued
    /// job, so the row says 排队中 the moment the request is accepted — and it said that forever until the user
    /// reloaded the page, which is exactly what a phone showed. The screen now looks again until the job settles.
    /// </para>
    /// </summary>
    private const int MaxPublicationPolls = 10;

    private static readonly TimeSpan PublicationPollInterval = TimeSpan.FromSeconds(3);

    public DraftPage(ClientSettings settings, DynamicReflectionApiClient api, SecureDeviceTokenProvider tokens)
    {
        InitializeComponent();

        _settings = settings;
        _api = api;
        _tokens = tokens;

        WarningList.ItemsSource = _warnings;
        VersionList.ItemsSource = _versions;
        SourceList.ItemsSource = _sources;
        UnsourcedList.ItemsSource = _unsourced;
        PublicationList.ItemsSource = _publications;

        DateSelector.Date = DateTime.Today;

        VisibilityPicker.ItemsSource = new List<string> { "草稿", "公开" };
        VisibilityPicker.SelectedIndex = 0;
    }

    /// <summary>The shell route of this screen, so the calendar can hand a date over (§9.3).</summary>
    public const string TabRoute = "//draft";

    /// <summary>The day this screen opens on.</summary>
    public static string RouteFor(DateOnly date) => $"//draft?date={date:yyyy-MM-dd}";

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPublicationPolling();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            InsecureBanner.IsVisible = _settings.IsInsecureConnection;

            await LoadAsync();
        }
        catch (Exception exception)
        {
            // Nothing in an async void handler may throw: the screen must survive a failure to load.
            ActionStatus.Text = $"载入失败：{exception.Message}";
        }
    }

    /// <summary>
    /// Opens a specific day, which is how the calendar screen will hand a date over (§9.3). Shell delivers the value
    /// already decoded, so the date is parsed rather than unescaped here.
    /// </summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.TryGetValue("date", out var value) &&
            value is string text &&
            DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            DateSelector.Date = parsed.ToDateTime(TimeOnly.MinValue);
        }
    }

    private static string SelectedDate(DatePicker picker) =>
        DateOnly.FromDateTime(picker.Date ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async void OnDateSelected(object? sender, DateChangedEventArgs e) => await GuardAsync(LoadAsync);

    private async void OnLoadClicked(object? sender, EventArgs e) => await GuardAsync(LoadAsync);

    /// <summary>
    /// Reads the day and everything the screen needs to draw it. Every failure is reported in the page's own words
    /// rather than thrown: the same code runs while the page appears.
    /// </summary>
    private async Task LoadAsync()
    {
        ActionStatus.Text = string.Empty;

        // The previous load's verdict is cleared first: a stale "连不上服务器" left on screen while a reload is in
        // flight reads as the current state, which is how a missing draft looked like a network failure on a phone.
        DraftStatus.Text = "正在载入…";
        DraftDetail.Text = string.Empty;

        var contentDate = SelectedDate(DateSelector);

        var reflection = await _api.GetAsync(contentDate, CancellationToken.None);

        if (!reflection.Succeeded)
        {
            _reflection = null;
            _versions.Clear();
            _sources.Clear();
            _unsourced.Clear();
            _publications.Clear();
            _warnings.Clear();
            WarningList.IsVisible = false;
            AcceptUnsourced.IsVisible = false;
            AcceptUnsourcedLabel.IsVisible = false;

            if (ReflectionReview.IsMissingDraft(reflection.FailureCode))
            {
                DraftStatus.Text = "这一天还没有草稿。";
                DraftDetail.Text = "在今日页记录一点内容，或直接在这里生成：点下面的「重新生成」。";

                // 重新生成 is exactly the action this state calls for, so it stays enabled. Found on a phone: the
                // page told the user to press it while disabling it, which left a fresh day as a dead end.
                SetActionsEnabled(false);
                RegenerateButton.IsEnabled = true;
            }
            else
            {
                DraftStatus.Text = ReflectionReview.DescribeTransportFailure(reflection.FailureCode);

                // The code itself, for the operator: this product is self-hosted, and "which failure was it" is the
                // first question when a screen refuses to load.
                DraftDetail.Text = $"失败原因：{reflection.FailureCode}";
                SetActionsEnabled(false);
            }

            return;
        }

        _reflection = reflection.Value;
        Draw(_reflection!);

        await LoadTargetsAsync();
        await LoadPublicationsAsync(contentDate);
    }

    private void Draw(ReflectionDto reflection)
    {
        DraftStatus.Text = ReflectionReview.DescribeStatus(reflection.Status);
        DraftDetail.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"内容日期 {reflection.ContentDate} · {ReflectionReview.DescribeGenerationReason(reflection.GenerationReason)}");

        _warnings.Clear();
        foreach (var warning in ReflectionReview.DescribeWarnings(reflection))
        {
            _warnings.Add(warning);
        }

        WarningList.IsVisible = _warnings.Count > 0;

        DrawVersions(reflection);
        DrawWorkingVersion(reflection.WorkingVersion);

        SetActionsEnabled(true);
    }

    private void DrawVersions(ReflectionDto reflection)
    {
        _versions.Clear();

        foreach (var version in new[] { reflection.InitialVersion, reflection.PreviousVersion, reflection.WorkingVersion })
        {
            if (version is null)
            {
                continue;
            }

            // The same version can occupy two slots early on (initial is usually the working one), and a duplicate
            // row would only invite the user to switch to where they already are.
            if (_versions.Any(row => row.VersionId == version.Id))
            {
                continue;
            }

            var slot = ReflectionReview.DescribeVersionSlot(reflection, version.Id);
            var detail = version.HasManualEdits
                ? $"手工修改过 · {DescribeChecked(version)}"
                : DescribeChecked(version);

            _versions.Add(new VersionRow(
                slot,
                detail,
                version.Id,
                version.Id != reflection.WorkingVersionId));
        }

        VersionSummary.Text = reflection.ConfirmedVersionId is { Length: > 0 } confirmed
            ? $"已确认版本：{ReflectionReview.DescribeVersionSlot(reflection, confirmed)}"
            : "还没有确认过这一天的草稿。";
    }

    private static string DescribeChecked(ReflectionVersionDto version) =>
        version.SourcesCheckedAtUtc is null
            ? "来源检查还没完成"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{version.Sources.Count} 处引用 · {version.UnsourcedClaims.Count} 处存疑");

    private void DrawWorkingVersion(ReflectionVersionDto? working)
    {
        TitleEntry.Text = working?.Title ?? string.Empty;
        SummaryEntry.Text = working?.Summary ?? string.Empty;
        BodyEditor.Text = working?.Body ?? string.Empty;

        _sources.Clear();
        _unsourced.Clear();

        if (working is null)
        {
            SourceStatus.Text = "这一天还没有可核验的版本。";
            UnsourcedStatus.Text = string.Empty;
            AcceptUnsourced.IsVisible = false;
            AcceptUnsourcedLabel.IsVisible = false;
            return;
        }

        foreach (var source in working.Sources)
        {
            var quote = ReflectionReview.SliceQuote(working.Body, source.BlockIndex, source.CharStart, source.CharEnd);

            _sources.Add(new SourceRow(
                string.IsNullOrWhiteSpace(quote)
                    ? ReflectionReview.BlockAt(working.Body, source.BlockIndex)
                    : quote,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"第 {source.BlockIndex + 1} 段 · {ReflectionReview.DescribeSourceOrigin(source)} · 相关度 {source.Relevance:0.00}"),
                source.Drift == SourceDriftNames.Exact ? string.Empty : "正文改动后，这段引用已不再精确对应。",
                source.Drift != SourceDriftNames.Exact));
        }

        foreach (var claim in working.UnsourcedClaims)
        {
            var quote = ReflectionReview.SliceQuote(working.Body, claim.BlockIndex, claim.CharStart, claim.CharEnd);

            _unsourced.Add(new UnsourcedRow(
                string.IsNullOrWhiteSpace(quote)
                    ? ReflectionReview.BlockAt(working.Body, claim.BlockIndex)
                    : quote,
                claim.Reason));
        }

        SourceStatus.Text = _sources.Count == 0
            ? "这一版没有来源引用。"
            : string.Create(CultureInfo.InvariantCulture, $"{_sources.Count} 处引用（逐字对应正文）。");

        var needsAcknowledgement = ReflectionReview.ConfirmNeedsAcknowledgement(working);

        UnsourcedStatus.Text = working.SourcesCheckedAtUtc is null
            ? "来源检查还没完成。"
            : needsAcknowledgement
                ? $"{_unsourced.Count} 句无法追溯到任何输入；确认前需要先接受它们。"
                : "来源检查已完成，没有无法追溯的句子。";

        AcceptUnsourced.IsVisible = needsAcknowledgement;
        AcceptUnsourcedLabel.IsVisible = needsAcknowledgement;
        AcceptUnsourced.IsChecked = false;
    }

    private async Task LoadTargetsAsync()
    {
        var targets = await _api.ListTargetsAsync(CancellationToken.None);

        _targets.Clear();
        TargetPicker.ItemsSource = null;

        if (!targets.Succeeded)
        {
            PublicationStatus.Text = ReflectionReview.DescribeTransportFailure(targets.FailureCode);
            return;
        }

        _targets.AddRange(targets.Value!);

        TargetPicker.ItemsSource = _targets
            .Select(target => string.Create(CultureInfo.InvariantCulture, $"{target.Name}（{DescribeTargetType(target.Type)}）"))
            .ToList();

        if (_targets.Count > 0)
        {
            TargetPicker.SelectedIndex = 0;
        }
    }

    private static string DescribeTargetType(string type) => "Hexo Markdown";

    private async Task LoadPublicationsAsync(string contentDate)
    {
        var publications = await _api.ListPublicationsAsync(contentDate, CancellationToken.None);

        _publications.Clear();

        if (!publications.Succeeded)
        {
            PublicationStatus.Text = ReflectionReview.DescribeTransportFailure(publications.FailureCode);
            return;
        }

        foreach (var publication in publications.Value!)
        {
            _publications.Add(ToRow(publication));
        }

        PublicationStatus.Text = _publications.Count == 0
            ? "这一天还没有发布记录。"
            : string.Create(CultureInfo.InvariantCulture, $"{_publications.Count} 条发布记录。");
    }

    private static PublicationRow ToRow(PublicationDto publication)
    {
        var detail = new List<string>();

        if (publication.RemoteId is { Length: > 0 } remoteId)
        {
            detail.Add($"远端标识：{remoteId}");
        }

        if (publication.ErrorCode is { Length: > 0 } errorCode)
        {
            detail.Add($"错误码：{errorCode}");
        }

        if (publication.AttemptCount > 1)
        {
            detail.Add($"已尝试 {publication.AttemptCount} 次");
        }

        return new PublicationRow(
            ReflectionReview.DescribePublication(publication),
            string.Join(" · ", detail),
            detail.Count > 0,
            publication.Id,
            publication.Status is PublicationStatusNames.DraftUploaded or PublicationStatusNames.Published,
            publication.Status is PublicationStatusNames.Queued or PublicationStatusNames.InProgress);
    }

    private async void OnRefreshPublicationsClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        await LoadPublicationsAsync(SelectedDate(DateSelector));
        ActionStatus.Text = "已刷新发布状态。";
    });

    private void StartPublicationPolling()
    {
        if (_publicationTimer is not null)
        {
            return;
        }

        _publicationPolls = 0;
        _publicationTimer = Dispatcher.CreateTimer();
        _publicationTimer.Interval = PublicationPollInterval;
        _publicationTimer.Tick += OnPublicationTick;
        _publicationTimer.Start();
    }

    private async void OnPublicationTick(object? sender, EventArgs e)
    {
        _publicationPolls++;

        if (_publicationPolls > MaxPublicationPolls)
        {
            StopPublicationPolling();
            return;
        }

        try
        {
            await LoadPublicationsAsync(SelectedDate(DateSelector));

            // Nothing is queued or running any more, so there is nothing left to watch.
            if (_publications.All(row => !row.IsInFlight))
            {
                StopPublicationPolling();

                // The verdict is put in the action line too: the row's own text is quiet about *when* it settled.
                if (_publications.Count > 0)
                {
                    ActionStatus.Text = _publications[0].Summary;
                }
            }
        }
        catch (Exception exception)
        {
            StopPublicationPolling();
            ActionStatus.Text = $"刷新发布状态失败：{exception.Message}";
        }
    }

    private void StopPublicationPolling()
    {
        if (_publicationTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnPublicationTick;
            _publicationTimer = null;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        ConfirmButton.IsEnabled = enabled;
        RegenerateButton.IsEnabled = enabled;
        PublishButton.IsEnabled = enabled;
    }

    // ---------------------------------------------------------------- actions

    private async void OnSaveEditClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (_reflection?.WorkingVersion is null)
        {
            ActionStatus.Text = "还没有可编辑的版本。";
            return;
        }

        var contentDate = SelectedDate(DateSelector);

        var saved = await _api.EditAsync(
            contentDate,
            TitleEntry.Text ?? string.Empty,
            SummaryEntry.Text ?? string.Empty,
            BodyEditor.Text ?? string.Empty,
            CancellationToken.None);

        if (!saved.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(saved.FailureCode);
            return;
        }

        _reflection = saved.Value;
        Draw(_reflection!);
        ActionStatus.Text = "已保存修改。原版本仍保留在版本列表里。";
    });

    private async void OnSwitchVersionClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not Button { CommandParameter: string versionId })
        {
            return;
        }

        var switched = await _api.SwitchVersionAsync(SelectedDate(DateSelector), versionId, CancellationToken.None);

        if (!switched.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(switched.FailureCode);
            return;
        }

        _reflection = switched.Value;
        Draw(_reflection!);
        ActionStatus.Text = "已切换当前版本。";
    });

    private async void OnConfirmClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var working = _reflection?.WorkingVersion;

        if (working is null)
        {
            ActionStatus.Text = "还没有可确认的版本。";
            return;
        }

        // §8.4: the acknowledgement is the user's, so it is asked for here and the server refuses without it.
        if (ReflectionReview.ConfirmNeedsAcknowledgement(working) && AcceptUnsourced.IsChecked != true)
        {
            ActionStatus.Text = "这一版还有无法追溯的句子，先勾选接受它们再确认。";
            return;
        }

        var confirmed = await _api.ConfirmAsync(
            SelectedDate(DateSelector),
            acceptedUnsourcedClaims: AcceptUnsourced.IsChecked == true,
            CancellationToken.None);

        if (!confirmed.Succeeded)
        {
            ActionStatus.Text = confirmed.ServerReached
                ? ReflectionReview.DescribeConfirmRefusal(confirmed.FailureCode)
                : ReflectionReview.DescribeTransportFailure(confirmed.FailureCode);
            return;
        }

        _reflection = confirmed.Value;
        Draw(_reflection!);
        await LoadPublicationsAsync(SelectedDate(DateSelector));
        ActionStatus.Text = "这一天已确认。现在可以发布它。";
    });

    private async void OnRegenerateClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var allowOverwrite = false;

        // §6.4: losing hand edits is the user's decision, and the server refuses to rotate without it.
        if (ReflectionReview.RegenerateNeedsOverwriteConfirmation(_reflection))
        {
            allowOverwrite = await DisplayAlertAsync(
                "重新生成会覆盖手工修改",
                "当前版本有你手工改过的内容。重新生成会把这一版移到「上一版」，并生成新的一版作为当前版本。",
                "继续重新生成",
                "取消");

            if (!allowOverwrite)
            {
                return;
            }
        }

        var generated = await _api.GenerateAsync(
            SelectedDate(DateSelector),
            ignoreTranscriptionFailures: IgnoreTranscriptionFailures.IsChecked,
            allowOverwriteOfManualEdits: allowOverwrite,
            CancellationToken.None);

        if (!generated.Succeeded)
        {
            ActionStatus.Text = generated.ServerReached
                ? ReflectionReview.DescribeGenerationRefusal(generated.FailureCode)
                : ReflectionReview.DescribeTransportFailure(generated.FailureCode);
            return;
        }

        if (generated.Value!.Queued == false)
        {
            ActionStatus.Text = ReflectionReview.DescribeGenerationRefusal(generated.Value.Code);
            return;
        }

        ActionStatus.Text = "已加入生成队列，稍后回到这一页看新的一版。";
    });

    private async void OnPublishClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (TargetPicker.SelectedIndex < 0 || TargetPicker.SelectedIndex >= _targets.Count)
        {
            ActionStatus.Text = "先在设置里配置一个发布目标。";
            return;
        }

        var target = _targets[TargetPicker.SelectedIndex];
        var visibility = VisibilityPicker.SelectedIndex == 1
            ? PublicationVisibilityNames.Public
            : PublicationVisibilityNames.Draft;

        if (visibility == PublicationVisibilityNames.Public)
        {
            var goAhead = await DisplayAlertAsync(
                "公开这一版？",
                "内容会立刻在目标站点上公开可见。",
                "公开",
                "取消");

            if (!goAhead)
            {
                return;
            }
        }

        var published = await _api.PublishAsync(
            SelectedDate(DateSelector),
            target.Id,
            visibility,
            replaceExistingFile: true,
            CancellationToken.None);

        if (!published.Succeeded)
        {
            ActionStatus.Text = published.ServerReached
                ? ReflectionReview.DescribePublishRefusal(published.FailureCode)
                : ReflectionReview.DescribeTransportFailure(published.FailureCode);
            return;
        }

        if (published.Value!.Queued == false)
        {
            ActionStatus.Text = ReflectionReview.DescribePublishRefusal(published.Value.Code);
            return;
        }

        ActionStatus.Text = "已加入发布队列。";
        await LoadPublicationsAsync(SelectedDate(DateSelector));
        StartPublicationPolling();
    });

    private async void OnCheckRemoteClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not Button { CommandParameter: string publicationId })
        {
            return;
        }

        var check = await _api.CheckRemoteAsync(publicationId, CancellationToken.None);

        if (!check.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(check.FailureCode);
            return;
        }

        var remote = check.Value!;

        ActionStatus.Text = (remote.RemoteChecked, remote.LocalChanged, remote.RemoteChanged) switch
        {
            (false, _, _) => "远程还没读到，稍后再试。",
            (_, false, false) => "远端与已发布的版本一致。",
            (_, true, false) => "本地改动过，远端还是旧的；重新发布即可更新。",
            (_, false, true) => "远端被改过了；发布时会覆盖它，或者到管理页处理差异。",
            _ => "本地和远端都变了，请到管理页处理差异。",
        };

        // No polling here on purpose: reading the remote is a synchronous answer, and watching it again would
        // overwrite the verdict the user just asked for (which is what happened on a phone).
        await LoadPublicationsAsync(SelectedDate(DateSelector));
    });

    /// <summary>
    /// Runs one handler body and reports instead of propagating — the same guard the capture screen uses, for the
    /// same reason: only the top of an <c>async void</c> can do this.
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
        catch (Exception exception)
        {
            ActionStatus.Text = $"操作失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
        }
    }
}
