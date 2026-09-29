// The method names below carry the state names, so the types are aliased: inside this class `ReflectionText`
// sits next to `ReflectionState`, and a bare `ReflectionStatus.Confirmed` would have to fight the member for
// resolution. Aliases also make it obvious that the wire-name overloads take strings, not enums.
using PublicationState = DailyMusings.Domain.Publishing.PublicationStatus;
using ReflectionState = DailyMusings.Domain.Reflections.ReflectionStatus;
using TopicOriginValue = DailyMusings.Domain.Topics.TopicOrigin;
using TranscriptionState = DailyMusings.Domain.Inputs.TranscriptionStatus;

namespace DailyMusings.Admin.Shared;

/// <summary>
/// 状态值到界面文字的唯一映射表。
/// <para>
/// 抽出来是因为同一批状态会出现在好几个分区里（数据管理、内容管理、发布设置、状态页）：同一个状态在两个页面上
/// 写成两个词，会让人以为它们不是同一件事。徽章配色也一起放这里，免得「失败」在一处是红的、在另一处是灰的。
/// </para>
/// <para>
/// 认不出来的取值一律原样显示：把将来新增的状态显示成「失败」，比显示成它自己要糟得多。
/// </para>
/// </summary>
public static class AdminLabels
{
    public static string TranscriptionText(TranscriptionState status) => status switch
    {
        TranscriptionState.Succeeded => "已转写",
        TranscriptionState.Pending => "等待转写",
        TranscriptionState.InProgress => "转写中",
        TranscriptionState.Failed => "转写失败",
        _ => "不需要转写",
    };

    public static string TranscriptionBadge(TranscriptionState status) => status switch
    {
        TranscriptionState.Succeeded => "badge-ok",
        TranscriptionState.Failed => "badge-err",
        TranscriptionState.Pending or TranscriptionState.InProgress => "badge-warn",
        _ => "badge-muted",
    };

    public static string ReflectionText(ReflectionState status) => status switch
    {
        ReflectionState.PendingInputs => "等待素材就绪",
        ReflectionState.Ready => "等待生成",
        ReflectionState.Generating => "生成中",
        ReflectionState.ReviewRequired => "待核验",
        ReflectionState.Confirmed => "已确认",
        ReflectionState.Failed => "生成失败",
        ReflectionState.StaleByLateInput => "已过时（有新素材）",
        _ => status.ToString(),
    };

    public static string ReflectionBadge(ReflectionState status) => status switch
    {
        ReflectionState.Confirmed => "badge-ok",
        ReflectionState.Failed => "badge-err",
        ReflectionState.StaleByLateInput => "badge-warn",
        ReflectionState.Generating or ReflectionState.ReviewRequired => "badge-warn",
        _ => "badge-muted",
    };

    /// <summary>
    /// The same statuses as the statistics reader spells them: domain enum names, or the documented <c>none</c>
    /// for a day that has no draft at all.
    /// </summary>
    public static string ReflectionText(string? wireName) => wireName switch
    {
        null or "none" => "今天还没有稿件",
        "PendingInputs" => "素材不足，未成稿",
        "Ready" => "等待生成",
        "Generating" => "生成中",
        "ReviewRequired" => "待核验",
        "Confirmed" => "已确认",
        "Failed" => "生成失败",
        "StaleByLateInput" => "已过时（有新素材）",
        _ => wireName,
    };

    public static string PublicationText(PublicationState status) => status switch
    {
        PublicationState.Queued => "排队中",
        PublicationState.InProgress => "导出中",
        PublicationState.DraftUploaded => "已导出草稿",
        PublicationState.Published => "已公开发布",
        PublicationState.Failed => "导出失败",
        PublicationState.Expired => "超窗过期",
        PublicationState.Superseded => "已被新版本取代",
        _ => status.ToString(),
    };

