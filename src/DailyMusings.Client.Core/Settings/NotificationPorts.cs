using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Settings;

/// <summary>
/// The notification preferences as the client may see them (docs/开发指导.md §9.3 设置：通知偏好, §12).
/// <para>
/// Read-only on purpose. §9.3 puts notification preferences on the client's settings screen, but <em>writing</em> them
/// from a device token would let a stolen token redirect the instance's mail — and that mail carries the day's date
/// and title. So a paired device may look; the recipient address and the event switches are changed by an
/// administrator (§10.4's "凭据不外流" reasoning applied to where notifications go, not just what they contain).
/// </para>
/// </summary>
public interface INotificationSettingsApiClient
{
    Task<ApiResult<NotificationSettingsDto>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The model names this instance is configured with (docs/开发指导.md §8.1: 客户端只能查看模型名与健康状态).
/// <para>
/// Names and on/off only. The Base URL and the secret's name stay with the administrator: a phone does not need to
/// know where an instance's endpoints or its credentials live, and §8.1 asks for the name specifically.
/// </para>
/// </summary>
public interface IModelNameApiClient
{
    Task<ApiResult<IReadOnlyList<ModelNameDto>>> GetAsync(CancellationToken cancellationToken);
}
