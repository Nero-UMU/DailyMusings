using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// The real test mail (§12, §16). The point of it is that a failure is <em>reported</em> rather than thrown: the
/// operator pressed a button and needs a sentence, and the admin page must not answer with a 500 it cannot
/// explain.
/// </summary>
[TestClass]
public class SendTestEmailTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An SMTP configuration whose events are all off — which the test mail deliberately ignores.</summary>
    private sealed class Settings : INotificationSettingsProvider
    {
        public NotificationSettings Value { get; set; } =
            new(ToAddress: "owner@example.test", InstanceUrl: "https://notes.example.test");

        public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
    }

    private sealed class Transport : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Exception? Fail { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (SendTestEmailUseCase UseCase, Transport Transport, Settings Settings) Build()
    {
        var settings = new Settings();
        var transport = new Transport();

        return (new SendTestEmailUseCase(settings, transport, new FixedClock(Now)), transport, settings);
    }

    [TestMethod]
    public async Task An_empty_recipient_is_a_refusal_without_a_send_attempt()
    {
        var (useCase, transport, settings) = Build();
        settings.Value = settings.Value with { ToAddress = string.Empty };

        var result = await useCase.ExecuteAsync(toAddress: null, CancellationToken.None);

        Assert.IsFalse(result.Sent);
        Assert.AreEqual("smtp.test.no_recipient", result.Code);
        Assert.IsNotNull(result.Detail);
        Assert.AreEqual(0, transport.Sent.Count, "Nothing may be sent when there is nowhere to send it.");
    }

    [TestMethod]
    public async Task The_address_in_the_request_wins_over_the_stored_one()
    {
        var (useCase, transport, _) = Build();

        var result = await useCase.ExecuteAsync("someone-else@example.test", CancellationToken.None);

        Assert.IsTrue(result.Sent);
        Assert.IsNull(result.Code);
        Assert.AreEqual(1, transport.Sent.Count);
        Assert.AreEqual("someone-else@example.test", transport.Sent[0].To);
        Assert.AreEqual("[每日随想] 测试邮件", transport.Sent[0].Subject);
    }

    /// <summary>
    /// §16: the mail carries facts, never the user's writing — a test message in particular may be sent to an
    /// address that is not the user's own.
    /// </summary>
    [TestMethod]
    public async Task The_message_states_what_it_is_and_nothing_private()
    {
        var (useCase, transport, _) = Build();

        await useCase.ExecuteAsync(toAddress: null, CancellationToken.None);

        var body = transport.Sent[0].Body;

        StringAssert.Contains(body, "说明 SMTP 配置可用");
        StringAssert.Contains(body, "https://notes.example.test");
        StringAssert.Contains(body, "2026-03-01 12:00:00 UTC", "The send instant has to be in the message.");
        StringAssert.Contains(body, "不含任何随想内容");
    }

    [TestMethod]
    public async Task A_refused_message_comes_back_as_a_code_and_a_sentence()
    {
        var (useCase, transport, _) = Build();

        transport.Fail = new PermanentExternalFailureException(
            "notification.secret_missing",
            "The secret 'smtp-password' is not provisioned.");

        var result = await useCase.ExecuteAsync(toAddress: null, CancellationToken.None);

        Assert.IsFalse(result.Sent);
        Assert.AreEqual("notification.secret_missing", result.Code);
        Assert.AreEqual("The secret 'smtp-password' is not provisioned.", result.Detail);
    }

    [TestMethod]
    public async Task Temporary_trouble_is_reported_the_same_way()
    {
        var (useCase, transport, _) = Build();

        transport.Fail = new TransientExternalFailureException("notification.network", "The relay could not be reached.");

        var result = await useCase.ExecuteAsync(toAddress: null, CancellationToken.None);

        Assert.IsFalse(result.Sent);
        Assert.AreEqual("notification.network", result.Code);
    }

    /// <summary>
    /// An unexpected defect must not leak its message — it can name internal paths or the recipient's server —
    /// so only the exception's type travels back.
    /// </summary>
    [TestMethod]
    public async Task An_unexpected_failure_reports_only_its_type()
    {
        var (useCase, transport, _) = Build();

        transport.Fail = new InvalidOperationException("C:\\secret\\path\\smtp.cs:42 exploded for owner@example.test");

        var result = await useCase.ExecuteAsync(toAddress: null, CancellationToken.None);

        Assert.IsFalse(result.Sent);
        Assert.AreEqual("smtp.test.failed", result.Code);
        Assert.IsNotNull(result.Detail);
        StringAssert.Contains(result.Detail, nameof(InvalidOperationException));
        Assert.IsFalse(
            result.Detail!.Contains("secret\\path", StringComparison.Ordinal),
            "The internal detail must not reach the operator-facing response.");
    }
}
