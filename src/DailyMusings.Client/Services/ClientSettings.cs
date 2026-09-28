namespace DailyMusings.Client.Services;

/// <summary>
/// The few settings the client keeps on the device (docs/开发指导.md §9.3).
/// <para>
/// Only the server address lives here. The device token is a credential and goes to the platform's secure storage
/// instead (§10.2) — the two are deliberately not kept together.
/// </para>
/// </summary>
public sealed class ClientSettings
{
    private const string ServerBaseUrlKey = "server.baseUrl";

    public string? ServerBaseUrl
    {
        get
        {
            var value = Preferences.Default.Get<string?>(ServerBaseUrlKey, null);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Preferences.Default.Remove(ServerBaseUrlKey);
                return;
            }

            Preferences.Default.Set(ServerBaseUrlKey, value.Trim());
        }
    }

    public bool IsConfigured => ResolveBaseUri() is not null;

    /// <summary>Parses the configured address, or returns <c>null</c> when it is missing or unusable.</summary>
    public Uri? ResolveBaseUri()
    {
        if (ServerBaseUrl is not { } url)
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        // A base address must end in a slash or relative paths would replace the last segment.
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
