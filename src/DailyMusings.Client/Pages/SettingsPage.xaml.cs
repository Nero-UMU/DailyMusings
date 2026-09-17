using DailyMusings.Client.Core.Settings;
using DailyMusings.Client.Services;

namespace DailyMusings.Client.Pages;

/// <summary>
/// Where the user points the app at their instance and pairs the device (docs/开发指导.md §9.3, §10.2, §10.3).
/// <para>
/// The risk notice here is not decoration. Saving a plain-HTTP address requires an explicit acknowledgement, the
/// banner stays visible afterwards, and the text never suggests the acknowledgement improved anything.
/// </para>
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly ClientSettings _settings;
    private readonly PairingService _pairing;
    private readonly SecureDeviceTokenProvider _tokens;
    private readonly DynamicInstanceApiClient _instance;

    public SettingsPage(
        ClientSettings settings,
        PairingService pairing,
        SecureDeviceTokenProvider tokens,
        DynamicInstanceApiClient instance)
    {
        InitializeComponent();

        _settings = settings;
        _pairing = pairing;
        _tokens = tokens;
        _instance = instance;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        ServerUrlEntry.Text = _settings.ServerBaseUrl ?? string.Empty;

        // The platform and its version, in that order: the label used to read "Android <windows version>" on Windows,
        // which is exactly the sort of small lie that makes a screenshot untrustworthy.
        DeviceInfoLabel.Text =
            $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model} · " +
            $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}";

        // Nothing in an async void handler may throw: this screen reads three things from the server, and any of them
        // failing must not take the process down. (The capture and draft screens learned this on a real device.)
        try
        {
            await RefreshAsync();
            await RefreshNotificationsAsync();
            await RefreshModelsAsync();
        }
        catch (Exception exception)
        {
            ServerStatus.Text = $"载入失败：{exception.Message}";
        }
    }

    private async void OnRefreshNotificationsClicked(object? sender, EventArgs e) =>
        await RefreshNotificationsAsync();

    /// <summary>
    /// §8.1's client-side half: which model each job will use, and whether it is switched on. Names only — the Base
    /// URL and the secret's name belong to the administrator who configured them.
    /// </summary>
    private async Task RefreshModelsAsync()
    {
        if (!_settings.IsConfigured || !await _tokens.HasTokenAsync(CancellationToken.None))
        {
            ModelStatus.Text = "配置服务器并配对后可以看到模型名。";
            return;
        }

        var models = await ((IModelNameApiClient)_instance).GetAsync(CancellationToken.None);

        if (!models.Succeeded)
        {
            ModelStatus.Text = DailyMusings.Client.Core.Reflections.ReflectionReview.DescribeTransportFailure(models.FailureCode);
            return;
        }

        ModelStatus.Text = string.Join(
            "\n",
            models.Value!.Select(model => $"{DescribeService(model.Service)}：{(model.Enabled ? model.Model : "未启用")}"));
    }

    private static string DescribeService(string service) => service switch
    {
        "transcription" => "语音转写",
        "generation" => "文章生成",
        "embedding" => "语义检索",
        _ => service,
    };

    /// <summary>
    /// Shows the notification preferences (docs/开发指导.md §9.3 通知偏好, §12) without offering to change them.
    /// The reason is worth a sentence on screen: a device token that could redirect the instance's mail could send
    /// the day's date and title to an address of the token holder's choosing, so the recipient and the event switches
    /// stay an administrator's decision on the admin page. What the client can do honestly is show the state.
    /// </summary>
    private async Task RefreshNotificationsAsync()
    {
        if (!_settings.IsConfigured || !await _tokens.HasTokenAsync(CancellationToken.None))
        {
            NotificationStatus.Text = "配置服务器并配对后可以看到通知偏好。";
            return;
        }

        var settings = await ((INotificationSettingsApiClient)_instance).GetAsync(CancellationToken.None);

        if (!settings.Succeeded)
        {
            NotificationStatus.Text = settings.FailureCode == "auth.forbidden"
                ? "通知偏好只对管理员显示；请到管理页的运维页修改。"
                : DailyMusings.Client.Core.Reflections.ReflectionReview.DescribeTransportFailure(settings.FailureCode);
            return;
        }

        var value = settings.Value!;
        var events = new List<string>();

        if (value.SmtpConfigured)
        {
            events.Add("SMTP 已配置");
        }
        else
        {
            events.Add("SMTP 未配置，不会发信");
        }

        if (value.DraftReady) events.Add("草稿待确认");
        if (value.JobFailed) events.Add("任务失败");
        if (value.AutomaticPublication) events.Add("自动发布");

        NotificationStatus.Text =
            $"收件地址：{(string.IsNullOrWhiteSpace(value.ToAddress) ? "未设置" : value.ToAddress)}\n" +
            $"已开启的事件：{string.Join("、", events)}\n" +
            "改动请到管理页的运维页（设备令牌没有修改收件地址的权限）。";
    }

    private async void OnSaveServerClicked(object? sender, EventArgs e)
    {
        var candidate = ServerUrlEntry.Text?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            _settings.ServerBaseUrl = null;
            await RefreshAsync();
            ServerStatus.Text = "已清除服务器地址。";
            return;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            ServerStatus.Text = "地址需要以 http:// 或 https:// 开头。";
            return;
        }

        // §10.3: the risk must be stated before the address takes effect, and confirming it is a gate rather than a
        // mitigation. This is the client-side counterpart of the acknowledgement the admin login page requires.
        if (uri.Scheme == "http")
        {
            var acknowledged = await DisplayAlertAsync(
                "这是未加密的连接",
                "通过 HTTP 访问时，密码、设备令牌、录音和文章都可能被同一网络中的其他人截获。\n\n" +
                "确认本提示不会让连接变安全，它只是说明风险。推荐改用 HTTPS、局域网或 VPN。\n\n仍要继续吗？",
                "我已了解风险，继续",
                "取消");

            if (!acknowledged)
            {
                ServerStatus.Text = "已取消，地址未改变。";
                return;
            }
        }

        _settings.ServerBaseUrl = uri.ToString();
        await RefreshAsync();
        ServerStatus.Text = "地址已保存。";
    }

    private async void OnTestConnectionClicked(object? sender, EventArgs e)
    {
        ServerStatus.Text = "正在测试…";

        var reachable = await _pairing.TestConnectionAsync(CancellationToken.None);

        ServerStatus.Text = reachable
            ? "服务器有响应。"
            : "连接失败，请检查地址、网络，以及服务端是否在运行。";
    }

    private async void OnPairClicked(object? sender, EventArgs e)
    {
        var code = PairingCodeEntry.Text?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            PairingStatus.Text = "请先填入管理页生成的配对码。";
            return;
        }

        PairingStatus.Text = "正在配对…";

        var result = await _pairing.RedeemAsync(code, CancellationToken.None);

        if (result.Succeeded)
        {
            PairingCodeEntry.Text = string.Empty;
            PairingStatus.Text = $"配对成功：{result.DeviceName}。录音与文字现在会自动上传。";
            await RefreshAsync();
            return;
        }

        PairingStatus.Text = result.FailureMessage ?? "配对失败。";
    }

    private async void OnUnpairClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync(
            "解除配对？",
            "本机会删除设备令牌。已经上传的内容不受影响，但要重新配对才能继续上传。",
            "解除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        _tokens.Clear();

        // The offline queue is deliberately left alone: unpairing must not throw away thoughts that were never sent.
        PairingStatus.Text = "已解除配对。待上传的内容仍然保留在本机。";
        await RefreshAsync();
        await RefreshNotificationsAsync();
    }

    private async Task RefreshAsync()
    {
        InsecureBanner.IsVisible = _settings.IsInsecureConnection;

        var paired = await _tokens.HasTokenAsync(CancellationToken.None);

        PairingState.Text = paired
            ? "此设备已配对，可以上传。"
            : "此设备尚未配对。";
    }
}
