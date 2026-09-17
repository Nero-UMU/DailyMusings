namespace DailyMusings.Client.Core;

/// <summary>
/// The platform name this client reports to the server (docs/开发指导.md §10.2).
/// <para>
/// Two strings, one meaning. MAUI names the runtime platform <c>WinUI</c>, <c>Android</c>, <c>iOS</c>,
/// <c>MacCatalyst</c>; the server stores the platform a device paired from, and the admin page shows it. Until a
/// Windows client was actually run, the client hard-coded <c>"android"</c> for everyone — so a PC appeared in the
/// device list as an Android phone. Keeping the mapping here (rather than inside the MAUI project) is what makes it
/// testable: the app project is not referenced by the test project, and that is exactly how the hard-coded value
/// survived four phases.
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
