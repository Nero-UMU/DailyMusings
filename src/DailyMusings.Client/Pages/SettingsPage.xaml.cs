using System.Globalization;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Core.Settings;
using DailyMusings.Client.Services;

namespace DailyMusings.Client.Pages;

/// <summary>
/// 设置 (phone spec §3.4): where the instance is, whether this device is paired, how much room the local archive
/// takes, and the model names the instance is configured with.
/// <para>
/// Notification preferences are owned by the admin page, so what is left is exactly what a phone needs to connect,
/// pair, inspect the active model names, and manage its local archive.
/// </para>
/// </summary>
public partial class SettingsPage : ContentPage
{
    private readonly ClientSettings _settings;
    private readonly PairingService _pairing;
    private readonly SecureDeviceTokenProvider _tokens;
    private readonly IModelNameApiClient _models;
    private readonly CaptureController _controller;
    private readonly IAudioPlayer _player;

    private bool _busy;

    public SettingsPage(
        ClientSettings settings,
        PairingService pairing,
        SecureDeviceTokenProvider tokens,
        IModelNameApiClient models,
        CaptureController controller,
        IAudioPlayer player)
    {
        InitializeComponent();

        _settings = settings;
        _pairing = pairing;
        _tokens = tokens;
        _models = models;
        _controller = controller;
        _player = player;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        ServerUrlEntry.Text = _settings.ServerBaseUrl ?? string.Empty;

        DeviceInfoLabel.Text =
            $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model} · " +
            $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}";

        // Nothing in an async void handler may throw: this screen reads the token store, the archive and the
        // instance, and any of them failing must not take the process down.
        try
        {
            await RefreshAsync();
            await RefreshInstanceInfoAsync();
        }
        catch (Exception)
        {
            ServerStatus.Text = "暂时无法载入设置，请关闭页面后重试。";
        }
    }

    private async void OnRefreshModelsClicked(object? sender, EventArgs e) => await GuardAsync(RefreshModelsAsync);

    /// <summary>
    /// §8.1's client-side half: which model each job will use, and whether it is switched on. Names only — the Base
    /// URL and the secret's name belong to the administrator who configured them.
    /// </summary>
    private async Task RefreshModelsAsync()
    {
        if (!_settings.IsConfigured || !await IsPairedAsync())
        {
            ModelSection.IsVisible = false;
            ModelStatus.Text = "配置服务器并配对后可以看到模型名。";
            return;
        }

        ModelSection.IsVisible = true;

        var models = await _models.GetAsync(CancellationToken.None);

        if (!models.Succeeded)
        {
            ModelStatus.Text = DescribeTransportFailure(models.FailureCode);
            return;
        }

        ModelStatus.Text = models.Value!.Count == 0
            ? "实例还没有配置任何模型。"
            : string.Join(
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

    private async void OnSaveServerClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var candidate = ServerUrlEntry.Text?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            _settings.ServerBaseUrl = null;
            await RefreshAsync();
            await RefreshInstanceInfoAsync();
            ServerStatus.Text = "已清除服务器地址。本机记录仍然保留。";
            return;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            ServerStatus.Text = "地址需要以 http:// 或 https:// 开头。";
            return;
        }

        _settings.ServerBaseUrl = uri.ToString();
        await RefreshAsync();
        await RefreshInstanceInfoAsync();
        ServerStatus.Text = "地址已保存。";
    });

    private async void OnTestConnectionClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        ServerStatus.Text = "正在测试…";

        var reachable = await _pairing.TestConnectionAsync(CancellationToken.None);

        ServerStatus.Text = reachable
            ? "服务器有响应。接下来用管理页签发的配对码完成配对。"
            : "连接失败，请检查地址、网络，以及服务端是否在运行。";
    });

    private async void OnPairClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
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
            PairingStatus.Text = $"配对成功：{result.DeviceName}。现在可以把本机的随想上传到服务器。";

            // The model section only becomes readable once the device has a token, so pairing is exactly when it has
            // to be read. Without this the page kept telling a freshly paired device to go and pair.
            await RefreshAsync();
            await RefreshInstanceInfoAsync();
            return;
        }

        PairingStatus.Text = result.FailureMessage ?? "配对失败。";
    });

    private async void OnUnpairClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {
        var confirmed = await DisplayAlertAsync(
            "解除配对？",
            "本机会删除设备令牌。本机记录不受影响，但要重新配对才能上传。",
            "解除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        _tokens.Clear();

        // The local archive is deliberately left alone: unpairing must not throw away thoughts that were never sent.
        PairingStatus.Text = "已解除配对。本机记录仍然保留。";
        await RefreshAsync();
        await RefreshInstanceInfoAsync();
    });

    private async void OnClearLocalClicked(object? sender, EventArgs e) => await GuardAsync(async () =>
    {        var usage = await _controller.GetLocalUsageAsync(CancellationToken.None);

        if (usage.Count == 0)
        {
            LocalStatus.Text = "本机没有记录可清。";
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "清空本机记录？",
            $"这会删除本机的 {usage.Count} 条记录和全部录音（{FormatBytes(usage.AudioBytes)}）。\n\n" +
            "还没上传的内容会因此永久丢失；已经上传到服务器的内容不会被删除。\n\n这一步无法撤销。",
            "确认清空",
            "取消");

        if (!confirmed)
        {
            LocalStatus.Text = "已取消，本机记录未改变。";
            return;
        }

        await _player.StopAsync(CancellationToken.None);

        var removed = await _controller.ClearLocalAsync(CancellationToken.None);

        await RefreshLocalUsageAsync();
        LocalStatus.Text = $"已删除本机的 {removed} 条记录。";
    }, LocalStatus);

    /// <summary>
    /// Re-reads what the instance tells this device. It needs a token, and it goes stale the moment the address or
    /// the pairing changes — so it is refreshed on appear and after either of those changes, rather than only when a
    /// refresh button is pressed.
    /// </summary>
    private async Task RefreshInstanceInfoAsync()
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshAsync()
    {
        var paired = await IsPairedAsync();

        PairingState.Text = paired
            ? "此设备已配对，可以上传。"
            : "此设备尚未配对。到管理页生成配对码后填到下面。";

        await RefreshLocalUsageAsync();
    }

    private async Task RefreshLocalUsageAsync()
    {
        var usage = await _controller.GetLocalUsageAsync(CancellationToken.None);

        LocalUsage.Text = usage.Count == 0
            ? "本机还没有记录。"
            : $"本机保存 {usage.Count} 条记录，其中 {usage.VoiceCount} 段录音，录音共 {FormatBytes(usage.AudioBytes)}。";

        ClearLocalButton.IsEnabled = usage.Count > 0;
    }

    private Task<bool> IsPairedAsync() => _tokens.HasTokenAsync(CancellationToken.None);

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };

    private static string DescribeTransportFailure(string? failureCode) => failureCode switch
    {
        "client.not_configured" => "还没有填写服务器地址。",
        "client.not_paired" => "设备还没有配对。",
        "client.network_unreachable" => "连不上服务器，请检查地址与网络。",
        "client.timeout" => "服务器没有及时响应。",
        "auth.device_token_rejected" or "auth.unauthenticated" => "设备令牌已失效，请重新配对。",
        "client.malformed_response" => "服务器返回了读不懂的内容。",
        null => string.Empty,
        _ => "服务器暂时无法完成这个请求，请稍后重试。",
    };

    private async Task GuardAsync(Func<Task> work, Label? statusTarget = null)
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
            // Reported where the user was looking: a failure to clear the archive belongs under 本机记录, not under
            // the server address.
            (statusTarget ?? ServerStatus).Text = "操作没有完成，请稍后重试。";
        }
        finally
        {
            _busy = false;
        }
    }
}
