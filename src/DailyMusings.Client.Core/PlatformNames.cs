namespace DailyMusings.Client.Core;

/// <summary>
/// The platform name this client reports to the server (docs/开发指导.md §10.2).
/// <para>
/// Two strings, one meaning: MAUI names the runtime platform <c>Android</c>, <c>WinUI</c>, <c>iOS</c> or
/// <c>MacCatalyst</c>, while the server stores the platform a device paired from and the admin page shows it.
/// The Android client is the only one that ships, so <c>android</c> is the only value this build ever sends —
/// the mapping lives here rather than inside the MAUI project so that statement is testable, since the app
/// project is not referenced by the test project.
/// </para>
/// </summary>
public static class PlatformNames
{
    /// <summary>The server-facing name for a runtime platform name, or <c>"unknown"</c> if it is one we do not know.</summary>
    public static string FromRuntimeName(string? runtimeName) => runtimeName?.Trim().ToLowerInvariant() switch
    {
        "android" => "android",
        "winui" or "windows" => "windows",
        "ios" => "ios",
        "macos" or "maccatalyst" => "macos",
        _ => "unknown",
    };
}
