namespace DailyMusings.Admin.Shared;

/// <summary>
/// One mailbox a form can be filled from: the three fields that are the same for everybody who uses that provider,
/// plus the sentence that is <em>not</em> the same (where the password comes from).
/// </summary>
/// <param name="Label">What the button says.</param>
/// <param name="Host">SMTP host name.</param>
/// <param name="Port">SMTP port. Every preset here is 587 — see <see cref="SmtpPresets"/> for why.</param>
/// <param name="UseStartTls">Always true; recorded so the invariant is data rather than prose.</param>
/// <param name="Note">Provider-specific advice, shown after the form is filled.</param>
public sealed record SmtpPreset(
    string Label,
    string Host,
    int Port,
    bool UseStartTls,
    string Note);

/// <summary>
/// The mailboxes an operator is most likely to have, so the three fields nobody remembers are filled by a click
/// instead of by a support conversation (docs/开发指导.md §12).
/// <para>
/// Every preset uses <strong>587 with STARTTLS</strong>, and that is not a style preference: the sender is
/// <c>System.Net.Mail.SmtpClient</c>, which implements <em>explicit</em> TLS only — it opens the connection in the
/// clear, reads the greeting, then upgrades. Providers that speak <em>implicit</em> TLS on 465 (the connection must
/// be a TLS handshake from the first byte — what other software labels "SSL: true, STARTTLS: false") therefore
/// cannot be used by this build at all: measured against the live servers, smtp.163.com, smtp.126.com and
/// smtp.qiye.aliyun.com never send a plaintext greeting on 587, so a preset for them would produce a request that
/// hangs until the timeout and then reports "the SMTP server could not be reached". They are deliberately absent
/// rather than present-but-broken, and <see cref="Port"/> being 587 is the invariant a test pins down.
/// </para>
/// </summary>
public static class SmtpPresets
{
    /// <summary>The presets, in the order the buttons appear.</summary>
    public static IReadOnlyList<SmtpPreset> All { get; } =
    [
        new(
            "QQ 邮箱",
            "smtp.qq.com",
            587,
            true,
            "用户名填完整的 QQ 邮箱地址；密码要在 QQ 邮箱「设置 → 账户 → POP3/IMAP/SMTP 服务」里生成授权码，"
            + "不是 QQ 登录密码。"),

        new(
            "腾讯企业邮",
            "smtp.exmail.qq.com",
            587,
            true,
            "用户名填完整的企业邮地址；密码是管理后台里为该客户端生成的专用密码，与网页登录密码可能不同。"),

        new(
            "Gmail",
            "smtp.gmail.com",
            587,
            true,
            "先在 Google 账号里开启两步验证并生成「应用专用密码」，用户名填完整的 Gmail 地址。"),

        new(
            "Outlook / Microsoft 365",
            "smtp.office365.com",
            587,
            true,
            "用户名填完整的邮箱地址；若该租户开启了安全默认值，还需要为这个应用单独允许 SMTP AUTH。"),
    ];
}
