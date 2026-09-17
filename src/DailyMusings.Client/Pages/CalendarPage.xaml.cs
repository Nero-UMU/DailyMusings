using DailyMusings.Client.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Client.Services;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Pages;

/// <summary>One day in the calendar: what was captured and what the draft looks like (§9.3 日历).</summary>
public sealed record CalendarDayRow(string Date, string Inputs, string Draft, bool CanOpen, string ContentDate);

/// <summary>
/// The calendar (docs/开发指导.md §9.3 日历：按日期查看输入与随想状态).
/// <para>
/// It answers one question quickly — "which days have material, and which of them have a draft I still have to
/// review?" — and hands a chosen day to the draft screen. Two reads do that: the inputs of each day and the drafts
/// in the range, both of which the server already serves (the range is capped at 366 days and defaults to 31, so a
/// month is what this asks for).
/// </para>
/// </summary>
public partial class CalendarPage : ContentPage
{
    private readonly ClientSettings _settings;
    private readonly DynamicReflectionApiClient _reflections;
    private readonly DynamicCaptureApiClient _captures;

    private readonly ObservableCollection<CalendarDayRow> _days = [];

    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private bool _busy;

    public CalendarPage(ClientSettings settings, DynamicReflectionApiClient reflections, DynamicCaptureApiClient captures)
    {
        InitializeComponent();

        _settings = settings;
        _reflections = reflections;
        _captures = captures;

        DayList.ItemsSource = _days;
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
            ActionStatus.Text = $"载入失败：{exception.Message}";
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async void OnPreviousMonthClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        _anchor = _anchor.AddMonths(-1);
        await LoadAsync();
    });

    private async void OnNextMonthClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        _anchor = _anchor.AddMonths(1);
        await LoadAsync();
    });

    private async void OnTodayClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        _anchor = DateOnly.FromDateTime(DateTime.Today);
        await LoadAsync();
    });

    private async Task LoadAsync()
    {
        ActionStatus.Text = string.Empty;
        _days.Clear();

        var first = new DateOnly(_anchor.Year, _anchor.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);

        MonthStatus.Text = string.Create(CultureInfo.InvariantCulture, $"{_anchor.Year} 年 {_anchor.Month} 月");

        var drafts = await _reflections.ListAsync(Iso(first), Iso(last), CancellationToken.None);

        if (!drafts.ServerReached)
        {
            MonthStatus.Text += " · 连不上服务器";
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(drafts.FailureCode);
            return;
        }

        var byDate = (drafts.Value ?? []).ToDictionary(draft => draft.ContentDate, StringComparer.Ordinal);

        // One read for the whole month's entries rather than one per day: a phone on a relayed connection pays for
        // every round trip, and the task is only counting what each day holds.
        var recent = await _captures.GetInputsAsync(contentDate: null, CancellationToken.None);

        var inputsByDate = (recent.Value ?? [])
            .GroupBy(input => input.ContentDate, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // Newest first: the days a person is most likely to look at are the ones just behind them.
        for (var date = last; date >= first; date = date.AddDays(-1))
        {
            var contentDate = Iso(date);

            var inputCount = inputsByDate.GetValueOrDefault(contentDate);
            var draft = byDate.GetValueOrDefault(contentDate);

            // Days with nothing at all are noise in a month view; §9.3 asks for what was captured and what the draft
            // state is, and a day with neither has nothing to say.
            if (inputCount == 0 && draft is null)
            {
                continue;
            }

            _days.Add(new CalendarDayRow(
                date.ToString("MM-dd ddd", CultureInfo.CurrentCulture),
                inputCount == 0 ? "没有输入" : $"输入 {inputCount} 条",
                draft is null ? "还没有草稿" : ReflectionReview.DescribeStatus(draft.Status),
                CanOpen: true,
                contentDate));
        }

        ActionStatus.Text = _days.Count == 0
            ? "这个月还没有任何输入或草稿。"
            : $"共 {_days.Count} 天有内容。点一天打开它的草稿。";
    }

    private async void OnOpenDayClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not Button { CommandParameter: string contentDate })
        {
            return;
        }

        await Shell.Current.GoToAsync($"{DraftPage.TabRoute}?date={contentDate}");
    });

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
