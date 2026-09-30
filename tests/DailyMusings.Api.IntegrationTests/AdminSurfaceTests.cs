using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DailyMusings.Contracts;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The administrative surface added with the topic, mail, retention, statistics and log work
/// (docs/开发指导.md §6.2, §12, §13, §15.1, §16).
/// <para>
/// Over real HTTP, because most of what is being asserted here is an API contract: which fields a topic carries,
/// which status a refusal uses, who is allowed to see the instance's counters, and — the one that matters most —
/// that a credential an operator types never comes back out.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class AdminSurfaceTests
{
    private static Dictionary<string, string?> GenerationEnabled(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["Generation:Enabled"] = "true",
        ["Generation:BaseUrl"] = baseUrl,
        ["Generation:Model"] = "test-writer",
        ["Generation:SecretName"] = "openai-api-key",
        ["Generation:TimeoutSeconds"] = "30",
        ["Scheduler:IntervalSeconds"] = "1",
    };

    [TestMethod]
    public async Task Pairing_code_button_is_enabled_while_the_device_page_is_idle()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/devices");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var button = Regex.Match(
            html,
            "<button[^>]*class=\"btn btn-primary\"[^>]*>",
            RegexOptions.CultureInvariant);

        Assert.IsTrue(button.Success, $"配对码按钮没有渲染：\n{html}");
        Assert.IsFalse(
            button.Value.Contains("disabled", StringComparison.OrdinalIgnoreCase),
            $"页面空闲时配对码按钮不应被禁用，实际标签是：{button.Value}");
    }

    /// <summary>
    /// 设备页的「复制」按钮靠 Admin 项目里的 <c>admin-copy.js</c>；脚本没被引用（或没进产物）时，按钮会直接
    /// 报错。这条只守「页面引用了它」——「文件确实进了产物」由 deploy/Dockerfile 的断言负责，那是这类缺陷
    /// 在这个项目里的既定拦截位置（`_framework/blazor.web.js` 那次就是这么修的）。
    /// </summary>
    [TestMethod]
    public async Task The_devices_page_loads_the_clipboard_helper()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/devices");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.IsTrue(
            html.Contains("_content/DailyMusings.Admin/admin-copy.js", StringComparison.Ordinal),
            $"设备页没有引用剪贴板脚本，明文 HTTP 下「复制配对码」会必然失败：\n{html}");
    }

    /// <summary>
    /// Under a deployment that owns the port, the system page must not offer the edit at all: a form that saves a
    /// value the published mapping cannot follow is a button whose only outcome is an instance nobody can reach.
    /// The section stays — it becomes a statement of where the port is decided.
    /// </summary>
    [TestMethod]
    public async Task The_system_page_does_not_offer_a_port_edit_when_the_deployment_owns_it()
    {
        var variable = RuntimeOverridesFile.ListeningPortLockVariableName;
        var previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "1");

            await using var instance = await TestInstance.StartAsync();
            await instance.SignInAsChangedAdministratorAsync();

            using var response = await instance.Client.GetAsync("/system");
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET /system failed:\n{html}");
            StringAssert.Contains(html, "监听端口", "这一节仍然要在，只是改成只读的说明。");
            StringAssert.Contains(html, "端口由谁决定", "锁定时应当说明端口由部署决定。");
            StringAssert.Contains(html, "DM_PORT", "并且要写清改端口的那一处在部署文件里。");
            Assert.IsFalse(
                html.Contains("保存端口", StringComparison.Ordinal),
                "锁定时不应再渲染保存按钮。");
            Assert.IsFalse(
                html.Contains("清除覆盖", StringComparison.Ordinal),
                "锁定时不应再渲染清除按钮。");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [TestMethod]
    public async Task Development_host_passes_dependency_scope_validation()
    {
        await using var instance = await TestInstance.StartAsync(environmentName: "Development");

        using var health = await instance.Client.GetAsync(ApiRoutes.Health);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task Model_settings_ask_for_the_key_value_without_exposing_a_secret_name_field()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/models");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "API Key", "模型设置页应当直接要求用户填写 Key。");
        Assert.IsFalse(
            html.Contains("Secret 名", StringComparison.Ordinal),
            "Secret 的内部存储名不应暴露成用户输入项。");
    }

    /// <summary>
    /// 2026-09-30 用户报的两个缺陷：语音转写卡片的**启用开关**与**清除在管理页保存的 API Key**都不渲染。
    /// 原因相同——页面对 transcription 特殊处理（保存时强制 enabled=true、渲染时跳过这两个控件），后果是
    /// 转写**关不掉、密钥也清不掉**。这条测试直接数页面上的勾选框：三个模型卡片各有一个启用开关，
    /// 而「清除」只在密钥来自管理页时出现。
    /// </summary>
    [TestMethod]
    public async Task Every_model_card_offers_its_enable_switch_and_the_clear_key_option()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var before = await instance.Client.GetAsync("/models");
        var beforeHtml = await before.Content.ReadAsStringAsync();

        Assert.AreEqual(
            3,
            Occurrences(beforeHtml, "type=\"checkbox\""),
            "三个模型卡片（生成 / 转写 / Embedding）都要有启用开关。");

        // 通过页面这条路保存一个转写密钥：密钥来源变成「管理页」，清除控件才有意义。
        using var saved = await instance.Client.PatchAsJsonAsync(
            "/api/system/model-endpoints/transcription",
            new UpdateModelEndpointRequest(
                Enabled: false,
                BaseUrl: "http://127.0.0.1:9/v1",
                Model: "stub-whisper",
                SecretName: null,
                TimeoutSeconds: null,
                Dimensions: null,
                ApiKey: "sk-not-a-real-key",
                ClearPassword: null,
                ApiType: "openai_transcription"));

        saved.EnsureSuccessStatusCode();

        using var after = await instance.Client.GetAsync("/models");
        var html = WebUtility.HtmlDecode(await after.Content.ReadAsStringAsync());

        StringAssert.Contains(html, "清除在管理页保存的 API Key", "保存过密钥之后要能把它清掉。");
        Assert.AreEqual(
            4,
            Occurrences(html, "type=\"checkbox\""),
            "三个启用开关 + 一个清除控件。");

        // 关掉转写之后，页面必须如实说它停用了——这正是「保存时强制 true」掩盖掉的事实。
        Assert.IsTrue(
            html.Contains("已停用", StringComparison.Ordinal),
            "转写被显式关停时，卡片应当显示「已停用」。");
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [TestMethod]
    public async Task A_topic_carries_its_origin_and_its_usage_and_can_be_deleted()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var created = await instance.Client.PostAsJsonAsync("/api/topics", new CreateTopicRequest("晨跑"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var topic = await created.Content.ReadFromJsonAsync<TopicDto>();

        Assert.IsNotNull(topic);
        Assert.AreEqual(TopicOriginNames.User, topic.Origin, "A topic a person typed is marked as theirs.");
        Assert.AreEqual(0, topic.ArticleCount);
        Assert.AreEqual(0, topic.InputCount);

        // Creating by an equivalent name is the same topic, not a second one.
        using var again = await instance.Client.PostAsJsonAsync("/api/topics", new CreateTopicRequest("晨 跑"));
        again.EnsureSuccessStatusCode();
        Assert.AreEqual(topic.Id, (await again.Content.ReadFromJsonAsync<TopicDto>())!.Id);

        using var listed = await instance.Client.GetAsync("/api/topics");
        var list = await listed.Content.ReadFromJsonAsync<TopicListResponse>();

        Assert.AreEqual(1, list!.Items.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(list.Items[0].Origin), "The list has to say where a name came from.");

        using var usage = await instance.Client.GetAsync($"/api/topics/{topic.Id}/usage");
        usage.EnsureSuccessStatusCode();
        var used = await usage.Content.ReadFromJsonAsync<TopicUsageDto>();

        Assert.AreEqual(topic.Id, used!.TopicId);
        Assert.AreEqual(0, used.Articles.Count);
        Assert.AreEqual(0, used.InputCount);

        // Deleting is an administrator action, and the device token must not be able to retire the vocabulary.
        using var fromDevice = await device.DeleteAsync($"/api/topics/{topic.Id}");
        Assert.IsTrue(
            fromDevice.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device must not delete topics (got {fromDevice.StatusCode}).");

        using var deleted = await instance.Client.DeleteAsync($"/api/topics/{topic.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        using var unknown = await instance.Client.DeleteAsync($"/api/topics/{Guid.CreateVersion7()}");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);

        using var unknownUsage = await instance.Client.GetAsync($"/api/topics/{Guid.CreateVersion7()}/usage");
        Assert.AreEqual(HttpStatusCode.NotFound, unknownUsage.StatusCode);
    }

    /// <summary>
    /// The deletion rule end to end: the model names the article's topic, that topic is then in use, deletion is
    /// refused with instructions, and moving the article frees it (§6.2).
    /// </summary>
    [TestMethod]
    public async Task A_topic_an_article_is_about_is_refused_until_the_article_is_moved()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        model.State.Topics = ["巷子"];

        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));
        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天走了一条没走过的巷子。", DateTimeOffset.UtcNow.ToString("o"), 480, "capture-1"));

        captured.EnsureSuccessStatusCode();
        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();
        var today = ingested!.Input.ContentDate;

        using var requested = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{today}/generate",
            new GenerateReflectionRequest(IgnoreTranscriptionFailures: false, AllowOverwriteOfManualEdits: false));

        requested.EnsureSuccessStatusCode();

        var draft = await WaitForDraftAsync(instance.Client, today);

        Assert.AreEqual(1, draft.WorkingVersion!.Topics.Count, "The model named one topic and it must be stored.");
        Assert.AreEqual("巷子", draft.WorkingVersion.Topics[0].Name);
        Assert.IsTrue(draft.WorkingVersion.Topics[0].IsPrimary);
        Assert.IsFalse(string.IsNullOrEmpty(draft.WorkingVersion.Topics[0].Id));

        using (var contentPage = await instance.Client.GetAsync("/content"))
        {
            var html = WebUtility.HtmlDecode(await contentPage.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.OK, contentPage.StatusCode);
            StringAssert.Contains(html, "今天的记录", "内容列表应显示工作版本的真实标题，而不是永远显示无标题。");
            StringAssert.Contains(html, "巷子", "内容列表应显示工作版本的主题，而不是永远显示没有主题。");
        }

        var usedTopicId = draft.WorkingVersion.Topics[0].Id;

        using var refused = await instance.Client.DeleteAsync($"/api/topics/{usedTopicId}");
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);

        var failure = await refused.Content.ReadFromJsonAsync<ApiError>();
        Assert.AreEqual("topic.in_use", failure!.Code);
        StringAssert.Contains(failure.Message, "内容管理");

        using var usage = await instance.Client.GetAsync($"/api/topics/{usedTopicId}/usage");
        var inUse = await usage.Content.ReadFromJsonAsync<TopicUsageDto>();

        Assert.AreEqual(1, inUse!.Articles.Count);
        Assert.AreEqual(today, inUse.Articles[0].ContentDate);
        Assert.AreEqual("今天的记录", inUse.Articles[0].Title);

        // The migration the refusal points at: re-file the working version, then the topic is free.
        using var replacement = await instance.Client.PostAsJsonAsync("/api/topics", new CreateTopicRequest("夜路"));
        var target = await replacement.Content.ReadFromJsonAsync<TopicDto>();

        using var moved = await instance.Client.PatchAsJsonAsync(
            $"/api/reflections/{today}/topics",
            new AssignReflectionTopicsRequest(target!.Id, []));

        moved.EnsureSuccessStatusCode();
        var refreshed = await moved.Content.ReadFromJsonAsync<ReflectionDto>();

        Assert.AreEqual(target.Id, refreshed!.WorkingVersion!.Topics.Single().Id);

        using var freed = await instance.Client.DeleteAsync($"/api/topics/{usedTopicId}");
        Assert.AreEqual(HttpStatusCode.NoContent, freed.StatusCode, "An unused topic has to be deletable.");

        // The topic the article moved to is now the one in use, so it stays.
        using var stillUsed = await instance.Client.DeleteAsync($"/api/topics/{target.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, stillUsed.StatusCode);
    }

    [TestMethod]
    public async Task Inputs_are_paged_newest_first_and_the_totals_add_up()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        for (var index = 1; index <= 3; index++)
        {
            using var captured = await device.PostAsJsonAsync(
                "/api/inputs/text",
                new TextInputRequest(
                    $"第 {index} 条。",
                    new DateTimeOffset(2026, 3, 1, 10 + index, 0, 0, TimeSpan.Zero).ToString("o"),
                    480,
                    $"capture-{index}"));

            captured.EnsureSuccessStatusCode();
        }

        var first = await instance.Client.GetFromJsonAsync<InputListResponse>("/api/inputs?page=1&pageSize=2");

        Assert.IsNotNull(first);
        Assert.AreEqual(2, first.Items.Count);
        Assert.AreEqual(3, first.Total);
        Assert.AreEqual(1, first.Page);
        Assert.AreEqual(2, first.PageSize);

        // Newest first: the third capture leads.
        Assert.AreEqual("第 3 条。", first.Items[0].Transcript);

        var second = await instance.Client.GetFromJsonAsync<InputListResponse>("/api/inputs?page=2&pageSize=2");

        Assert.AreEqual(1, second!.Items.Count);
        Assert.AreEqual(3, second.Total);

        // A day query keeps its old shape: everything for that day, in capture order, whatever the paging says.
        var day = await instance.Client.GetFromJsonAsync<InputListResponse>("/api/inputs?date=2026-03-01");

        Assert.AreEqual(3, day!.Items.Count);
        Assert.AreEqual("第 1 条。", day.Items[0].Transcript);

        // The `limit` parameter still means what it always did for the undated list.
        var limited = await instance.Client.GetFromJsonAsync<InputListResponse>("/api/inputs?limit=1");

        Assert.AreEqual(1, limited!.Items.Count);
    }

    [TestMethod]
    public async Task Statistics_and_logs_are_for_the_administrator_and_say_something_useful()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using (var captured = await device.PostAsJsonAsync(
                   "/api/inputs/text",
                   new TextInputRequest("今天记了一句。", DateTimeOffset.UtcNow.ToString("o"), 480, "capture-1")))
        {
            captured.EnsureSuccessStatusCode();
        }

        using var created = await instance.Client.PostAsJsonAsync("/api/topics", new CreateTopicRequest("记录"));
        created.EnsureSuccessStatusCode();

        var statistics = await instance.Client.GetFromJsonAsync<StatisticsResponse>("/api/system/statistics");

        Assert.IsNotNull(statistics);
        Assert.AreEqual(1, statistics.TotalInputCount);
        Assert.AreEqual(1, statistics.TodayTextCount);
        Assert.AreEqual(0, statistics.TodayVoiceCount);
        Assert.AreEqual(1, statistics.TopicCount);
        // Nothing has been generated for today, and the wire value for that is the documented "none" rather
        // than an empty string: it is a state the page renders.
        Assert.AreEqual("none", statistics.TodayReflectionStatus);
        Assert.AreEqual(-1, statistics.ContentRetentionDays, "Content retention is off unless the user asks for it.");
        Assert.IsFalse(statistics.SmtpConfigured);
        Assert.IsTrue(statistics.DatabaseBytes > 0, "The database file exists and has a size.");
        // A device token is a credential for capturing thoughts, not for reading the instance's numbers.
        using (var forbidden = await device.GetAsync("/api/system/statistics"))
        {
            Assert.IsTrue(
                forbidden.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"A device must not read the instance statistics (got {forbidden.StatusCode}).");
        }

        var logs = await instance.Client.GetFromJsonAsync<LogResponse>("/api/system/logs?lines=20");

        Assert.IsNotNull(logs);
        Assert.IsFalse(string.IsNullOrWhiteSpace(logs.MinimumLevel), "The page has to say what it is showing.");
        Assert.IsNotNull(logs.Items);

        using (var forbidden = await device.GetAsync("/api/system/logs"))
        {
            Assert.IsTrue(
                forbidden.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"A device must not read the instance log (got {forbidden.StatusCode}).");
        }

        // The buffer is bounded, so an enormous request is answered rather than honoured.
        var bounded = await instance.Client.GetFromJsonAsync<LogResponse>("/api/system/logs?lines=100000");

        Assert.IsTrue(bounded!.Items.Count <= 2000);
    }

    /// <summary>
    /// §12/§10.4: a password typed into the page is stored, reported as coming from there, and never echoed back
    /// — and a test mail with nowhere to go is a refusal, not an error.
    /// </summary>
    [TestMethod]
    public async Task The_smtp_password_can_be_set_from_the_page_and_never_comes_back_out()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        // Nothing is configured yet, so a test mail has nowhere to go — a refusal the page can explain, not a
        // failure and certainly not an exception.
        using (var nowhere = await instance.Client.PostAsJsonAsync("/api/system/smtp-settings/test", new SmtpTestRequest(null)))
        {
            nowhere.EnsureSuccessStatusCode();
            var refusal = await nowhere.Content.ReadFromJsonAsync<SmtpTestResponse>();

            Assert.IsFalse(refusal!.Sent);
            Assert.AreEqual("smtp.test.no_recipient", refusal.Code);
            Assert.IsNotNull(refusal.Detail);
        }

        const string Password = "typed-in-the-admin-page";

        using var stored = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(
                Enabled: true,

                // A closed port on the loopback interface: the send has to fail, and it has to fail without
                // reaching anybody's network — this is what makes the assertion below deterministic and offline.
                Host: "127.0.0.1",
                Port: 65000,
                FromAddress: "noreply@example.com",

                // A password may not travel over an unencrypted connection, which is why the test mail below is
                // configured for STARTTLS. This relay will not answer at all, so nothing is ever really negotiated.
                UseSsl: false,
                UseStartTls: true,
                Password: Password,
                ToAddress: "reader@example.com"));

        stored.EnsureSuccessStatusCode();

        var raw = await stored.Content.ReadAsStringAsync();
        Assert.IsFalse(raw.Contains(Password, StringComparison.Ordinal), "The password must never be in a response.");

        var dto = System.Text.Json.JsonSerializer.Deserialize<SmtpSettingsDto>(
            raw,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsNotNull(dto);
        Assert.AreEqual(SecretSourceNames.Ui, dto.PasswordSource);
        Assert.IsTrue(dto.HasPassword);
        Assert.AreEqual("reader@example.com", dto.ToAddress);
        Assert.AreEqual("noreply@example.com", dto.FromAddress);

        // And the read path agrees: the form shows where the effective password would come from.
        var read = await instance.Client.GetFromJsonAsync<SmtpSettingsDto>("/api/system/smtp-settings");

        Assert.AreEqual(SecretSourceNames.Ui, read!.PasswordSource);
        Assert.AreEqual("reader@example.com", read.ToAddress);

        // A relay that refuses the connection is reported as a result of the test: a code and a sentence, not a
        // 500 the operator cannot act on.
        using (var unreachable = await instance.Client.PostAsJsonAsync(
                   "/api/system/smtp-settings/test",
                   new SmtpTestRequest(null)))
        {
            unreachable.EnsureSuccessStatusCode();
            var outcome = await unreachable.Content.ReadFromJsonAsync<SmtpTestResponse>();

            Assert.IsFalse(outcome!.Sent);
            Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Code), "A failed test has to name what went wrong.");
            Assert.IsFalse(
                outcome.Detail?.Contains(Password, StringComparison.Ordinal) ?? false,
                "The password must not travel back inside an error message either.");
        }

        // Clearing it hands the name back to whatever the deployment provides.
        using var cleared = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, null, null, null, null, ClearPassword: true));

        cleared.EnsureSuccessStatusCode();
        Assert.AreEqual(SecretSourceNames.None, (await cleared.Content.ReadFromJsonAsync<SmtpSettingsDto>())!.PasswordSource);
    }

    [TestMethod]
    public async Task The_content_window_is_configurable_and_the_sweep_can_be_run_by_hand()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var settings = await instance.Client.GetFromJsonAsync<ContentSettingsDto>("/api/content-settings");
        Assert.AreEqual(-1, settings!.ContentRetentionDays);

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(null, null, null, null, null, ContentRetentionDays: 30));

        updated.EnsureSuccessStatusCode();
        Assert.AreEqual(30, (await updated.Content.ReadFromJsonAsync<ContentSettingsDto>())!.ContentRetentionDays);

        // A window shorter than "forever" is a typo, not a preference.
        using var invalid = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(null, null, null, null, null, ContentRetentionDays: -5));

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual("content.retention.invalid", (await invalid.Content.ReadFromJsonAsync<ApiError>())!.Code);

        using var swept = await instance.Client.PostAsync("/api/maintenance/content-cleanup/run", null);
        swept.EnsureSuccessStatusCode();

        var job = await swept.Content.ReadFromJsonAsync<MaintenanceRunResponse>();
        Assert.AreEqual("ContentCleanup", job!.JobType);

        var statistics = await instance.Client.GetFromJsonAsync<StatisticsResponse>("/api/system/statistics");
        Assert.AreEqual(30, statistics!.ContentRetentionDays);
    }

    /// <summary>
    /// 写作规范经 API 往返（docs/开发指导.md §8.4，附录 A.24）。
    /// <para>
    /// 关键在于「删空」与「从未配置」是两件事：前者是用户在界面上按下删除的结果，必须在读回来时保持为空，
    /// 否则那个删除按钮等于没有用。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task The_writing_spec_round_trips_and_an_emptied_rule_list_stays_empty()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var initial = await instance.Client.GetFromJsonAsync<ContentSettingsDto>("/api/content-settings");

        Assert.AreEqual(50, initial!.WritingMinCharacters, "默认最少 50 字。");
        Assert.AreEqual(300, initial.WritingMaxCharacters, "默认最多 300 字。");
        Assert.AreEqual(20, initial.WritingTolerance, "默认公差 20 字。");
        Assert.AreEqual("first", initial.WritingPerson, "默认第一人称。");
        Assert.IsTrue(initial.WritingRules.Count > 0, "全新实例应当带一份可改的默认规范清单。");

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(
                null, null, null, null, null,
                WritingMinCharacters: 120,
                WritingMaxCharacters: 450,
                WritingTolerance: 30,
                WritingPerson: "third",
                WritingRules: [new WritingRuleDto("我的风格", "短句为主，不写总结。")]));

        updated.EnsureSuccessStatusCode();

        var saved = (await updated.Content.ReadFromJsonAsync<ContentSettingsDto>())!;
        Assert.AreEqual(120, saved.WritingMinCharacters);
        Assert.AreEqual(450, saved.WritingMaxCharacters);
        Assert.AreEqual(30, saved.WritingTolerance);
        Assert.AreEqual("third", saved.WritingPerson);
        Assert.AreEqual(1, saved.WritingRules.Count);

        var reloaded = await instance.Client.GetFromJsonAsync<ContentSettingsDto>("/api/content-settings");
        Assert.AreEqual("我的风格", reloaded!.WritingRules[0].Title, "读回来要还是用户那一份，不是默认清单。");
        Assert.AreEqual("短句为主，不写总结。", reloaded.WritingRules[0].Instruction);

        using var emptied = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(null, null, null, null, null, WritingRules: []));

        emptied.EnsureSuccessStatusCode();
        Assert.AreEqual(
            0,
            (await emptied.Content.ReadFromJsonAsync<ContentSettingsDto>())!.WritingRules.Count,
            "用户把清单删空是明确的选择，不能在读回来时又变回默认条目。");

        // 认不出来的人称要被明确拒绝，而不是悄悄退回第一人称 —— 那会写出一篇人称不对的文章。
        using var badPerson = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(null, null, null, null, null, WritingPerson: "fourth"));

        Assert.AreEqual(HttpStatusCode.BadRequest, badPerson.StatusCode);
    }

    [TestMethod]
    public async Task A_half_filled_writing_rule_is_rejected_by_name()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var half = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(
                null, null, null, null, null,
                WritingRules: [new WritingRuleDto("只有标题", "   ")]));

        Assert.AreEqual(HttpStatusCode.BadRequest, half.StatusCode);
        Assert.AreEqual(
            "writing.rule.instruction_invalid",
            (await half.Content.ReadFromJsonAsync<ApiError>())!.Code,
            "半填的条目是笔误，要按名字报出来，而不是被静默丢掉。");

        using var tooLong = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(null, null, null, null, null, WritingMaxCharacters: 99999));

        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.AreEqual("writing.max.out_of_range", (await tooLong.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // 畸形 JSON 是调用方的错，不是服务端缺陷：必须是 400，绝不能是 500。
        // 这里只断言状态码：框架自己可能已经写下 400（响应已开始），那时就没有响应体可以再补校验码；
        // 中间件在异常真的逃逸到它那里的路径上（生产/开发容器里实测过）会补上 validation_failed。
        using var malformed = await instance.Client.PatchAsync(
            "/api/content-settings",
            new StringContent("{publishLocalTime:23:16}", System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(
            HttpStatusCode.BadRequest,
            malformed.StatusCode,
            "读不懂的请求体是 400，不是 500。");

        // 最少不小于最多：用户点名要这条规则，报错也要能指名道姓。
        using var inverted = await instance.Client.PatchAsJsonAsync(
            "/api/content-settings",
            new UpdateContentSettingsRequest(
                null, null, null, null, null,
                WritingMinCharacters: 400,
                WritingMaxCharacters: 300));

        Assert.AreEqual(HttpStatusCode.BadRequest, inverted.StatusCode);
        Assert.AreEqual("writing.range.invalid", (await inverted.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    /// <summary>
    /// 写作规范卡渲染在发布设置页，而且在 Front matter 模板**上方**（附录 A.24 指明了摆放位置）。
    /// </summary>
    [TestMethod]
    public async Task The_writing_spec_card_renders_above_the_front_matter_template()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var page = await instance.Client.GetAsync("/publishing");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());

        page.EnsureSuccessStatusCode();

        var specAt = html.IndexOf("写作规范</h2>", StringComparison.Ordinal);
        var templateAt = html.IndexOf("Front matter 模板", StringComparison.Ordinal);

        Assert.IsTrue(specAt >= 0, $"发布设置页没有渲染写作规范卡片：\n{html}");
        Assert.IsTrue(templateAt > specAt, "写作规范卡必须在 Front matter 模板上方。");
        StringAssert.Contains(html, "最少字数");
        StringAssert.Contains(html, "最多字数");
        StringAssert.Contains(html, "公差");
        StringAssert.Contains(html, "添加一条规范");
    }

    /// <summary>
    /// 「保存发布设置」在页头右上角、与「刷新状态」并排。
    /// <para>
    /// 它原来贴在「时区」那一行上，看起来像是在给时区做设置 —— 一个按钮挨着哪段文字，就会被读成那段文字的
    /// 动作。保存属于整页，所以它属于页头。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task The_save_button_sits_in_the_page_header_beside_the_refresh_button()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var page = await instance.Client.GetAsync("/publishing");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());

        page.EnsureSuccessStatusCode();

        var head = Regex.Match(
            html,
            "<header class=\"page-head\">.*?</header>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.IsTrue(head.Success, $"发布设置页没有渲染页头：\n{html}");
        StringAssert.Contains(head.Value, "刷新状态");
        StringAssert.Contains(head.Value, "保存发布设置");
        StringAssert.Contains(head.Value, "btn-primary", "保存是这一页的主操作，应当保持主按钮样式。");

        // 时区仍然要说清楚，只是它不再挂着一个按钮。
        StringAssert.Contains(html, "时区", "计划时间用哪个时区仍然要写在页面上。");
        Assert.IsFalse(
            html.Contains("save-bar", StringComparison.Ordinal),
            "保存按钮已经移到页头，不该再留着那条「时区 + 保存」的横条。");
    }

    /// <summary>
    /// The eight sections the operator asked for (2026-09-24). It walks the menu the way a person does: every route
    /// answers 200 and renders a heading of its own, the menu names all eight, and the pages the layout replaced are
    /// gone rather than merely unlinked.
    /// <para>
    /// Every assertion runs against HTML-decoded HTML on purpose: a value that came out of a binding is written as a
    /// numeric character reference, while text that was literal in the template is not — so a raw substring test on
    /// Chinese would fail for reasons that have nothing to do with the page. Decoding first makes the two
    /// indistinguishable, which is what the assertion is actually about.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task The_admin_surface_is_the_eight_sections_that_were_asked_for()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        // One marker per section, each a string that section renders unconditionally.
        var sections = new (string Route, string Marker)[]
        {
            ("/", "基础健康检查"),
            ("/devices", "生成配对码"),
            ("/data", "数据管理"),
            ("/models", "语音转写"),
            ("/notifications", "发送测试邮件"),
            ("/publishing", "Front matter 模板"),
            ("/content", "主题管理"),
            ("/system", "监听端口"),
        };

        foreach (var (route, marker) in sections)
        {
            using var response = await instance.Client.GetAsync(route);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {route} failed:\n{html}");
            StringAssert.Contains(html, marker, $"{route} 应当渲染出「{marker}」。");
        }

        using var home = await instance.Client.GetAsync("/");
        var homeHtml = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync());

        // The menu itself: eight labels, one per section.
        foreach (var label in new[] { "状态", "设备", "数据管理", "模型管理", "通知管理", "发布设置", "内容管理", "系统设置" })
        {
            StringAssert.Contains(homeHtml, label, $"导航里应当有「{label}」。");
        }

        Assert.IsFalse(
            homeHtml.Contains("当前是 HTTP 连接", StringComparison.Ordinal),
            "连接风险只在提交登录凭据前提示，登录后的每个管理页不应重复占用界面。");

        // The two pages the eight-section layout absorbed are gone, not just unlinked. (/inputs is deliberately
        // still routed: it is the per-day capture view, which an existing assertion in this assembly covers.)
        foreach (var removed in new[] { "/settings", "/operations" })
        {
            using var response = await instance.Client.GetAsync(removed);

            Assert.AreEqual(
                HttpStatusCode.NotFound,
                response.StatusCode,
                $"{removed} 已经并入八个分区，不应当还能打开（got {response.StatusCode}）。");
        }

        // And the per-day capture view still answers an administrator, with the draft of that day on it.
        using (var capture = await instance.Client.GetAsync("/inputs"))
        {
            var captureHtml = WebUtility.HtmlDecode(await capture.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.OK, capture.StatusCode);
            StringAssert.Contains(captureHtml, "当天的草稿");
        }
    }

    private static async Task<ReflectionDto> WaitForDraftAsync(HttpClient client, string contentDate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        ReflectionDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/reflections/{contentDate}");

            if (response.IsSuccessStatusCode)
            {
                last = await response.Content.ReadFromJsonAsync<ReflectionDto>();

                if (last?.WorkingVersion is not null &&
                    last.Status is ReflectionStatusNames.ReviewRequired or ReflectionStatusNames.Confirmed)
                {
                    return last;
                }
            }

            await Task.Delay(250);
        }

        Assert.Fail($"No draft was produced in time. Last seen: {last?.Status ?? "nothing"}");
        throw new InvalidOperationException("unreachable");
    }
}
