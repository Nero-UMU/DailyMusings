namespace DailyMusings.Client.Services;

/// <summary>
/// 把上传失败的分类码翻成给人看的一句话。
/// <para>
/// 两个页面都要用它——今日随想页的显式上传、日历详情里的重新上传。原先这份映射是
/// <c>CapturePage</c> 的私有方法，日历要用就得再抄一遍；文案只有一个来源，才不会两边走偏。
/// </para>
/// </summary>
internal static class UploadFailureText
{
    public static string Describe(string? failureCode) => failureCode switch
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
}
