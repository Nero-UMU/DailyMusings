using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;

namespace DailyMusings.Infrastructure.Configuration;

/// <summary>
/// Reads a setting the way this instance means it: what the admin page stored, else the deployment configuration,
/// else the built-in default (docs/开发指导.md §8.1, §12).
/// <para>
/// The order matters. An instance configured entirely from compose keeps working exactly as before — nothing is
/// stored, so every read falls through to the environment — and an instance configured from the admin page picks the
/// change up on the very next call, which is the point of having a form for it. A restart is never required, which is
/// why the providers read per call instead of caching a record at start-up.
/// </para>
/// </summary>
internal static class StoredSettings
{
    public static string? String(
        IReadOnlyDictionary<string, string> stored,
        string key,
        string? configured)
    {
        if (stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public static bool Boolean(
        IReadOnlyDictionary<string, string> stored,
        string key,
        bool configured) =>
        stored.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : configured;

    public static int Integer(
        IReadOnlyDictionary<string, string> stored,
        string key,
        int configured) =>
        stored.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : configured;

    public static int? NullableInteger(
        IReadOnlyDictionary<string, string> stored,
        string key,
        int? configured)
    {
        if (stored.TryGetValue(key, out var value))
        {
            // An empty stored value means "no dimension configured", which is different from "not set here".
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        return configured;
    }

    /// <summary>
    /// A stored username that has been cleared reads as "no username", not as an empty one: a relay that needs none
    /// must be able to lose the one it had.
    /// </summary>
    public static string? Username(
        IReadOnlyDictionary<string, string> stored,
        string key,
        string? configured) =>
        stored.TryGetValue(key, out var value)
            ? string.IsNullOrWhiteSpace(value) ? null : value.Trim()
            : configured;

    /// <summary>An <c>HH:mm</c> local time, stored as text because that is what the admin page edits.</summary>
    public static TimeOnly TimeOfDay(
        IReadOnlyDictionary<string, string> stored,
        string key,
        TimeOnly configured) =>
        stored.TryGetValue(key, out var value) &&
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : configured;

    /// <summary>
    /// How the SMTP connection is protected, with the boolean it replaced still honoured.
    /// <para>
    /// Source precedence first (settings table, then deployment configuration, then the default), and within one
    /// source the newer key wins over the older flag — an instance that saved "use STARTTLS" before implicit TLS
    /// existed keeps sending without anyone touching the page, and an instance configured from compose with
    /// <c>Smtp__UseStartTls=true</c> keeps working too. An unreadable value never throws: it falls through to the
    /// next source, because a typo in an environment variable must not stop the notification job from starting.
    /// </para>
    /// </summary>
    public static SmtpSecurity Security(
        IReadOnlyDictionary<string, string> stored,
        string securityKey,
        string legacyKey,
        string? configuredSecurity,
        bool? configuredLegacy,
        SmtpSecurity fallback)
    {
        if (stored.TryGetValue(securityKey, out var storedToken) &&
            SmtpSecurityNames.TryParse(storedToken, out var fromSettings))
        {
            return fromSettings;
        }

        if (stored.TryGetValue(legacyKey, out var storedFlag) && bool.TryParse(storedFlag, out var fromStoredFlag))
        {
            return fromStoredFlag ? SmtpSecurity.StartTls : SmtpSecurity.None;
        }

        if (SmtpSecurityNames.TryParse(configuredSecurity, out var fromConfiguration))
        {
            return fromConfiguration;
        }

        return configuredLegacy is { } flag ? flag ? SmtpSecurity.StartTls : SmtpSecurity.None : fallback;
    }

    /// <summary>A ratio or threshold. Invariant culture, so a stored "0.35" cannot be read as 35 on a comma locale.</summary>
    public static double Decimal(
        IReadOnlyDictionary<string, string> stored,
        string key,
        double configured) =>
        stored.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : configured;
}
