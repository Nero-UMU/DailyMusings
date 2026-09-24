using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Settings;

/// <summary>
/// The model names this instance is configured with (docs/开发指导.md §8.1: 客户端只能查看模型名与健康状态).
/// <para>
/// Names and on/off only. The Base URL and the secret's name stay with the administrator: a phone does not need to
/// know where an instance's endpoints or its credentials live, and §8.1 asks for the name specifically. This is the
/// only thing the settings screen still reads from the instance — notification preferences left the phone with the
/// reviewed feature set, and the server's audio is not read either, because playback is of the local recording.
/// </para>
/// </summary>
public interface IModelNameApiClient
{
    Task<ApiResult<IReadOnlyList<ModelNameDto>>> GetAsync(CancellationToken cancellationToken);
}
