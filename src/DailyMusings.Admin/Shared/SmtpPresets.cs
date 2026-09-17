using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;

namespace DailyMusings.Admin.Shared;

/// <summary>
/// One mailbox a form can be filled from: the fields that are the same for everybody who uses that provider, plus the
/// sentence that is <em>not</em> the same (where the password comes from).
/// </summary>
/// <param name="Label">What the button says.</param>
/// <param name="Host">SMTP host name.</param>
/// <param name="Port">SMTP port, matching <paramref name="Security"/>.</param>
/// <param name="Security">How that provider protects the connection on that port.</param>
/// <param name="Note">Provider-specific advice, shown after the form is filled.</param>
public sealed record SmtpPreset(
    string Label,
    string Host,
    int Port,
    SmtpSecurity Security,
    string Note)
{
    /// <summary>
    /// What the other program's form calls this transport, so the operator can see that the button and their old
    /// configuration are talking about the same thing.
    /// </summary>
    public string SecurityLabel => Security switch
    {
        SmtpSecurity.ImplicitTls => "SSL/TLS",
        SmtpSecurity.StartTls => "STARTTLS",
        _ => "不加密",
    };
}

/// <summary>A row of preset buttons that all fill the same transport.</summary>
public sealed record SmtpPresetGroup(string Title, IReadOnlyList<SmtpPreset> Presets);

/// <summary>
/// The mailboxes an operator is most likely to have, so the fields nobody remembers are filled by a click instead of
/// by a support conversation (docs/开发指导.md §12).
/// <para>
/// Grouped by transport because that is the axis an operator has to reconcile with whatever their provider's help page
/// says, and because the two groups are genuinely different: a provider that speaks STARTTLS on 587 and one that only
/// accepts a handshake on 465 are both covered, and each button fills the pair that was measured to work. Every entry
/// here was checked against the live server while writing this: smtp.qq.com, smtp.exmail.qq.com, smtp.gmail.com and
/// smtp.office365.com answer a plaintext greeting on 587 and advertise STARTTLS; smtp.163.com, smtp.126.com and
/// smtp.qiye.aliyun.com never greet on 587 at all and only speak inside a tunnel on 465.
/// </para>
/// <para>
/// No preset fills an unencrypted configuration: plain TCP is for a relay on localhost, which is not something a
/// provider button should ever suggest.
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
            SmtpSecurity.StartTls,
            "用户名填完整的 QQ 邮箱地址；密码要在 QQ 邮箱「设置 → 账户 → POP3/IMAP/SMTP 服务」里生成授权码，"
            + "不是 QQ 登录密码。QQ 也提供 465，两种都行。"),

        new(
            "腾讯企业邮",
            "smtp.exmail.qq.com",
            587,
            SmtpSecurity.StartTls,
            "用户名填完整的企业邮地址；密码是管理后台里为该客户端生成的专用密码，与网页登录密码可能不同。"),

        new(
            "Gmail",
            "smtp.gmail.com",
            587,
            SmtpSecurity.StartTls,
            "先在 Google 账号里开启两步验证并生成「应用专用密码」，用户名填完整的 Gmail 地址。"),

        new(
            "Outlook / Microsoft 365",
            "smtp.office365.com",
            587,
            SmtpSecurity.StartTls,
            "用户名填完整的邮箱地址；若该租户开启了安全默认值，还需要为这个应用单独允许 SMTP AUTH。"),

        new(
            "163 邮箱",
            "smtp.163.com",
            465,
            SmtpSecurity.ImplicitTls,
            "163 只在 465 上收信（587 上不说话），所以安全方式必须是 SSL/TLS。密码用「设置 → POP3/SMTP/IMAP」里"
            + "开启服务时生成的授权码。"),

        new(
            "126 邮箱",
            "smtp.126.com",
            465,
            SmtpSecurity.ImplicitTls,
            "与 163 同一套服务：只有 465，安全方式必须是 SSL/TLS，密码是授权码。"),

        new(
            "阿里云企业邮",
            "smtp.qiye.aliyun.com",
            465,
            SmtpSecurity.ImplicitTls,
            "只有 465，安全方式必须是 SSL/TLS；密码是邮箱设置里为客户端生成的密码。"),
    ];

    /// <summary>The presets as the buttons are laid out: one row per transport, STARTTLS first.</summary>
    public static SmtpPresetGroup[] Grouped() =>
    [
        new("STARTTLS（587）：", [.. All.Where(preset => preset.Security == SmtpSecurity.StartTls)]),
        new("SSL/TLS（465）：", [.. All.Where(preset => preset.Security == SmtpSecurity.ImplicitTls)]),
    ];

    /// <summary>The token an operator would write in a configuration file for a preset's transport.</summary>
    public static string TokenFor(SmtpPreset preset) => SmtpSecurityNames.ToToken(preset.Security);
}
