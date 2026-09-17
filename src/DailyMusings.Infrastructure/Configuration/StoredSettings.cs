using System.Globalization;
using DailyMusings.Application.Abstractions;

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
}
