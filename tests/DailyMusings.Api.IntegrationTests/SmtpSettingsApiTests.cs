using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The SMTP settings as the admin page reads and writes them (docs/开发指导.md §12).
/// <para>
/// The shape of this contract is the point of the test: the form asks for a host, a port, the sender's mailbox, a
/// password, SSL, STARTTLS and a recipient — and the sender's mailbox <em>is</em> the SMTP username, so there is no
/// <c>username</c> field, and the password is filed under one fixed name, so there is no <c>secretName</c> either.
/// A response that still carried either would mean the old model is being described to a form that no longer has
/// those boxes.
/// </para>
/// <para>
/// The two switches are asserted as booleans because that is what the checkboxes bind to, and the pair being
/// impossible to set at once is asserted here as well as in the use case: this is the endpoint the page actually
/// calls, and the refusal has to arrive as a 400 with a code the page can translate.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class SmtpSettingsApiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task The_settings_are_the_seven_fields_and_no_username_or_secret_name()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.GetAsync("/api/system/smtp-settings");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();

        foreach (var gone in new[] { "\"security\"", "\"username\"", "\"secretName\"", "\"fromName\"", "\"timeoutSeconds\"" })
        {
            Assert.IsFalse(
                raw.Contains(gone, StringComparison.OrdinalIgnoreCase),
                $"The response still describes a field the form no longer has: {gone}\n{raw}");
        }

        foreach (var present in new[] { "\"useSsl\"", "\"useStartTls\"", "\"fromAddress\"", "\"toAddress\"" })
        {
            Assert.IsTrue(raw.Contains(present, StringComparison.OrdinalIgnoreCase), $"{present} is missing from:\n{raw}");
        }

        var dto = JsonSerializer.Deserialize<SmtpSettingsDto>(raw, Web);

        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.Enabled, "A fresh instance sends nothing.");
        Assert.IsFalse(dto.UseSsl);
        Assert.IsFalse(dto.UseStartTls);
        Assert.IsFalse(string.IsNullOrWhiteSpace(dto.FromAddress));
    }

    [TestMethod]
    public async Task The_two_switches_are_written_independently_and_cannot_both_be_true()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        // The "SSL: true, STARTTLS: false" every other mail form shows.
        using var ssl = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(true, "smtp.example.com", 465, "noreply@example.com", UseSsl: true, UseStartTls: false));

        ssl.EnsureSuccessStatusCode();
        var sslDto = await ssl.Content.ReadFromJsonAsync<SmtpSettingsDto>();

        Assert.IsNotNull(sslDto);
        Assert.IsTrue(sslDto.Enabled);
        Assert.IsTrue(sslDto.UseSsl);
        Assert.IsFalse(sslDto.UseStartTls);
        Assert.AreEqual("smtp.example.com", sslDto.Host);
        Assert.AreEqual(465, sslDto.Port);
        Assert.AreEqual("noreply@example.com", sslDto.FromAddress, "The sender's mailbox travels as itself; it is the username.");

        // The other checkbox, which has to turn the first one off in the same save.
        using var startTls = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, 587, null, UseSsl: false, UseStartTls: true));

        startTls.EnsureSuccessStatusCode();
        var startTlsDto = await startTls.Content.ReadFromJsonAsync<SmtpSettingsDto>();

        Assert.IsFalse(startTlsDto!.UseSsl);
        Assert.IsTrue(startTlsDto.UseStartTls);

        // Both at once: refused with a code the page can translate into a sentence.
        using var both = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, null, null, UseSsl: true, UseStartTls: true));

        Assert.AreEqual(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.AreEqual("smtp.security.conflicting", (await both.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // And a refused save changes nothing.
        var after = await instance.Client.GetFromJsonAsync<SmtpSettingsDto>("/api/system/smtp-settings");

        Assert.IsFalse(after!.UseSsl);
        Assert.IsTrue(after.UseStartTls);
    }

    [TestMethod]
    public async Task A_password_cannot_be_saved_on_a_connection_with_no_encryption()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        // No password anywhere: an unencrypted relay on localhost is a legal configuration, not a mistake.
        using var localRelay = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(true, "127.0.0.1", 1025, "dailymusings@localhost", UseSsl: false, UseStartTls: false));

        localRelay.EnsureSuccessStatusCode();

        // With one: the password would be offered on a wire anyone can read, so the save is refused.
        using var inTheClear = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, null, null, UseSsl: false, UseStartTls: false, Password: "s3cret"));

        Assert.AreEqual(HttpStatusCode.BadRequest, inTheClear.StatusCode);
        Assert.AreEqual(
            "smtp.security.credentials_in_clear",
            (await inTheClear.Content.ReadFromJsonAsync<ApiError>())!.Code);

        // The same password with a switch on is fine, and it never comes back out.
        using var encrypted = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, 465, null, UseSsl: true, UseStartTls: false, Password: "s3cret"));

        encrypted.EnsureSuccessStatusCode();

        var raw = await encrypted.Content.ReadAsStringAsync();

        Assert.IsFalse(raw.Contains("s3cret", StringComparison.Ordinal), "The password must never be in a response.");
        Assert.IsTrue((await encrypted.Content.ReadFromJsonAsync<SmtpSettingsDto>())!.HasPassword);
    }

    /// <summary>
    /// The mailboxes an operator used to fill in by clicking a preset are gone: no preset list in the source, no
    /// button on either page, and no endpoint that serves one.
    /// </summary>
    [TestMethod]
    public async Task No_preset_remains_in_the_api_or_on_the_pages()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        foreach (var route in new[] { "/api/system/smtp-presets", "/api/system/smtp-settings/presets", "/api/system/email-presets" })
        {
            using var response = await instance.Client.GetAsync(route);

            Assert.AreEqual(
                HttpStatusCode.NotFound,
                response.StatusCode,
                $"{route} 不应当存在：常用邮箱预设已经删除（got {response.StatusCode}）。");
        }

        foreach (var route in new[] { "/models", "/notifications" })
        {
            using var response = await instance.Client.GetAsync(route);
            var html = await response.Content.ReadAsStringAsync();

            foreach (var preset in new[] { "smtp.qq.com", "smtp.163.com", "smtp.gmail.com", "常用邮箱", "一键预置" })
            {
                Assert.IsFalse(
                    html.Contains(preset, StringComparison.OrdinalIgnoreCase),
                    $"{route} 上还留着预设的痕迹：{preset}");
            }
        }
    }
}
