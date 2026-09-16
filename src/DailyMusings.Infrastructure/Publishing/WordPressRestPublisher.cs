using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Publishing;

/// <summary>
/// Publishes to WordPress through its own REST API with an Application Password (docs/开发指导.md §11.1).
/// <para>
/// Three things this class is careful about. It never logs the post body, the title or the credential — §16 keeps
/// content out of the default log, and an unpublished draft is the most private thing the product holds. It
/// classifies every failure as transient or permanent, because that is what §14's retry budget acts on. And it
/// always addresses an article it created by its remote id, so a retry updates that article instead of creating
/// a second one (§17.2: WordPress 重试不产生重复文章).
/// </para>
/// <para>
/// The site is a parameter of every call rather than state on the client. One client serves every configured
/// target, and a long-lived singleton holding "the site I am currently talking to" would be a data race the first
/// time two publications ran at once.
/// </para>
/// </summary>
public sealed class WordPressRestPublisher : IRemotePublisher
{
    private const string PostsPath = "wp-json/wp/v2/posts";

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly ILogger<WordPressRestPublisher> _logger;

    public WordPressRestPublisher(
        HttpClient httpClient,
        ISecretStore secrets,
        ILogger<WordPressRestPublisher> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<RemoteArticle> CreateAsync(
        WordPressSite site,
        RemoteArticleDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(draft);

        // Adopt before creating. WordPress has no idempotency key, so a POST whose response was lost looks exactly
        // like a POST that never arrived — and a blind retry would leave two drafts of the same day on the site.
        // Slugs are unique per post type, so a post already carrying this slug is the one this request created.
        if (!string.IsNullOrWhiteSpace(draft.Slug) &&
            await FindBySlugAsync(site, draft.Slug!, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            _logger.LogInformation("Adopted an existing article with the same slug rather than creating a second one.");
            return await UpdateAsync(site, existing.RemoteId, draft, cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendAsync(
            site,
            HttpMethod.Post,
            BuildUri(site, PostsPath),
            new PostBody(draft.Title, draft.Content, Status(draft), draft.Slug),
            cancellationToken).ConfigureAwait(false);

        return await ReadArticleAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteArticle> UpdateAsync(
        WordPressSite site,
        string remoteId,
        RemoteArticleDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        using var response = await SendAsync(
            site,
            HttpMethod.Post,
            BuildUri(site, $"{PostsPath}/{Uri.EscapeDataString(remoteId)}"),
            new PostBody(draft.Title, draft.Content, Status(draft), draft.Slug),
            cancellationToken).ConfigureAwait(false);

        return await ReadArticleAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an article back with <c>context=edit</c>, which is what makes the comparison in §11.1 meaningful:
    /// the edited context returns the raw title and content we sent, whereas the default returns rendered HTML
    /// that would never hash to the same value.
    /// </summary>
    public async Task<RemoteArticle?> GetAsync(
        WordPressSite site,
        string remoteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        var uri = new Uri(
            $"{BuildUri(site, $"{PostsPath}/{Uri.EscapeDataString(remoteId)}")}?context=edit",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = BasicAuth(site, RequireSecret(site));

        using var response = await SendRawAsync(site, request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleted on the site. Not an error: the caller reports the divergence as unverifiable rather than
            // pretending there is something to compare.
            return null;
        }

        Classify(response);

        return await ReadArticleAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static string Status(RemoteArticleDraft draft) => draft.IsDraft ? "draft" : "publish";

    /// <summary>
    /// Looks for a post already carrying this slug, in any status. Returns the first match, or <c>null</c>.
    /// </summary>
    private async Task<RemoteArticle?> FindBySlugAsync(
        WordPressSite site,
        string slug,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{BuildUri(site, PostsPath)}?slug={Uri.EscapeDataString(slug)}&status=any&context=edit&per_page=1",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = BasicAuth(site, RequireSecret(site));

        using var response = await SendRawAsync(site, request, cancellationToken).ConfigureAwait(false);
        Classify(response);

        List<PostPayload>? payload;

        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<List<PostPayload>>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                "publication.malformed_response",
                "The WordPress site returned a response that could not be read.");
        }

        var match = payload?.FirstOrDefault();

        return match is null || match.Id <= 0
            ? null
            : new RemoteArticle(
                match.Id.ToString(CultureInfo.InvariantCulture),
                match.Title?.Raw ?? match.Title?.Rendered ?? string.Empty,
                match.Content?.Raw ?? match.Content?.Rendered ?? string.Empty,
                match.Status ?? string.Empty,
                match.Link,
                ParseModified(match.ModifiedGmt));
    }

    private async Task<HttpResponseMessage> SendAsync(
        WordPressSite site,
        HttpMethod method,
        Uri uri,
        PostBody body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = BasicAuth(site, RequireSecret(site));

        using (request)
        {
            return await SendRawAsync(site, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        WordPressSite site,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(site.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientExternalFailureException(
                "publication.timeout",
                "The WordPress site did not answer in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientExternalFailureException(
                "publication.network",
                "The WordPress site could not be reached.",
                exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Only the status code is logged; never the post body or the credentials (§16).
            _logger.LogWarning("WordPress answered {StatusCode} while publishing.", (int)response.StatusCode);
        }

        return response;
    }

    private string RequireSecret(WordPressSite site) =>
        _secrets.TryGet(site.SecretName)
        ?? throw new PermanentExternalFailureException(
            "publication.secret_missing",
            $"The secret '{site.SecretName}' is not provisioned.");

    /// <summary>
    /// Turns an HTTP status into §14's two buckets. The distinction is the whole reason this adapter classifies
    /// at all: a throttled site should be retried in a minute, and rejected credentials never will be.
    /// </summary>
    private static void Classify(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PermanentExternalFailureException(
                "publication.credentials_rejected",
                "The WordPress site rejected the configured application password.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PermanentExternalFailureException(
                "publication.remote_not_found",
                "The WordPress site no longer has that article.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            throw new TransientExternalFailureException(
                "publication.upstream_unavailable",
                "The WordPress site is temporarily unavailable.");
        }

        throw new PermanentExternalFailureException(
            "publication.request_rejected",
            "The WordPress site rejected the request.");
    }

    private static async Task<RemoteArticle> ReadArticleAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Classify(response);

        PostPayload? payload;

        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<PostPayload>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PermanentExternalFailureException(
                "publication.malformed_response",
                "The WordPress site returned a response that could not be read.");
        }

        if (payload is null || payload.Id <= 0)
        {
            throw new PermanentExternalFailureException(
                "publication.malformed_response",
                "The WordPress response did not name the article it created.");
        }

        return new RemoteArticle(
            payload.Id.ToString(CultureInfo.InvariantCulture),
            payload.Title?.Raw ?? payload.Title?.Rendered ?? string.Empty,
            payload.Content?.Raw ?? payload.Content?.Rendered ?? string.Empty,
            payload.Status ?? string.Empty,
            payload.Link,
            ParseModified(payload.ModifiedGmt));
    }

    private static DateTimeOffset? ParseModified(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private static Uri BuildUri(WordPressSite site, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site.BaseUrl);
        return new Uri($"{site.BaseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }

    private static AuthenticationHeaderValue BasicAuth(WordPressSite site, string applicationPassword) =>
        new(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{site.Username}:{applicationPassword}")));

    private sealed record PostBody(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("slug")] string? Slug);

    private sealed record PostPayload(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("modified_gmt")] string? ModifiedGmt,
        [property: JsonPropertyName("title")] RenderedField? Title,
        [property: JsonPropertyName("content")] RenderedField? Content);

    private sealed record RenderedField(
        [property: JsonPropertyName("raw")] string? Raw,
        [property: JsonPropertyName("rendered")] string? Rendered);
}
