using System.Net;
using System.Text;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// 写作规范真的进了提示词，而且放在当天素材**之前**（docs/开发指导.md §8.4，附录 A.24）。
/// <para>
/// 这一条只能在这一层验：规范的落库、界面与校验都在别处覆盖，但「模型到底看到了什么、看到的顺序是什么」只有
/// 拼提示词的地方知道。顺序是需求的一部分 —— 用户要的是前置提示词，不是写在素材后面的补丁。
/// </para>
/// </summary>
[TestClass]
public sealed class WritingSpecPromptTests
{
    [TestMethod]
    public async Task The_spec_states_the_length_person_and_every_rule_before_the_day_material()
    {
        var handler = new CapturingHandler();
        var client = new OpenAiCompatibleGenerationClient(
            new HttpClient(handler),
            TestSecretStore.With("test-key", "not-a-real-secret"),
            new EnabledSettings(),
            NullLogger<OpenAiCompatibleGenerationClient>.Instance);

        await client.GenerateAsync(
            new GenerationRequest(
                ContentDate.From(new DateOnly(2026, 9, 29)),
                Array.Empty<InputEntry>(),
                Array.Empty<RetrievedMaterial>(),
                new WritingSettings(
                    100,
                    420,
                    20,
                    WritingPerson.Third,
                    [
                        new WritingRule("行文风格", "平实克制，不要升华。"),
                        new WritingRule("用词禁区", "不用「总之」。"),
                    ]),
                "generation-v2",
                Array.Empty<string>()),
            CancellationToken.None);

        var prompt = handler.Prompt;

        StringAssert.Contains(prompt, "100 到 420 字", "篇幅要按用户填的区间说明，而不是三档枚举。");
        StringAssert.Contains(prompt, "短到 80 字", "有公差时要把「可以到多少」说清楚。");
        StringAssert.Contains(prompt, "长到 440 字", "公差对上限同样生效。");
        StringAssert.Contains(prompt, "第三人称", "人称要跟着用户的选择走。");
        StringAssert.Contains(prompt, "行文风格：平实克制，不要升华。", "用户写的规范要原样进入提示词。");
        StringAssert.Contains(prompt, "用词禁区：不用「总之」。", "每一条规范都要带上，不能只发第一条。");

        var specAt = prompt.IndexOf("写作规范", StringComparison.Ordinal);
        var materialAt = prompt.IndexOf("今天的素材", StringComparison.Ordinal);

        Assert.IsTrue(specAt >= 0, $"提示词里没有写作规范块：\n{prompt}");
        Assert.IsTrue(materialAt > specAt, $"写作规范必须出现在当天素材之前，实际位置 {specAt} vs {materialAt}：\n{prompt}");

        // 系统消息是用户改不了的那一半。如果它自己写死一个人称，用户刚选的人称就会被它压掉 —— 配置看起来存了、
        // 实际不生效。这条断言守的就是「人称只能由写作规范说」。
        Assert.IsFalse(
            handler.System.Contains("人称", StringComparison.Ordinal),
            $"系统规则不该写死人称，否则「博客人称」这个设置是假的：\n{handler.System}");
    }

    [TestMethod]
    public async Task An_empty_rule_list_produces_no_style_block_rather_than_invented_rules()
    {
        var handler = new CapturingHandler();
        var client = new OpenAiCompatibleGenerationClient(
            new HttpClient(handler),
            TestSecretStore.With("test-key", "not-a-real-secret"),
            new EnabledSettings(),
            NullLogger<OpenAiCompatibleGenerationClient>.Instance);

        await client.GenerateAsync(
            new GenerationRequest(
                ContentDate.From(new DateOnly(2026, 9, 29)),
                Array.Empty<InputEntry>(),
                Array.Empty<RetrievedMaterial>(),
                new WritingSettings(300, 400, 0, WritingPerson.First, []),
                "generation-v2",
                Array.Empty<string>()),
            CancellationToken.None);

        var prompt = handler.Prompt;

        // 篇幅和人称仍然要说，否则模型连长度都没有依据；但一条规则都不许被凭空补上。
        StringAssert.Contains(prompt, "300 到 400 字", "关掉公差时给出的是区间本身。");
        StringAssert.Contains(prompt, "不要超出这个范围", "关掉公差要把范围说死。");
        StringAssert.Contains(prompt, "第一人称");
        Assert.IsFalse(prompt.Contains("行文风格：", StringComparison.Ordinal), $"删空的清单不该又冒出默认条目：\n{prompt}");
    }

    private sealed class EnabledSettings : IGenerationSettingsProvider
    {
        public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GenerationSettings.Default with
            {
                Enabled = true,
                BaseUrl = "https://generation.example/v1",
                Model = "test-model",
                SecretName = "test-key",
                Timeout = TimeSpan.FromSeconds(5),
            });
    }

    /// <summary>
    /// 回一个最小的合法响应，并把请求体里的用户提示词取出来。
    /// <para>
    /// 必须解 JSON 再取，不能直接在原始请求体上做字符串断言：请求体是序列化后的 JSON，默认编码器会把中文转义
    /// 成 <c>\uXXXX</c>，于是任何中文断言都会失败在一个与产品无关的地方。
    /// </para>
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Prompt { get; private set; } = string.Empty;

        public string System { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(raw);
            var messages = document.RootElement.GetProperty("messages");
            System = messages[0].GetProperty("content").GetString() ?? string.Empty;
            Prompt = messages[1].GetProperty("content").GetString() ?? string.Empty;

            var draft = JsonSerializer.Serialize(new
            {
                title = "标题",
                summary = "摘要",
                body = "正文。",
            });

            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = draft } } },
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
