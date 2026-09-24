using System.Globalization;
using DailyMusings.Contracts;

namespace DailyMusings.Client.Core.Reflections;

/// <summary>
/// The decisions the draft screen makes about what a day's draft needs from the user
/// (docs/开发指导.md §6.4, §8.4, §9.3).
/// <para>
/// These are the rules that carry the product's promises — a hand edit is never overwritten without an explicit
/// answer, a sentence the source check could not trace is never confirmed silently, a drifted source is not shown
/// as if it still resolved — so they live here, as pure functions over the API's own DTOs, rather than inside a
/// page where nothing can test them. The page then only has to draw what these say.
/// </para>
/// </summary>
public static class ReflectionReview
{
    /// <summary>Whether confirming this version has to acknowledge the untraced sentences first (§8.4).</summary>
    public static bool ConfirmNeedsAcknowledgement(ReflectionVersionDto? version) =>
        version is { UnsourcedClaims.Count: > 0 };

    /// <summary>
    /// Whether asking for a regeneration has to warn that the working version's hand edits will be lost (§6.4).
    /// The server refuses to rotate without that acceptance, so asking first is not politeness — it is what makes the
    /// user's decision the one that is carried out.
    /// </summary>
    public static bool RegenerateNeedsOverwriteConfirmation(ReflectionDto? reflection) =>
        reflection?.WorkingVersion?.HasManualEdits == true;

    /// <summary>The state of a day, in the words the screen shows (§6.3, decision A.3).</summary>
    public static string DescribeStatus(string? status) => status switch
    {
        ReflectionStatusNames.PendingInputs => "等待今天的输入",
        ReflectionStatusNames.Ready => "草稿已生成，待确认",
        ReflectionStatusNames.Generating => "正在生成…",
        ReflectionStatusNames.ReviewRequired => "草稿已生成，有待核验的内容",
        ReflectionStatusNames.Confirmed => "已确认",
        ReflectionStatusNames.Failed => "生成失败",
        ReflectionStatusNames.StaleByLateInput => "有新的输入，草稿需要重新生成",
        _ => "未知状态",
    };

    /// <summary>Why this day's draft exists — scheduled, a catch-up, new material, or the user asking (§6.3).</summary>
    public static string DescribeGenerationReason(string? reason) => reason switch
    {
        GenerationReasonNames.Scheduled => "按当天时刻自动生成",
        GenerationReasonNames.Backfill => "补生成（当天没有跑）",
        GenerationReasonNames.LateInputRegeneration => "晚到的输入触发的重新生成",
        GenerationReasonNames.Manual => "手动生成",
        _ => "未知来源",
    };

    /// <summary>How a version is named, given where it sits in the three slots (§6.4).</summary>
    public static string DescribeVersionSlot(ReflectionDto reflection, string versionId)
    {
        if (versionId == reflection.WorkingVersionId) return "当前版本";
        if (versionId == reflection.ConfirmedVersionId) return "已确认版本";
        if (versionId == reflection.PreviousVersionId) return "上一版";
        return versionId == reflection.InitialVersionId ? "初稿" : "某一版";
    }

    /// <summary>
    /// The things the screen must warn about before the user acts. Each one is a state the guide calls out; an empty
    /// list means the draft needs nothing more than a read.
    /// </summary>
    public static IReadOnlyList<string> DescribeWarnings(ReflectionDto? reflection)
    {
        if (reflection is null)
        {
            return [];
        }

        var warnings = new List<string>();
        var working = reflection.WorkingVersion;

        if (reflection.Status == ReflectionStatusNames.StaleByLateInput)
        {
            warnings.Add(reflection.LastStaleReason is { Length: > 0 } reason
                ? $"今天又有新的输入，草稿需要重新生成（{reason}）。"
                : "今天又有新的输入，草稿需要重新生成。");
        }

        if (reflection.Status == ReflectionStatusNames.Failed)
        {
            warnings.Add("上一次生成失败了，可以再试一次。");
        }

        if (working?.HasManualEdits == true)
        {
            warnings.Add("当前版本有你手工修改的内容；重新生成会覆盖它，需要你确认。");
        }

        if (working is { SourcesCheckedAtUtc: null })
        {
            warnings.Add("这一版的来源检查还没完成，暂时看不到存疑语句。");
        }

        var drifted = working?.Sources.Count(source => source.Drift != SourceDriftNames.Exact) ?? 0;
        if (drifted > 0)
        {
            warnings.Add($"有 {drifted} 处引用因为正文被改动而不再精确对得上，源句会按段落显示。");
        }

        if (reflection.ConfirmedVersionIsNotWorking)
        {
            warnings.Add("已确认的版本不是当前版本：发布将使用已确认的那一版。");
        }

        if (reflection.SemanticSearch is { Enabled: true, Available: false })
        {
            warnings.Add("语义检索重建中：历史检索暂时退化为主题与全文匹配。");
        }

        return warnings;
    }

