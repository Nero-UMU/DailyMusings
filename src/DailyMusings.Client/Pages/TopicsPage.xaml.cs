using DailyMusings.Client.Core;
using System.Collections.ObjectModel;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Client.Core.Topics;
using DailyMusings.Client.Services;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Pages;

/// <summary>One topic in the list, with what the user can do to it.</summary>
public sealed record TopicRow(string Id, string Name, string Detail, bool CanRename);

/// <summary>
/// The topic vocabulary (docs/开发指导.md §9.3 主题：浏览、重命名、合并和调整归属, §6.2).
/// <para>
/// The point of this screen is that the vocabulary stays one the user recognises: automatic recognition only files
/// material under topics that already exist, so browsing, renaming and merging are how they keep it usable. Merging
/// leaves a tombstone rather than rewriting history (A.9), which is why the merged topic disappears from this list
/// and the target's count grows.
/// </para>
/// </summary>
public partial class TopicsPage : ContentPage
{
    private readonly ClientSettings _settings;
    private readonly DynamicTopicApiClient _topics;

    private readonly ObservableCollection<TopicRow> _rows = [];

    private bool _busy;

    public TopicsPage(ClientSettings settings, DynamicTopicApiClient topics)
    {
        InitializeComponent();

        _settings = settings;
        _topics = topics;

        TopicList.ItemsSource = _rows;
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

    private async Task LoadAsync()
    {
        ActionStatus.Text = string.Empty;

        var listed = await _topics.ListAsync(includeMerged: false, CancellationToken.None);

        _rows.Clear();

        if (!listed.Succeeded)
        {
            TopicStatus.Text = ReflectionReview.DescribeTransportFailure(listed.FailureCode);
            return;
        }

        foreach (var topic in listed.Value!.OrderBy(topic => topic.Name, StringComparer.CurrentCulture))
        {
            _rows.Add(new TopicRow(topic.Id, topic.Name, "点「重命名」改名；合并会把材料转到另一个主题。", true));
        }

        TopicStatus.Text = _rows.Count == 0
            ? "还没有任何主题。自动识别只会把内容归入已存在的主题，所以主题要由你命名。"
            : $"共 {_rows.Count} 个主题。";
    }

    private async void OnCreateClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var name = NewTopicName.Text?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            ActionStatus.Text = "先写一个主题名。";
            return;
        }

        var created = await _topics.CreateAsync(name, CancellationToken.None);

        if (!created.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(created.FailureCode);
            return;
        }

        NewTopicName.Text = string.Empty;
        await LoadAsync();
        ActionStatus.Text = $"已添加主题「{created.Value!.Name}」。";
    });

    private async void OnRenameClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not Button { CommandParameter: string topicId })
        {
            return;
        }

        var row = _rows.FirstOrDefault(item => item.Id == topicId);

        var name = await DisplayPromptAsync(
            "重命名主题",
            "改成什么名字？",
            accept: "改名",
            cancel: "取消",
            initialValue: row?.Name ?? string.Empty);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var renamed = await _topics.RenameAsync(topicId, name, CancellationToken.None);

        if (!renamed.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(renamed.FailureCode);
            return;
        }

        await LoadAsync();
        ActionStatus.Text = $"已改名为「{renamed.Value!.Name}」。";
    });

    private async void OnMergeClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not Button { CommandParameter: string sourceId })
        {
            return;
        }

        var source = _rows.FirstOrDefault(item => item.Id == sourceId);
        var targets = _rows.Where(item => item.Id != sourceId).ToArray();

        if (targets.Length == 0)
        {
            ActionStatus.Text = "至少要有一个别的主题才能合并。";
            return;
        }

        var choice = await DisplayActionSheetAsync(
            $"把「{source?.Name}」合并到",
            "取消",
            null,
            targets.Select(item => item.Name).ToArray());

        var target = targets.FirstOrDefault(item => item.Name == choice);

        if (target is null)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "确认合并？",
            $"「{source?.Name}」的材料会转到「{target.Name}」。历史文章的来源映射不会改变（A.9）。",
            "合并",
            "取消");

        if (!confirmed)
        {
            return;
        }

        var merged = await _topics.MergeAsync(sourceId, target.Id, CancellationToken.None);

        if (!merged.Succeeded)
        {
            ActionStatus.Text = ReflectionReview.DescribeTransportFailure(merged.FailureCode);
            return;
        }

        await LoadAsync();
        ActionStatus.Text = $"已把「{merged.Value!.Source.Name}」合并到「{target.Name}」，{merged.Value.RemappedInputs} 条材料换了归属。";
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
