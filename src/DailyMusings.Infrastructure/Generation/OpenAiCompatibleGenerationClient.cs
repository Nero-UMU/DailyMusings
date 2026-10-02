using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Generation;

/// <summary>
/// Turns a day's material into a draft and then checks it, against an OpenAI-compatible chat endpoint
/// (docs/开发指导.md §8.4).
/// <para>
/// Three rules govern this class. It never logs the prompt or the answer — §16 keeps content out of the default
/// log, and both are the user's private writing. It classifies every failure as transient or permanent, because
/// that is what decides whether §14 retries. And it asks the model for structure it can verify rather than for
/// offsets it cannot: citations are quotes, and their positions are computed from the draft text afterwards
/// (decision A.6).
/// </para>
/// <para>
/// No <c>response_format</c> is sent. It is the one part of the "OpenAI-compatible" surface that providers
/// disagree about, and a 400 from an otherwise fine endpoint would turn every generation into a permanent
/// failure. The JSON requirement lives in the prompt instead, and the reader tolerates a fenced or narrated
/// answer.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleGenerationClient : IReflectionGenerationClient, IUnsourcedStatementChecker
{
    private const string ChatPath = "chat/completions";

    /// <summary>
    /// The rules that may not be overridden by the user's own instructions, per §8.4's
    /// "不得添加无来源事实的不可覆盖系统规则".
    /// </summary>
    private const string SystemRules = """
        你是一名中文写作助手，负责根据用户自己记录的素材，写一篇每日随想。

        不可违反的规则：
        1. 只能使用下方提供的素材内容。不得添加任何素材中不存在的事实——人物、时间、地点、数字、因果
           都不许自己补；素材里已经有的感受可以写细一点、可以把几件事之间的语气连起来（这不算添加事实）。
        2. **要写成一篇文章，不是把素材逐条排下来。** 全文必须有一条贯穿的线索——一个感受、一个念头，
           或这一天里最让你停了一下的那一点；所有素材都服务于它。
        3. **段落数与素材条数无关**：几件事可以写在同一段里（合并），一件事也可以分两段（展开）。
           禁止一段对一条素材，也禁止按素材出现的时间顺序逐条铺开——时间顺序只能当辅助，不能当骨架。
        4. 先判断这一天偏"想法"还是偏"日记"，据此决定结构：偏想法就以那个想法为主线，偏日记也要有一条线
           把当天的事串起来，而不是按顺序报流水。
        5. 正文的主体必须是"今天的素材"里的事。当天素材少就把正文写短一点：宁可写得短，也不要用历史素材
           把篇幅填满。历史素材与"近几天的成稿"都不是今天发生的事，只能"提一嘴"：用一句以内的呼应、对比
           或回忆带过（例如"比上个月那家好很多""前几天也写过"），不得单独成段，不得展开叙述它们的经过，
           更不能写成当天发生的事。
        6. 结尾可以收束这一天，但要停在具体的事或感受上；不写金句、不做抽象总结、不替读者拔高，
           也不用"总之""综上所述"这类套话。
        7. 每条"引用"必须逐字摘自你写出的正文，且必须标注它来自哪几条素材。
        8. 正文用空行分段，不要使用 Markdown 标题或列表。
        9. 只输出一个 JSON 对象，不要输出解释文字或代码块。
        10. 主题方面：优先从"已有主题"里挑 1–3 个最贴切的，并原样回抄它们的写法；同义概念不要另造新名。
            只有当已有主题确实都不合适时，才在 newTopics 里提出简短的候选名（每个不超过 12 字）。
            无论如何都不能一个主题都不给：要么给 topics，要么给 newTopics。
        """;

    private const string SchemaHint = """
        {
          "title": "标题，20 字以内",
          "summary": "摘要，60 字以内",
          "body": "正文，用空行分段",
          "tags": ["标签"],
          "categories": ["分类建议"],
          "topics": ["从「已有主题」里原样挑选的主题名，1-3 个"],
          "newTopics": ["已有主题都不合适时提出的新主题名，每个不超过 12 字；没有就留空数组"],
          "citations": [
            {
              "quote": "正文中的一句话，必须与正文逐字一致",
              "sources": ["S1"],
              "relevance": 0.9,
              "reason": "为什么这句话来自这条素材"
            }
          ]
        }
        """;

    private const string CheckSchemaHint = """
        {
          "unsourced": [
            {
              "quote": "正文中无法追溯到任何素材的一句话，必须与正文逐字一致",
              "reason": "为什么无法追溯"
            }
          ]
        }
        """;

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly IGenerationSettingsProvider _settings;
    private readonly ILogger<OpenAiCompatibleGenerationClient> _logger;

    public OpenAiCompatibleGenerationClient(
        HttpClient httpClient,
        ISecretStore secrets,
        IGenerationSettingsProvider settings,
        ILogger<OpenAiCompatibleGenerationClient> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public async Task<GeneratedDraft> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var labels = BuildLabels(request);
        var content = await CompleteAsync(
            BuildGenerationPrompt(request, labels),
            "generation.malformed_response",
            cancellationToken).ConfigureAwait(false);

        var payload = JsonPayloadReader.TryRead<GenerationPayload>(content)
            ?? throw new PermanentExternalFailureException(
                "generation.malformed_response",
                "The generation endpoint returned a response that could not be read.");

        if (string.IsNullOrWhiteSpace(payload.Body))
        {
            throw new PermanentExternalFailureException(
                "generation.empty_body",
                "The generation endpoint returned no body.");
        }

        return new GeneratedDraft(
            payload.Title?.Trim() ?? string.Empty,
            payload.Summary?.Trim() ?? string.Empty,
            payload.Body.Trim(),
            Clean(payload.Tags),
            Clean(payload.Categories),
            BuildCitations(payload.Citations, labels),

            // Missing, mistyped or non-string fields read as "no topics", exactly like tags and categories: a
            // model that forgets the field must not turn a perfectly good draft into a permanent failure.
            Clean(payload.Topics),
            Clean(payload.NewTopics));
    }

    /// <summary>
    /// The second stage of §8.4: look for sentences the draft cannot trace back to the material.
    /// </summary>
    public async Task<IReadOnlyList<UnsourcedFinding>> CheckAsync(
        UnsourcedCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var labels = BuildLabels(request.DayInputs, request.HistoricalMaterial);
        var prompt = BuildCheckPrompt(request, labels);

        var content = await CompleteAsync(prompt, "check.malformed_response", cancellationToken)
            .ConfigureAwait(false);

        var payload = JsonPayloadReader.TryRead<CheckPayload>(content)
            ?? throw new PermanentExternalFailureException(
                "check.malformed_response",
                "The source check returned a response that could not be read.");

        return (payload.Unsourced ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Quote))
            .Select(item => new UnsourcedFinding(
                item.Quote!.Trim(),
                string.IsNullOrWhiteSpace(item.Reason) ? "无法追溯到任何输入" : item.Reason!.Trim()))
            .ToArray();
    }

    /// <summary>
    /// One chat completion, with §14's retry classification applied to whatever comes back.
    /// </summary>
    private async Task<string> CompleteAsync(
        string prompt,
        string malformedCode,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            throw new PermanentExternalFailureException(
                "generation.disabled",
                "No generation endpoint is configured.");
        }

        var apiKey = _secrets.TryGet(settings.SecretName);
        if (apiKey is null)
        {
            throw new PermanentExternalFailureException(
                "generation.secret_missing",
                $"The secret '{settings.SecretName}' is not provisioned.");
        }

        var body = new ChatRequest(
            settings.Model,
            [
                new ChatMessage("system", SystemRules),
                new ChatMessage("user", prompt),
            ]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientExternalFailureException(
                "generation.timeout",
                "The generation endpoint did not answer in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientExternalFailureException(
                "generation.network",
                "The generation endpoint could not be reached.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Only the status code reaches the log; never the prompt or the answer (§16).
                _logger.LogWarning("Generation endpoint answered {StatusCode}.", (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PermanentExternalFailureException(
                    "generation.credentials_rejected",
                    "The generation endpoint rejected the configured credentials.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new TransientExternalFailureException(
                    "generation.upstream_unavailable",
                    "The generation endpoint is temporarily unavailable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PermanentExternalFailureException(
                    "generation.request_rejected",
                    "The generation endpoint rejected the request.");
            }

            ChatResponse payload;
            try
            {
                // ResponseHeadersRead returns as soon as the provider sends headers. The same configured deadline
                // must remain in force while its (possibly streamed) JSON body is read; otherwise a provider can
                // leave the job running forever after satisfying only the header phase.
                payload = await ReadAsync(response, malformedCode, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransientExternalFailureException(
                    "generation.timeout",
                    "The generation endpoint did not answer in time.",
                    exception);
            }

            var content = payload.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new PermanentExternalFailureException(
                    malformedCode,
                    "The generation endpoint returned no content.");
            }

            return content;
        }
    }

    /// <summary>
    /// Reads the envelope, turning a shape we cannot understand into a permanent failure. Retrying the same
    /// request would produce the same unreadable answer, so it must not burn §14's retry budget.
    /// </summary>
    private static async Task<ChatResponse> ReadAsync(
        HttpResponseMessage response,
        string malformedCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<ChatResponse>(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PermanentExternalFailureException(
                    malformedCode,
                    "The generation endpoint returned an empty response.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                malformedCode,
                "The generation endpoint returned a response that could not be read.");
        }
    }

    /// <summary>
    /// Assigns a short label to every piece of material.
    /// <para>
    /// The model cites labels, never identifiers: asking it to echo UUIDs invites invented ones, and the label is
    /// what the prompt can actually refer to. The mapping back to real inputs stays on this side.
    /// </para>
    /// </summary>
    private static Dictionary<string, InputEntryId> BuildLabels(GenerationRequest request) =>
        BuildLabels(request.DayInputs, request.HistoricalMaterial);

    private static Dictionary<string, InputEntryId> BuildLabels(
        IReadOnlyList<InputEntry> dayInputs,
        IReadOnlyList<RetrievedMaterial> historical)
    {
        var labels = new Dictionary<string, InputEntryId>(StringComparer.Ordinal);
        var index = 0;

        foreach (var entry in dayInputs)
        {
            labels[string.Create(CultureInfo.InvariantCulture, $"S{++index}")] = entry.Id;
        }

        foreach (var material in historical)
        {
            // A past entry can also appear in the day's own material; reusing its label keeps the prompt from
            // offering the same text twice under two names.
            if (labels.ContainsValue(material.Entry.Id))
            {
                continue;
            }

            labels[string.Create(CultureInfo.InvariantCulture, $"S{++index}")] = material.Entry.Id;
        }

        return labels;
    }

    private static string BuildGenerationPrompt(
        GenerationRequest request,
        IReadOnlyDictionary<string, InputEntryId> labels)
    {
        var byInput = labels.ToDictionary(pair => pair.Value, pair => pair.Key);
        var builder = new StringBuilder();

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"内容日期：{request.ContentDate}（{WeekdayName(request.ContentDate)}），这就是要写的那一天，正文里称它为「今天」。");
        builder.AppendLine();

        // The writing spec comes before the material on purpose (decision A.24). These are the standing rules for
        // how the article is written, and a model that reads them first composes against them instead of having to
        // retrofit a style onto text it has already planned.
        AppendWritingSettings(builder, request.Settings);

        builder.AppendLine();

        // 每条素材都带上它自己的时间。当天的时间交给模型，它才写得出「凌晨」「晚上」这类判断，也才
        // 排得对先后；历史素材带日期与时间，它才说得出「上个月」而不是编一个时间出来。时间用它被记录时
        // 的时区偏移渲染（§7 记的就是这个偏移），也就是用户当时看到的那一刻。
        builder.AppendLine("今天的素材（正文主体，按时间排序。方括号后是当天的时间）：");
        foreach (var entry in request.DayInputs)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[{byInput[entry.Id]}] ({ClockOf(entry)}) {entry.TranscriptForGeneration}");
        }

        builder.AppendLine();

        if (request.HistoricalMaterial.Count > 0)
        {
            builder.AppendLine("可以引用的历史素材（不是今天发生的事；只能一句带过作呼应或对比，不得成段。方括号后是它发生的日期与时间）：");
            foreach (var material in request.HistoricalMaterial)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"[{byInput[material.Entry.Id]}] ({MaterialStamp(material.Entry)}) {material.Entry.TranscriptForGeneration}");
            }
        }
        else
        {
            builder.AppendLine("没有可引用的历史素材。");
        }

        builder.AppendLine();

        // 往日的成稿（已发布的博客全文）。与历史素材的区别是：历史素材是原始随想，用来呼应具体的事；
        // 成稿是写好的文章，用来接上行文与延续。两者都受第 3 条约束，只能提一嘴。
        if (request.RecentArticles.Count > 0)
        {
            builder.AppendLine("近几天的成稿（已经发表过的全文；只用来看行文与延续，不得成段复述）：");

            foreach (var article in request.RecentArticles)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"({article.ContentDate}) {article.Title}");
                builder.AppendLine(article.Body);
                builder.AppendLine();
            }
        }

        builder.AppendLine();

        // 结构不合格时的重写要求：判定在应用层（ArticleStructurePolicy），这里只负责把话带到。
        if (!string.IsNullOrWhiteSpace(request.StructureCorrection))
        {
            builder.AppendLine("这次必须改写（下面的要求优先于上面的写作规范）：");
            builder.AppendLine(request.StructureCorrection);
            builder.AppendLine();
        }

        // §6.2 as revised: the model picks the day's topics, and it can only pick from a vocabulary it was
        // shown. An instance with no topics yet gets an explicit "there are none", which reads better than an
        // empty list and is what makes the "propose one instead" rule unambiguous.
        if (request.KnownTopics.Count > 0)
        {
            builder.AppendLine("已有主题（优先从这里挑选，回抄写法）：");
            builder.AppendLine(string.Join("、", request.KnownTopics));
        }
        else
        {
            builder.AppendLine("已有主题：暂无（请用 newTopics 提出 1-3 个简短主题名）。");
        }

        builder.AppendLine();
        builder.Append("只输出这样一个 JSON 对象：");
        builder.AppendLine();
        builder.Append(SchemaHint);

        return builder.ToString();
    }

    /// <summary>
    /// 一条素材被记录时的当地时刻，用它自己的时区偏移渲染（§7 记录的正是这个偏移），格式 <c>HH:mm</c>。
    /// 当天素材只用时刻：这一块的整体日期已经由上面的「内容日期」说清了，再写一遍反而啰嗦。
    /// </summary>
    private static string ClockOf(InputEntry entry) =>
        entry.CreatedAtUtc
            .ToOffset(TimeSpan.FromMinutes(entry.CreatedOffsetMinutes))
            .ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 历史素材的日期与时刻，<c>yyyy-MM-dd HH:mm</c>。日期是它属于哪个内容日，时刻是它被记录的那一刻 ——
    /// 模型要说出「上个月」「昨天晚上」这类话，靠的就是这两个值，而不是自己推算。
    /// </summary>
    private static string MaterialStamp(InputEntry entry) =>
        $"{entry.ContentDate} {ClockOf(entry)}";

    private static string WeekdayName(ContentDate contentDate) => contentDate.Value.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    private static string BuildCheckPrompt(
        UnsourcedCheckRequest request,
        IReadOnlyDictionary<string, InputEntryId> labels)
    {
        var byInput = labels.ToDictionary(pair => pair.Value, pair => pair.Key);
        var builder = new StringBuilder();

        builder.AppendLine("下面是一篇已经写好的正文，以及它所依据的全部素材。");
        builder.AppendLine("请找出正文中无法从这些素材得到支持的句子。只报告确实无来源的句子；有来源的句子不要报告。");
        builder.AppendLine();

        builder.AppendLine("素材：");
        foreach (var entry in request.DayInputs)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[{byInput[entry.Id]}] {entry.TranscriptForGeneration}");
        }

        foreach (var material in request.HistoricalMaterial)
        {
            if (!byInput.TryGetValue(material.Entry.Id, out var label))
            {
                continue;
            }

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"[{label}] ({material.Entry.ContentDate}) {material.Entry.TranscriptForGeneration}");
        }

        builder.AppendLine();
        builder.AppendLine("正文：");
        builder.AppendLine(request.Body);
        builder.AppendLine();
        builder.Append("只输出这样一个 JSON 对象：");
        builder.AppendLine();
        builder.Append(CheckSchemaHint);

        return builder.ToString();
    }

    /// <summary>
    /// Writes the user's writing spec (docs/开发指导.md §8.4, decision A.24).
    /// <para>
    /// Length and person are stated as facts about the piece ("about 300 characters", "first person") because
    /// those are the two things a model most reliably honours when they are unambiguous; everything else is the
    /// user's own list, emitted verbatim and in their order. The list can be empty — a user who deleted every
    /// rule gets a prompt with no style block rather than a prompt with invented rules.
    /// </para>
    /// <para>
    /// The person lives here rather than in the system rules on purpose: the system message is the part the user
    /// cannot change, so a person hardcoded there would quietly override the setting they just picked.
    /// </para>
    /// </summary>
    private static void AppendWritingSettings(StringBuilder builder, WritingSettings settings)
    {
        builder.AppendLine("写作规范（本次成文必须遵守；与系统规则冲突时，以系统规则为准）：");

        // 篇幅是区间，不是目标值（附录 A.28）。有公差时明确给出「可以到多少」，让素材多少决定落在哪儿；
        // 没有公差时把范围说死，不给模型自行放宽的余地。
        builder.AppendLine(settings.AllowsTolerance
            ? $"- 篇幅：{settings.MinCharacters} 到 {settings.MaxCharacters} 字之间（正文汉字数，不含 Markdown 标记）。"
              + $"如果当天的素材撑不满或装不下，可以短到 {settings.ToleratedMinCharacters} 字、或长到 "
              + $"{settings.ToleratedMaxCharacters} 字，由素材多少决定；不要为了凑字数而扩写，也不要为了压字数而删掉具体的事。"
            : $"- 篇幅：{settings.MinCharacters} 到 {settings.MaxCharacters} 字之间（正文汉字数，不含 Markdown 标记），"
              + "不要超出这个范围。");

        var person = settings.Person switch
        {
            WritingPerson.Second => "第二人称，用「你」来写。",
            WritingPerson.Third => "第三人称，用「他」「她」或直接叙述，不要出现「我」。",
            _ => "第一人称，用「我」来写。",
        };

        builder.AppendLine(CultureInfo.InvariantCulture, $"- 人称：{person}");

        foreach (var rule in settings.Rules)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {rule.Title.Trim()}：{rule.Instruction.Trim()}");
        }
    }

    private static IReadOnlyList<GeneratedCitation> BuildCitations(
        IReadOnlyList<CitationPayload>? citations,
        IReadOnlyDictionary<string, InputEntryId> labels)
    {
        if (citations is null)
        {
            return [];
        }

        var results = new List<GeneratedCitation>();

        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation.Quote))
            {
                continue;
            }

            var inputs = (citation.Sources ?? [])
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source!.Trim())
                .Where(labels.ContainsKey)
                .Select(source => labels[source])
                .Distinct()
                .ToArray();

            results.Add(new GeneratedCitation(
                citation.Quote!.Trim(),
                inputs,
                citation.Relevance is { } relevance and >= 0 and <= 1 ? relevance : 0.5,
                string.IsNullOrWhiteSpace(citation.Reason) ? "与素材一致" : citation.Reason!.Trim()));
        }

        return results;
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static Uri BuildEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return new Uri($"{baseUrl.TrimEnd('/')}/{ChatPath}", UriKind.Absolute);
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record GenerationPayload(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
        [property: JsonPropertyName("categories")] IReadOnlyList<string>? Categories,
        [property: JsonPropertyName("citations")] IReadOnlyList<CitationPayload>? Citations,
        [property: JsonPropertyName("topics")] IReadOnlyList<string>? Topics,
        [property: JsonPropertyName("newTopics")] IReadOnlyList<string>? NewTopics);

    private sealed record CitationPayload(
        [property: JsonPropertyName("quote")] string? Quote,
        [property: JsonPropertyName("sources")] IReadOnlyList<string?>? Sources,
        [property: JsonPropertyName("relevance")] double? Relevance,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record CheckPayload(
        [property: JsonPropertyName("unsourced")] IReadOnlyList<CheckItemPayload>? Unsourced);

    private sealed record CheckItemPayload(
        [property: JsonPropertyName("quote")] string? Quote,
        [property: JsonPropertyName("reason")] string? Reason);
}
