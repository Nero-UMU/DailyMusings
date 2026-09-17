using System.Net;
using DailyMusings.Admin.Shared;
using DailyMusings.Application.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The SMTP form can be filled from a preset (docs/开发指导.md §12). Two things have to hold for that to be a help
/// rather than a trap: the buttons have to reach an operator, and no preset may describe a configuration that does
/// not work.
/// <para>
/// The second half is the interesting one, and it changed shape once the transport learned implicit TLS. It used to be
/// "every preset must be 587 + STARTTLS, because that is the only dialect the sender knows"; now that 465 works too,
/// the invariant is that a preset always names a transport that was measured against the live server, that the port
/// matches it (587 ↔ STARTTLS, 465 ↔ TLS), and that no preset ever fills an unencrypted configuration — that last one
/// is only ever right for a relay on localhost, which is not something a provider button should suggest.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SmtpPresetApiTests
{
    [TestMethod]
    public void Every_preset_is_an_encrypted_transport_with_the_port_that_belongs_to_it()
    {
        Assert.IsTrue(SmtpPresets.All.Count > 0, "A form with no presets is the state this feature exists to replace.");

        foreach (var preset in SmtpPresets.All)
        {
            Assert.AreNotEqual(
                SmtpSecurity.None,
                preset.Security,
                $"{preset.Label} must not fill an unencrypted configuration: plain TCP is for a relay on localhost.");

            var expectedPort = preset.Security == SmtpSecurity.ImplicitTls ? 465 : 587;

            Assert.AreEqual(
                expectedPort,
                preset.Port,
                $"{preset.Label} says {preset.SecurityLabel} on port {preset.Port}; {preset.SecurityLabel} on this "
                + $"transport means port {expectedPort}, and a mismatched pair is precisely the trap this form exists "
                + "to avoid.");

            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Host));
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Note), $"{preset.Label} needs its provider-specific advice.");
        }

        CollectionAssert.AllItemsAreUnique(
            SmtpPresets.All.Select(preset => preset.Host).ToList(),
            "Two presets for the same host would be one button too many.");
    }

    [TestMethod]
    public void The_presets_are_offered_grouped_by_the_transport_they_fill()
    {
        var groups = SmtpPresets.Grouped();

        Assert.AreEqual(2, groups.Length);
        CollectionAssert.AreEquivalent(
            SmtpPresets.All.ToArray(),
            groups.SelectMany(group => group.Presets).ToArray(),
            "Every preset has to appear exactly once.");

        foreach (var group in groups)
        {
            Assert.IsTrue(group.Presets.Count > 0, $"{group.Title} would be an empty row of buttons.");
            Assert.AreEqual(
                1,
                group.Presets.Select(preset => preset.Security).Distinct().Count(),
                $"{group.Title} exists to fill one transport.");
        }

        // 163 and 126 were unusable before implicit TLS existed; a group that fills 465 has to actually offer them.
        Assert.IsTrue(
            groups.SelectMany(group => group.Presets).Any(preset => preset.Security == SmtpSecurity.ImplicitTls),
            "The 465 group is the half of this feature that makes 'copy the settings out of my other program' work.");
    }

    [TestMethod]
    public async Task The_models_page_offers_the_presets_and_says_which_transport_is_which()
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

        // Both transports have to be named where the operator is about to type the port, not only in the manual.
        StringAssert.Contains(text, "STARTTLS");
        StringAssert.Contains(text, "465");
        StringAssert.Contains(text, "SSL/TLS");
    }
}