    /// <summary>
    /// The quoted text a source reference points at, sliced out of the version's own body.
    /// <para>
    /// The server sends offsets, not text (decision A.6): the positions are computed against the body the model
    /// produced, so the screen can show exactly the sentence the citation covers — and can show that an edited
    /// paragraph no longer contains it, which is what <c>drifted</c> means.
    /// </para>
    /// </summary>
    public static string SliceQuote(string? body, int blockIndex, int charStart, int charEnd)
    {
        var blocks = SplitBlocks(body);

        if (blockIndex < 0 || blockIndex >= blocks.Count)
        {
            return string.Empty;
        }

        var block = blocks[blockIndex];
        var start = Math.Clamp(charStart, 0, block.Length);
        var end = Math.Clamp(charEnd, start, block.Length);

        return block[start..end].Trim();
    }

    /// <summary>The paragraph a claim sits in, so a drifted or unresolved offset still shows something readable.</summary>
    public static string BlockAt(string? body, int blockIndex)
    {
        var blocks = SplitBlocks(body);
        return blockIndex >= 0 && blockIndex < blocks.Count ? blocks[blockIndex].Trim() : string.Empty;
    }

    /// <summary>The same blank-line split the server uses, so indices mean the same thing on both sides.</summary>
    public static IReadOnlyList<string> SplitBlocks(string? body) =>
        string.IsNullOrWhiteSpace(body)
            ? []
            : body
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split("\n\n", StringSplitOptions.None)
                .Select(block => block.Trim('\n'))
                .ToArray();

    /// <summary>Where a citation came from, in words: today's material or an earlier day (§8.3).</summary>
    public static string DescribeSourceOrigin(SourceReferenceDto source) =>
        source.IsHistorical ? "来自更早的记录" : "来自今天";

    /// <summary>How a publication reads on the draft screen (§11.1).</summary>
    public static string DescribePublication(PublicationDto publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var state = publication.Status switch
        {
            PublicationStatusNames.Queued => "排队中",
            PublicationStatusNames.InProgress => "正在导出",

            // One target kind remains, so both finished states say the same thing: a file was written. Whether
            // that file is a published post or still a draft is the visibility half of the line below, and it is
            // also exactly what the front matter's `draft` flag says.
            PublicationStatusNames.DraftUploaded or PublicationStatusNames.Published => "已写入 Markdown 文件",

            PublicationStatusNames.Expired => "已超过执行窗口，未执行",
            PublicationStatusNames.Superseded => "已被新的发布取代",
            _ => $"失败（{publication.ErrorCode ?? "未知原因"}）",
        };

        var origin = publication.Trigger == PublicationTriggerNames.Automatic ? "自动" : "手动";
        var visibility = publication.RequestedVisibility == PublicationVisibilityNames.Public ? "公开" : "草稿";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{publication.TargetName} · {origin}{visibility} · {state}");
    }

    /// <summary>
    /// What to tell the user after a publish request: whether it was queued, and if not, why in their words. The
    /// codes are the server's own (they are instructions to the user, not internal errors), so an unknown one is
    /// shown verbatim rather than guessed at.
    /// </summary>
    public static string DescribePublishRefusal(string? code) => code switch
    {
        "publication.reflection_not_confirmed" => "这一天还没确认，确认后才能发布。",
        "publish.target.unknown" or "publication.target_missing" => "这个发布目标已经不存在了。",
        "publication.reflection_unknown" => "服务器上还没有这一天的草稿。",
        "publication.version_missing" => "这一天没有可发布的版本。",
        "publication.overwrite.not_applicable" => "远端已经是公开状态，不需要再发布一次。",
        "publication.already_published" => "这一版已经发布过了。",
        "publication.already_finished" => "这次发布已经结束了。",
        "publication.window_expired" => "已超过执行窗口，自动发布不会再执行；可以手动发布。",
        "publication.slot_not_due" => "还没到发布时刻。",
        "publication.superseded" => "这次发布已被新的发布取代。",
        "auth.forbidden" => "设备令牌没有发布权限，请重新配对。",
        "reflection.unconfirmed" => "这一天还没确认，确认后才能发布。",
        null => "服务器拒绝了这次发布。",
        _ => $"服务器拒绝了这次发布（{code}）。",
    };

