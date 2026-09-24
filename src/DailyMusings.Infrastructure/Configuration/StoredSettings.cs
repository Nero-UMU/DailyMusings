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

    /// <summary>
    /// A boolean from the deployment configuration, or null when it is absent or unreadable.
    /// <para>
    /// Read as text and parsed here rather than with <c>GetValue&lt;bool?&gt;</c> on purpose: the binder throws on a
    /// value it cannot convert, and a typo in an environment variable must not stop the notification job from
    /// starting. Unreadable means "not configured", which falls through to the next source — the same rule the old
    /// <c>Security</c> token followed.
    /// </para>
    /// </summary>
    public static bool? ConfiguredBoolean(string? value) => bool.TryParse(value, out var parsed) ? parsed : null;

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
    /// Implicit TLS (465), with the keys it replaced still honoured.
    /// <para>
    /// Source precedence first (settings table, then deployment configuration, then the default), and within one
    /// source the newer key wins over the older spelling. Two deployments must keep working without anyone touching
    /// anything: one saved from the admin page before the two checkboxes existed — <c>smtp.security=ssl</c> — and one
    /// configured from compose in either old spelling, <c>Smtp__Security=ssl</c> or <c>Smtp__UseStartTls=false</c>.
    /// An unreadable value never throws: it falls through to the next source, because a typo in an environment
    /// variable must not stop the notification job from starting.
    /// </para>
    /// </summary>
    public static bool Ssl(
        IReadOnlyDictionary<string, string> stored,
        string key,
        bool? configured,
        string? configuredLegacySecurity,
        bool fallback)
    {
        if (stored.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (LegacySecurity(Stored(stored, LegacySecurityKey)) is { } storedLegacy)
        {
            return storedLegacy.Ssl;
        }

        if (configured is { } fromConfiguration)
        {
            return fromConfiguration;
        }

        // The last resort: a deployment that only ever set the old name. ("none" is a real answer here, and it means
        // both switches are off; the fallback only applies to a value nobody can read.)
        return LegacySecurity(configuredLegacySecurity) is { } configuredLegacy ? configuredLegacy.Ssl : fallback;
    }

    /// <summary>
    /// Explicit TLS (587), with both keys it replaced still honoured — <c>smtp.security</c> / <c>Smtp__Security</c>
    /// and the boolean that came before it, <c>smtp.useStartTls</c> / <c>Smtp__UseStartTls</c>. Same precedence rules
    /// as <see cref="Ssl"/>: stored before configured, new key before old spelling, unreadable values ignored.
    /// </summary>
    public static bool StartTls(
        IReadOnlyDictionary<string, string> stored,
        string key,
        bool? configured,
        bool? configuredLegacyUseStartTls,
        string? configuredLegacySecurity,
        bool fallback)
    {
        if (stored.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (LegacySecurity(Stored(stored, LegacySecurityKey)) is { } storedLegacy)
        {
            return storedLegacy.StartTls;
        }

        if (stored.TryGetValue(LegacyUseStartTlsKey, out var storedFlag) && bool.TryParse(storedFlag, out var fromStoredFlag))
        {
            return fromStoredFlag;
        }

        if (configured is { } fromConfiguration)
        {
            return fromConfiguration;
        }

        if (configuredLegacyUseStartTls is { } fromLegacyConfiguration)
        {
            return fromLegacyConfiguration;
        }

        return LegacySecurity(configuredLegacySecurity) is { } configuredLegacy ? configuredLegacy.StartTls : fallback;
    }

    /// <summary>
    /// The old <c>security</c> spelling — <c>none</c>, <c>starttls</c> or <c>ssl</c> — as the two booleans that
    /// replaced it. Also accepts the words other software's configuration dumps use, because refusing <c>tls</c> or
    /// <c>implicit</c> would send an operator back to a form that cannot say what their old program said. Null means
    /// "not a value this understands", which is what makes a typo fall through instead of stopping the job.
    /// </summary>
    private static (bool Ssl, bool StartTls)? LegacySecurity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" or "off" or "false" or "plain" => (Ssl: false, StartTls: false),
        "starttls" or "starttlsrequired" or "explicit" => (Ssl: false, StartTls: true),
        "ssl" or "tls" or "implicit" or "smtps" or "true" => (Ssl: true, StartTls: false),
        _ => null,
    };

    /// <summary>A stored value, or null when it was never written or was written empty.</summary>
    private static string? Stored(IReadOnlyDictionary<string, string> stored, string key) =>
        stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// A ratio or threshold. Invariant culture, so a stored "0.35" cannot be read as 35 on a comma locale.
    /// </summary>
    public static double Decimal(
        IReadOnlyDictionary<string, string> stored,
        string key,
        double configured) =>
        stored.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : configured;

    /// <summary>
    /// The names the two encryption switches had before they were split in two. They are read here and nowhere else:
    /// everywhere else in the product speaks <c>smtp.ssl</c> and <c>smtp.starttls</c>, and the only reason these
    /// strings still exist is so an instance upgraded from the previous version keeps sending.
    /// </summary>
    private const string LegacySecurityKey = "smtp.security";

    private const string LegacyUseStartTlsKey = "smtp.useStartTls";
}