    public static string PublicationBadge(PublicationState status) => status switch
    {
        PublicationState.Published or PublicationState.DraftUploaded => "badge-ok",
        PublicationState.Failed => "badge-err",
        PublicationState.Expired or PublicationState.Queued or PublicationState.InProgress => "badge-warn",
        _ => "badge-muted",
    };

    public static string TopicOriginText(TopicOriginValue origin) =>
        origin == TopicOriginValue.Model ? "大模型" : "用户";

    public static string ProcessingFailureText(string? code) => code switch
    {
        "transcription.disabled" => "语音转写尚未启用",
        "transcription.secret_missing" => "语音转写缺少 API Key",
        "transcription.credentials_rejected" => "语音转写的 API Key 无效",
        "transcription.network" => "暂时无法连接转写服务",
        "transcription.timeout" => "转写服务响应超时",
        "transcription.upstream_unavailable" => "转写服务暂时不可用",
        "transcription.request_rejected" => "转写服务拒绝了录音，请检查地址和模型",
        "transcription.public_audio_url_required" => "dashscope_async 需要公网可访问的音频 URL，当前录音只有本地文件",
        "transcription.api_type_invalid" => "语音模型的 API 类型无效，请在模型管理中重新选择",
        "transcription.empty_result" => "模型没有识别出文字",
        "transcription.malformed_response" => "转写服务返回了无法识别的结果",
        null or "" => "处理失败",
        _ => "处理失败，请到系统设置查看日志",
    };

    public static string HealthProbeText(string name) => name switch
    {
        "database.migrations" => "数据库版本",
        "database.writable" => "数据库写入",
        "media.writable" => "录音存储",
        "jobExecutor.alive" => "后台任务",
        _ => name,
    };

    /// <summary>
    /// The refusals the publish use cases return, in the operator's language. Falling back to the server's own
    /// sentence is deliberate: an unmapped refusal is more useful shown than hidden.
    /// </summary>
    public static string PublicationRefusalText(string? code, string? detail) => code switch
    {
        "publication.reflection_unknown" => "这一天还没有稿件。",
        "publication.reflection_not_confirmed" => "只有已确认的稿件才能发布：请先核验并确认这一天的草稿。",
        "publish.target.unknown" => "这个导出目标已经不存在了。",
        "publication.slot_not_due" => "今天的导出时刻还没到。自动导出按时刻执行；手动发布不受时刻限制。",
        "publication.already_finished" => "这个版本已经导出过了。要再写一次，请在目标那一行点「重试」。",
        "publication.already_published" => "这个版本已经公开发布过了。",
        "publication.superseded" => "那次导出已经被更新的版本取代。",
        "publication.overwrite.not_applicable" => "这篇文章已经公开；要改内容，请先确认一个新版本再导出。",
        "publication.pull.not_supported" => "不能把导出文件反向覆盖稿件；需要修改时请直接编辑稿件。",
        _ => string.IsNullOrWhiteSpace(detail) ? "导出请求被拒绝了。" : detail,
    };

    /// <summary>The refusals a manual generation request can come back with.</summary>
    public static string GenerationRefusalText(string? code, string? detail) => code switch
    {
        "reflection.regeneration.date_not_current" => "只能重新生成今天的随想（历史日期不支持重新生成）。",
        "reflection.generation.no_inputs" => "今天还没有可用的素材，没有东西可以写。",
        "reflection.generation.transcription_failures" =>
            "今天有转写失败的录音，默认暂缓生成；要照常生成，请勾上「忽略转写失败的项」。",
        "reflection.generation.in_progress" => "今天正在生成，等它跑完再试。",
        "reflection.generation.already_confirmed" => "今天的稿件已经确认过了；已确认的稿件不会被重新生成覆盖。",
        "reflection.generation.unknown_status" => "这一天的稿件状态不是已知的生成状态。",
        _ => string.IsNullOrWhiteSpace(detail) ? "生成请求被拒绝了。" : detail,
    };
}