    /// <summary>What to tell the user when a generation request was refused instead of queued (§7, §6.4).</summary>
    public static string DescribeGenerationRefusal(string? code) => code switch
    {
        "reflection.regeneration.date_not_current" => "只能重新生成今天的随想，历史日期不允许。",
        "reflection.generation.no_inputs" => "今天还没有任何输入，先记下一点再生成。",
        "reflection.generation.transcription_failures" => "有输入的转写失败了；确认忽略失败项后才能生成。",
        "reflection.generation.already_confirmed" => "这一天已经确认，不能重新生成。",
        "reflection.generation.slot_not_due" => "今天还没有到生成时刻。",
        "reflection.generation.in_progress" => "这一天正在生成中。",
        "reflection.generation.awaiting_review" => "草稿已经生成好了，先去核验它。",
        "reflection.generation.previously_failed" => "上一次生成失败了，可以直接再试一次。",
        "reflection.generation.blocked" => "当前状态不允许生成。",
        "reflection.generation.unknown_status" => "这一天的草稿状态不属于正常流程，请检查实例日志。",
        "reflection.regeneration.overwrites_manual_edits" => "这次重新生成会覆盖你的手工修改，需要先明确确认。",
        "reflection.unknown" => "服务器上还没有这一天的草稿。",
        null => "服务器拒绝了这次生成。",
        _ => $"服务器拒绝了这次生成（{code}）。",
    };

    /// <summary>What to tell the user when a confirmation was refused (§8.4).</summary>
    public static string DescribeConfirmRefusal(string? code) => code switch
    {
        "reflection.confirm.unsourced_claims_not_acknowledged" => "草稿里还有无法追溯的句子，需要先确认接受它们。",
        "reflection.confirm.no_version" => "这一天还没有可确认的版本。",
        "reflection.confirm.not_working_version" => "只有当前版本可以确认；先切换到你想要的那一版。",
        "reflection.status.illegal_transition" => "这一天的草稿状态不允许确认。",
        "reflection.unknown" => "服务器上还没有这一天的草稿。",
        null => "服务器拒绝了这次确认。",
        _ => $"服务器拒绝了这次确认（{code}）。",
    };

    /// <summary>
    /// Whether the server said "there is no draft for that day", which is the ordinary state of a day nobody has
    /// generated yet — not a failure.
    /// <para>
    /// Found on a phone: the day had no draft, the server answered 404 with its own code (<c>request.not_found</c>),
    /// and the screen reported it as "连不上服务器" because it only recognised a code the server never sends. The
    /// first thing a user sees on a fresh day was a lie about the network.
    /// </para>
    /// </summary>
    public static bool IsMissingDraft(string? failureCode) => failureCode switch
    {
        "request.not_found" => true,
        "server.rejected.404" => true,
        "reflection.unknown" => true,
        _ => false,
    };

    /// <summary>The failure text for a transport-shaped failure, shared by every action on the screen.</summary>
    public static string DescribeTransportFailure(string? code) => code switch
    {
        "request.not_found" or "server.rejected.404" or "reflection.unknown" => "服务器上还没有这一天的草稿。",
        "client.not_configured" => "还没有配置服务器地址，请到设置页填写。",
        "client.not_paired" => "设备还没有配对，请到设置页输入配对码。",
        "client.token_unavailable" => "读不到设备令牌，请到设置页重新配对。",
        "auth.device_token_rejected" => "设备令牌已失效，请到设置页重新配对。",
        "client.timeout" => "服务器响应超时，稍后再试。",
        "client.malformed_response" => "服务器返回了看不懂的内容。",
        "client.empty_response" => "服务器返回了空内容。",
        "server.rejected.403" => "这台设备没有权限做这件事，请用管理员身份操作。",
        "server.rejected.500" => "服务器内部出错了，请查看实例日志。",
        _ => "连不上服务器，稍后再试。",
    };
}
