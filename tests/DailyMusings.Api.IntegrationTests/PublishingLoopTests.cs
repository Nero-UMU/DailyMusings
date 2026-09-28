using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// Publishing over real HTTP (docs/开发指导.md §11.1, §13, decision A.7).
/// <para>
/// The interesting assertions here are about authority rather than plumbing: a device may upload a draft and a
/// device may not invent a destination or switch on unattended publishing, and that split is enforced by the API
/// surface rather than by a check somebody could forget to write.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class PublishingLoopTests
{
    private static Dictionary<string, string?> GenerationEnabled(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["Generation:Enabled"] = "true",
        ["Generation:BaseUrl"] = baseUrl,
        ["Generation:Model"] = "test-writer",
        ["Generation:SecretName"] = "openai-api-key",
        ["Generation:TimeoutSeconds"] = "30",
        ["Scheduler:IntervalSeconds"] = "1",

        // Notifications are exercised in the infrastructure tests; here the point is the publish path, and a
        // switched-off SMTP server keeps the queue free of jobs that can only fail.
        ["Notification:To"] = "owner@example.invalid",
    };

    [TestMethod]
    public async Task A_confirmed_day_is_exported_to_a_markdown_target()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        // Only an administrator may invent a destination.
        using var created = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("hexo", PublishTargetTypeNames.Markdown, "drafts"));

        created.EnsureSuccessStatusCode();
        var target = await created.Content.ReadFromJsonAsync<PublishTargetDto>();
        Assert.IsNotNull(target);
        Assert.IsFalse(target.AutomaticPublishEnabled, "Unattended publishing is off until somebody turns it on.");

        // Creating the same target twice is not an error.
        using var again = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("hexo", PublishTargetTypeNames.Markdown, "drafts"));
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
        Assert.AreEqual(target.Id, (await again.Content.ReadFromJsonAsync<PublishTargetDto>())!.Id);

        var (contentDate, _) = await GenerateAndConfirmAsync(instance, device);

        // Publishing needs the day to be confirmed, which it now is.
        using var published = await device.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/publish/{target.Id}",
            new PublishRequest(PublicationVisibilityNames.Draft, ReplaceExistingFile: false));

        published.EnsureSuccessStatusCode();
        var queued = await published.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.Queued, queued.Detail);

        var publication = await WaitForPublicationAsync(instance, contentDate, PublicationStatusNames.DraftUploaded);

        Assert.AreEqual("hexo", publication.TargetName);
        Assert.AreEqual("device:", publication.TriggeredBy![..7], "§11.1 records which device asked.");

        // The file is where the target said, named after the content day, and it is a draft.
        var fileName = Path.Combine(instance.RootPath, "markdown", "drafts", publication.RemoteId!);
        Assert.IsTrue(File.Exists(fileName), $"Expected {fileName}. Found: {Describe(instance)}");

        var content = await File.ReadAllTextAsync(fileName);
        StringAssert.Contains(content, $"date: {contentDate} 00:00:00");
        StringAssert.Contains(content, "draft: true");

        // And nothing was mailed: this was a manual action, and §12's events are about things that happen without
        // anyone watching.
        var jobs = await instance.Client.GetFromJsonAsync<JobListResponse>("/api/jobs?limit=50");
        Assert.IsNotNull(jobs);
        Assert.IsFalse(jobs.Items.Any(job => job.JobType == "Notification"));
    }

    [TestMethod]
    public async Task A_regenerated_working_version_is_shown_as_unpublished_when_an_older_version_was_published()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        var settings = GenerationEnabled(model.BaseUrl);
        settings["Scheduler:IntervalSeconds"] = "3600";
        await using var instance = await TestInstance.StartAsync(settings);

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var created = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("hexo", PublishTargetTypeNames.Markdown, "posts"));
        var target = await created.Content.ReadFromJsonAsync<PublishTargetDto>();

        var (contentDate, publishedVersionId) = await GenerateAndConfirmAsync(instance, device);

        using var publish = await device.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/publish/{target!.Id}",
            new PublishRequest(PublicationVisibilityNames.Public, ReplaceExistingFile: false));
        publish.EnsureSuccessStatusCode();
        var publication = await WaitForPublicationAsync(instance, contentDate, PublicationStatusNames.Published);
        Assert.AreEqual(publishedVersionId, publication.ReflectionVersionId);
        var publishedDirectory = Path.Combine(instance.RootPath, "markdown", "posts");
        var oldFile = Path.Combine(publishedDirectory, publication.RemoteId!);
        Assert.IsTrue(File.Exists(oldFile));

        model.State.GroundedSentence = "后来又补充了一条新素材。";
        model.State.UnsourcedSentence = string.Empty;
        using var lateInput = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest(
                model.State.GroundedSentence,
                DateTimeOffset.UtcNow.ToString("o"),
                480,
                "capture-after-publication"));
        lateInput.EnsureSuccessStatusCode();

        using var regenerate = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/regenerate-stale",
            new GenerateReflectionRequest(false, false));
        regenerate.EnsureSuccessStatusCode();

        var regenerated = await WaitForNewWorkingVersionAsync(instance.Client, contentDate, publishedVersionId);
        Assert.AreNotEqual(publishedVersionId, regenerated.WorkingVersionId);
        Assert.AreEqual(ReflectionStatusNames.ReviewRequired, regenerated.Status);
        Assert.AreNotEqual(regenerated.WorkingVersionId, publication.ReflectionVersionId);

        using var contentPage = await instance.Client.GetAsync("/content");
        var html = WebUtility.HtmlDecode(await contentPage.Content.ReadAsStringAsync());
        Assert.IsTrue(
            Regex.IsMatch(
                html,
                "<strong>0</strong><span>已发布</span>.*?<strong>1</strong><span>未发布</span>",
                RegexOptions.Singleline | RegexOptions.CultureInvariant),
            $"内容管理的已发布/未发布汇总没有按当前工作版本统计：\n{html}");
        var articleCard = Regex.Match(
            html,
            "<article class=\"article-card[^\"]*\">.*?</article>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.IsTrue(articleCard.Success, $"内容管理没有渲染稿件卡片：\n{html}");
        StringAssert.Contains(
            articleCard.Value,
            "未发布",
            "旧版本已发布不能让新生成、尚未发布的工作版本显示为已发布。");
        Assert.IsFalse(
            articleCard.Value.Contains(">已发布<", StringComparison.Ordinal),
            $"重新生成后的稿件卡片错误显示为已发布：\n{articleCard.Value}");

        using var publishingPage = await instance.Client.GetAsync("/publishing");
        var publishingHtml = WebUtility.HtmlDecode(await publishingPage.Content.ReadAsStringAsync());
        var publishCard = Regex.Match(
            publishingHtml,
            "<article class=\"card action-card public-card\">.*?</article>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.IsTrue(publishCard.Success, $"发布设置没有渲染今日发布卡片：\n{publishingHtml}");
        StringAssert.Contains(
            publishCard.Value,
            "尚未发布",
            "旧版本已公开后，新生成的工作版本在发布设置中必须恢复为尚未发布。");
        Assert.IsFalse(
            publishCard.Value.Contains("已公开发布", StringComparison.Ordinal),
            $"发布设置把旧版本的发布状态错误套到了新工作版本：\n{publishCard.Value}");

        var beforeReplacement = await instance.Client.GetFromJsonAsync<PublicationListResponse>(
            $"/api/reflections/{contentDate}/publications");
        Assert.AreEqual(
            1,
            beforeReplacement!.Items.Count(item => item.Status == PublicationStatusNames.Published),
            "重新生成只能产生未发布稿，不能提前撤下当日已发布稿。");
        Assert.IsTrue(File.Exists(oldFile), "只有用户点击发布替代稿后，才能删除当日旧的已发布文件。");

        await ConfirmExistingAsync(instance, contentDate);
        using var publishReplacement = await device.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/publish/{target.Id}",
            new PublishRequest(PublicationVisibilityNames.Public, ReplaceExistingFile: false));
        publishReplacement.EnsureSuccessStatusCode();

        var replacement = await WaitForPublishedVersionAsync(instance, contentDate, regenerated.WorkingVersionId!);
        var afterReplacement = await instance.Client.GetFromJsonAsync<PublicationListResponse>(
            $"/api/reflections/{contentDate}/publications");

        Assert.AreEqual(
            1,
            afterReplacement!.Items.Count(item => item.Status == PublicationStatusNames.Published),
            "一天只能保留一个已公开发布的稿件记录。");
        Assert.AreEqual(
            1,
            afterReplacement.Items.Count(item => item.Status == PublicationStatusNames.Superseded),
            "旧版本发布后应标记为已替换，而不是继续显示为已发布。");

        var files = Directory.GetFiles(publishedDirectory, "*.md", SearchOption.TopDirectoryOnly);
        Assert.AreEqual(1, files.Length, $"重新发布后目录里只能留下当天的新稿件。Found: {string.Join(", ", files)}");
        Assert.AreEqual(replacement.RemoteId, Path.GetFileName(files[0]));
    }

    [TestMethod]
    public async Task Publishing_an_unconfirmed_day_is_refused()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var created = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("hexo", PublishTargetTypeNames.Markdown, "drafts"));

        var target = await created.Content.ReadFromJsonAsync<PublishTargetDto>();

        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天试着记录了一点东西。", DateTimeOffset.UtcNow.ToString("o"), 480, "capture-1"));

        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();

        // No draft at all yet.
        using var tooEarly = await device.PostAsJsonAsync(
            $"/api/reflections/{ingested!.Input.ContentDate}/publish/{target!.Id}",
            new PublishRequest(PublicationVisibilityNames.Draft, false));

        Assert.AreEqual(HttpStatusCode.Conflict, tooEarly.StatusCode);
        var refusal = await tooEarly.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.AreEqual("publication.reflection_unknown", refusal!.Code);
    }

    /// <summary>
    /// §11.1 and decision A.7: the switch that lets the instance publish by itself belongs to the administrator, is
    /// protected by the password, and is unreachable from a device token.
    /// </summary>
    [TestMethod]
    public async Task Only_an_administrator_with_the_password_can_enable_unattended_publishing()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var created = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("blog", PublishTargetTypeNames.Markdown, "drafts"));

        var target = (await created.Content.ReadFromJsonAsync<PublishTargetDto>())!;

        // A device token cannot even see the route.
        using var fromDevice = await device.PostAsJsonAsync(
            $"/api/publish-targets/{target.Id}/automatic-publish",
            new SetAutomaticPublishRequest(true, "CorrectHorseBattery1"));

        Assert.IsTrue(
            fromDevice.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"A device token must not be able to enable automatic publishing (got {fromDevice.StatusCode}).");

        // The administrator gets it wrong first, and the wrong password is refused.
        using var wrongPassword = await instance.Client.PostAsJsonAsync(
            $"/api/publish-targets/{target.Id}/automatic-publish",
            new SetAutomaticPublishRequest(true, "not-the-password"));

        Assert.AreEqual(HttpStatusCode.Forbidden, wrongPassword.StatusCode);

        using var enabled = await instance.Client.PostAsJsonAsync(
            $"/api/publish-targets/{target.Id}/automatic-publish",
            new SetAutomaticPublishRequest(true, "CorrectHorseBattery1"));

        enabled.EnsureSuccessStatusCode();
        var enabledTarget = await enabled.Content.ReadFromJsonAsync<PublishTargetDto>();
        Assert.IsTrue(enabledTarget!.AutomaticPublishEnabled);

        // §11.1: the switch is attributable, so a target that publishes by itself can be traced to a person.
        Assert.IsFalse(string.IsNullOrWhiteSpace(enabledTarget.AutomaticPublishEnabledBy));
        Assert.IsNotNull(enabledTarget.AutomaticPublishEnabledAtUtc);

        using var disabled = await instance.Client.PostAsJsonAsync(
            $"/api/publish-targets/{target.Id}/automatic-publish",
            new SetAutomaticPublishRequest(false, "CorrectHorseBattery1"));

        disabled.EnsureSuccessStatusCode();
        Assert.IsFalse((await disabled.Content.ReadFromJsonAsync<PublishTargetDto>())!.AutomaticPublishEnabled);
    }

    /// <summary>
    /// Who may read and who may change the notification preferences (§9.3, §12).
    /// <para>
    /// The split is deliberate. §9.3 puts 通知偏好 on the client's settings screen, so a paired device may read them
    /// and say what is switched on; changing the recipient stays an administrator's decision, because a device token
    /// that could redirect the instance's mail could send the day's date and title to an address of its holder's
    /// choosing. This test used to assert that a device could not even read — which kept the client's settings screen
    /// from showing anything at all.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task Notification_settings_can_be_read_by_a_device_but_changed_only_by_an_administrator()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        instance.WriteSecret("openai-api-key", "test-api-key");
        await instance.SignInAsChangedAdministratorAsync();
        var (_, device) = await instance.PairDeviceAsync();

        using var fromDevice = await device.GetAsync("/api/notification-settings");

        Assert.AreEqual(
            HttpStatusCode.OK,
            fromDevice.StatusCode,
            "§9.3 puts the notification preferences on the client's settings screen, so a paired device may look.");

        var deviceView = await fromDevice.Content.ReadFromJsonAsync<NotificationSettingsDto>();
        Assert.AreEqual("owner@example.invalid", deviceView!.ToAddress);

        // Writing is the part a device must not be able to do: the recipient decides where the user's material goes.
        using var deviceWrite = await device.PatchAsJsonAsync(
            "/api/notification-settings",
            new UpdateNotificationSettingsRequest("attacker@example.invalid", null, null, null, null));

        Assert.IsTrue(
            deviceWrite.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device token must not be able to redirect notifications (got {deviceWrite.StatusCode}).");

        var settings = await instance.Client.GetFromJsonAsync<NotificationSettingsDto>("/api/notification-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual("owner@example.invalid", settings.ToAddress);
        Assert.IsFalse(settings.SmtpConfigured, "No SMTP server is configured, so no event can be on.");

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/notification-settings",
            new UpdateNotificationSettingsRequest("owner@example.invalid", "http://instance.test", DraftReady: true, null, null));

        updated.EnsureSuccessStatusCode();
        var after = await updated.Content.ReadFromJsonAsync<NotificationSettingsDto>();
        Assert.IsTrue(after!.DraftReady);
        Assert.AreEqual("http://instance.test", after.InstanceUrl);
    }

    [TestMethod]
    public async Task An_unauthenticated_caller_cannot_publish_or_read_publications()
    {
        await using var model = await StubGenerationEndpoint.StartAsync();
        await using var instance = await TestInstance.StartAsync(GenerationEnabled(model.BaseUrl));

        using var anonymous = new HttpClient { BaseAddress = instance.Client.BaseAddress };

        using var publications = await anonymous.GetAsync("/api/publications");
        Assert.AreEqual(HttpStatusCode.Unauthorized, publications.StatusCode);

        using var targets = await anonymous.GetAsync("/api/publish-targets");
        Assert.AreEqual(HttpStatusCode.Unauthorized, targets.StatusCode);

        using var create = await anonymous.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest("mine", PublishTargetTypeNames.Markdown, "drafts"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    private static async Task<(string ContentDate, string VersionId)> GenerateAndConfirmAsync(
        TestInstance instance,
        HttpClient device)
    {
        using var captured = await device.PostAsJsonAsync(
            "/api/inputs/text",
            new TextInputRequest("今天试着记录了一点东西。", DateTimeOffset.UtcNow.ToString("o"), 480, "capture-1"));

        captured.EnsureSuccessStatusCode();
        var ingested = await captured.Content.ReadFromJsonAsync<IngestResponse>();
        var contentDate = ingested!.Input.ContentDate;

        using var requested = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/generate",
            new GenerateReflectionRequest(false, false));

        requested.EnsureSuccessStatusCode();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        ReflectionDto? draft = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var read = await instance.Client.GetAsync($"/api/reflections/{contentDate}");
            if (read.IsSuccessStatusCode)
            {
                var candidate = await read.Content.ReadFromJsonAsync<ReflectionDto>();
                if (candidate?.Status == ReflectionStatusNames.ReviewRequired)
                {
                    draft = candidate;
                    break;
                }
            }

            await Task.Delay(250);
        }

        Assert.IsNotNull(draft, "The draft was never produced.");

        // The source check may still be running; acknowledging its findings is what confirming requires.
        var checkDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < checkDeadline)
        {
            using var sources = await instance.Client.GetAsync($"/api/reflections/{contentDate}/sources");
            if (sources.IsSuccessStatusCode)
            {
                var report = await sources.Content.ReadFromJsonAsync<ReflectionSourcesResponse>();
                if (report?.CheckedAtUtc is not null)
                {
                    break;
                }
            }

            await Task.Delay(250);
        }

        using var confirmed = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/confirm",
            new ConfirmReflectionRequest(AcceptedUnsourcedClaims: true));

        confirmed.EnsureSuccessStatusCode();
        var result = await confirmed.Content.ReadFromJsonAsync<ReflectionDto>();

        return (contentDate, result!.ConfirmedVersionId!);
    }

    private static async Task<PublicationDto> WaitForPublicationAsync(
        TestInstance instance,
        string contentDate,
        string status)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        PublicationDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var listed = await instance.Client.GetFromJsonAsync<PublicationListResponse>(
                $"/api/reflections/{contentDate}/publications");

            last = listed?.Items.FirstOrDefault();
            if (last?.Status == status)
            {
                return last;
            }

            if (last?.Status == PublicationStatusNames.Failed)
            {
                Assert.Fail($"The publication failed: {last.ErrorCode} {last.ErrorSummary}");
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The publication never reached {status}. Last seen: {last?.Status ?? "none"}");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<PublicationDto> WaitForPublishedVersionAsync(
        TestInstance instance,
        string contentDate,
        string versionId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var listed = await instance.Client.GetFromJsonAsync<PublicationListResponse>(
                $"/api/reflections/{contentDate}/publications");
            var publication = listed?.Items.FirstOrDefault(item =>
                item.ReflectionVersionId == versionId && item.Status == PublicationStatusNames.Published);

            if (publication is not null)
            {
                return publication;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Version {versionId} was never published.");
        throw new InvalidOperationException();
    }

    private static async Task ConfirmExistingAsync(TestInstance instance, string contentDate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var sources = await instance.Client.GetAsync($"/api/reflections/{contentDate}/sources");
            if (sources.IsSuccessStatusCode &&
                (await sources.Content.ReadFromJsonAsync<ReflectionSourcesResponse>())?.CheckedAtUtc is not null)
            {
                break;
            }

            await Task.Delay(250);
        }

        using var confirmed = await instance.Client.PostAsJsonAsync(
            $"/api/reflections/{contentDate}/confirm",
            new ConfirmReflectionRequest(AcceptedUnsourcedClaims: true));
        confirmed.EnsureSuccessStatusCode();
    }

    private static async Task<ReflectionDto> WaitForNewWorkingVersionAsync(
        HttpClient client,
        string contentDate,
        string previousVersionId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        ReflectionDto? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/reflections/{contentDate}");
            if (response.IsSuccessStatusCode)
            {
                last = await response.Content.ReadFromJsonAsync<ReflectionDto>();
                if (last?.WorkingVersionId is { } versionId &&
                    versionId != previousVersionId &&
                    last.Status == ReflectionStatusNames.ReviewRequired)
                {
                    return last;
                }
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The regenerated working version was never produced. Last status: {last?.Status ?? "none"}.");
        throw new InvalidOperationException("unreachable");
    }

    private static string Describe(TestInstance instance)
    {
        var root = Path.Combine(instance.RootPath, "markdown");
        return Directory.Exists(root)
            ? string.Join(", ", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            : "no markdown directory";
    }
}
