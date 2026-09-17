using System.Net;
using DailyMusings.Admin.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The SMTP form can be filled from a preset (docs/开发指导.md §12). Two things have to hold for that to be a help
/// rather than a trap: the buttons have to reach an operator, and no preset may describe a transport this build
/// cannot speak.
/// <para>
/// The second half is the interesting one. The sender is <c>System.Net.Mail.SmtpClient</c>, which implements
/// explicit TLS (STARTTLS) only, so a preset pointing at a provider's 465 — implicit TLS, the "SSL: true,
/// STARTTLS: false" every other mail form shows — would fill the form with values that can only time out. Measured
/// against the live servers while writing this: 587 on smtp.qq.com, smtp.exmail.qq.com, smtp.gmail.com,
/// smtp.office365.com and smtp.feishu.cn answers a plaintext greeting and advertises STARTTLS, while smtp.163.com,
/// smtp.126.com and smtp.qiye.aliyun.com never greet on 587 at all (they only accept a handshake first, on 465).
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SmtpPresetApiTests
{
    [TestMethod]
    public void Every_preset_asks_for_the_one_transport_the_sender_speaks()
    {
        Assert.IsTrue(SmtpPresets.All.Count > 0, "A form with no presets is the state this feature exists to replace.");

        foreach (var preset in SmtpPresets.All)
        {
            Assert.IsTrue(
                preset.UseStartTls,
                $"{preset.Label} must ask for STARTTLS: the sender cannot do implicit TLS, so a preset without it "
                + "would fill the form with values that hang until the timeout.");

            Assert.AreEqual(
                587,
                preset.Port,
                $"{preset.Label} must use 587. Port 465 is implicit TLS, which System.Net.Mail.SmtpClient does not "
                + "implement — a preset on 465 is a trap, not a convenience.");

            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Host));
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Note), $"{preset.Label} needs its provider-specific advice.");
        }

        CollectionAssert.AllItemsAreUnique(
            SmtpPresets.All.Select(preset => preset.Host).ToList(),
            "Two presets for the same host would be one button too many.");
    }

    [TestMethod]
    public async Task The_models_page_offers_the_presets_and_says_what_the_transport_does_not_support()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/models");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // Blazor encodes everything a component *binds* as numeric character references (「邮箱」 arrives as
        // &#x90AE;&#x7BB1;), so a raw search for Chinese only ever finds the template's own literal text. Decode
        // first, or this assertion would fail while the page renders exactly what it should.
        var text = WebUtility.HtmlDecode(html);

        foreach (var preset in SmtpPresets.All)
        {
            StringAssert.Contains(text, preset.Label, $"The {preset.Label} preset has to be reachable from the page.");
        }

        // The trap is named where the operator is about to type the port, not only in the manual.
        StringAssert.Contains(text, "STARTTLS");
        StringAssert.Contains(text, "465");
    }
}
