using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Publishing;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Operations;

/// <summary>
/// The temporary debug mode, stored as two settings rows (docs/开发指导.md §16).
/// <para>
/// A row rather than a field on a service: it has to survive a restart, because an instance that came back up with
/// content logging silently on is exactly the failure this switch is supposed to prevent. The expiry is an absolute
/// instant, so nothing has to run for it to end — an expired switch is simply not enabled any more, whether or not
/// anybody noticed.
/// </para>
/// </summary>
public sealed class AppSettingDiagnosticMode : IDiagnosticMode
{
    public const string EnabledUntilKey = "diagnostics.enabledUntil";
    public const string EnabledByKey = "diagnostics.enabledBy";

    private readonly IAppSettingStore _settings;
    private readonly IClock _clock;

    public AppSettingDiagnosticMode(IAppSettingStore settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
    }

    public async Task<DiagnosticModeState> GetAsync(CancellationToken cancellationToken)
    {
        var values = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        if (!values.TryGetValue(EnabledUntilKey, out var raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until))
        {
            return DiagnosticModeState.Disabled;
        }

        if (until <= _clock.UtcNow)
        {
            // Expired but still on disk. Reported as off, and deliberately not cleaned up here: a read should not
            // write, and the row is harmless.
            return DiagnosticModeState.Disabled;
        }

        values.TryGetValue(EnabledByKey, out var enabledBy);

        return new DiagnosticModeState(true, until, enabledBy);
    }

    public async Task<DiagnosticModeState> EnableAsync(
        string enabledBy,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledBy);

        var until = _clock.UtcNow + duration;

        await _settings.SetAsync(EnabledUntilKey, until.ToString("o", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        await _settings.SetAsync(EnabledByKey, enabledBy.Trim(), cancellationToken).ConfigureAwait(false);

        return new DiagnosticModeState(true, until, enabledBy.Trim());
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        // Written as a past instant rather than deleted, so turning it off is one code path in both directions and a
        // half-written delete cannot leave the switch on.
        await _settings
            .SetAsync(EnabledUntilKey, _clock.UtcNow.AddSeconds(-1).ToString("o", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        await _settings.SetAsync(EnabledByKey, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsContentLoggingAllowedAsync(CancellationToken cancellationToken) =>
        (await GetAsync(cancellationToken).ConfigureAwait(false)).Enabled;
}

/// <summary>
/// Checks an external service on demand (docs/开发指导.md §16).
/// <para>
/// Every probe is a real, minimal call to the service itself rather than a configuration check: a base URL that
/// parses and a secret file that exists say nothing about whether the other end answers. What the probes deliberately
/// never do is send the user's content — a test that uploaded a recording or a draft in order to prove connectivity
/// would be a test that leaks the thing it is protecting.
/// </para>
/// </summary>
public sealed class ExternalServiceProbe : IExternalServiceProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly ITranscriptionSettingsProvider _transcription;
    private readonly IGenerationSettingsProvider _generation;
    private readonly IEmbeddingSettingsProvider _embedding;
    private readonly ISmtpSettingsProvider _smtp;
    private readonly IPublishTargetRepository _targets;
    private readonly IPublishDestinationProvider _destinations;
    private readonly ILogger<ExternalServiceProbe> _logger;

    public ExternalServiceProbe(
        HttpClient httpClient,
        ISecretStore secrets,
        ITranscriptionSettingsProvider transcription,
        IGenerationSettingsProvider generation,
        IEmbeddingSettingsProvider embedding,
        ISmtpSettingsProvider smtp,
        IPublishTargetRepository targets,
        IPublishDestinationProvider destinations,
        ILogger<ExternalServiceProbe> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _transcription = transcription;
        _generation = generation;
        _embedding = embedding;
        _smtp = smtp;
        _targets = targets;
        _destinations = destinations;
        _logger = logger;
    }

    public async Task<ProbeResult> ProbeAsync(ExternalService service, CancellationToken cancellationToken)
    {
        var result = service switch
        {
            ExternalService.Transcription => await ProbeModelAsync(
                "transcription",
                (await _transcription.GetAsync(cancellationToken).ConfigureAwait(false)) is { Enabled: true } t ? (t.BaseUrl, t.SecretName) : null,
                cancellationToken).ConfigureAwait(false),

            ExternalService.Generation => await ProbeModelAsync(
                "generation",
                (await _generation.GetAsync(cancellationToken).ConfigureAwait(false)) is { Enabled: true } g ? (g.BaseUrl, g.SecretName) : null,
                cancellationToken).ConfigureAwait(false),

            ExternalService.Embedding => await ProbeModelAsync(
                "embedding",
                (await _embedding.GetAsync(cancellationToken).ConfigureAwait(false)) is { Enabled: true } e ? (e.BaseUrl, e.SecretName) : null,
                cancellationToken).ConfigureAwait(false),

            ExternalService.Smtp => await ProbeSmtpAsync(cancellationToken).ConfigureAwait(false),
            ExternalService.WordPress => await ProbeWordPressAsync(cancellationToken).ConfigureAwait(false),
            _ => ProbeResult.Failure("probe.unknown_service", "That is not a service this instance talks to."),
        };

        // The outcome and the code only: a probe never logs what it sent or what came back (§16).
        _logger.LogInformation("Test connection for {Service} reported {Outcome}.", service, result.Code);

        return result;
    }

    private async Task<ProbeResult> ProbeModelAsync(
        string name,
        (string BaseUrl, string SecretName)? settings,
        CancellationToken cancellationToken)
    {
        if (settings is not { } config)
        {
            return ProbeResult.Failure($"probe.{name}.disabled", $"No {name} endpoint is configured.");
        }

        var secret = _secrets.TryGet(config.SecretName);
        if (secret is null)
        {
            return ProbeResult.Failure($"probe.{name}.secret_missing", $"The secret '{config.SecretName}' is not provisioned.");
        }

        // /models is the one endpoint every OpenAI-compatible service implements and that never costs a generation.
        var uri = new Uri($"{config.BaseUrl.TrimEnd('/')}/models", UriKind.Absolute);

        return await SendAsync(
            name,
            HttpMethod.Get,
            uri,
            new AuthenticationHeaderValue("Bearer", secret),
            "The endpoint answered.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeWordPressAsync(CancellationToken cancellationToken)
    {
        var targets = (await _targets.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(target => target.Type == PublishTargetType.WordPress)
            .ToArray();

        if (targets.Length == 0)
        {
            return ProbeResult.Failure("probe.wordpress.no_target", "No WordPress target is configured.");
        }

        foreach (var target in targets)
        {
            PublishDestination destination;

            try
            {
                destination = await _destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Domain.Common.DomainException exception)
            {
                return ProbeResult.Failure($"probe.wordpress.{exception.Code}", $"Target '{target.Name}' is misconfigured.");
            }

            var site = destination.RequireWordPress();
            var secret = _secrets.TryGet(site.SecretName);

            if (secret is null)
            {
                return ProbeResult.Failure("probe.wordpress.secret_missing", $"The secret '{site.SecretName}' is not provisioned.");
            }

            // Reading the authenticated user is the smallest call that proves both parts of the credential: the site
            // is there, and the application password is accepted.
            var uri = new Uri($"{site.BaseUrl.TrimEnd('/')}/wp-json/wp/v2/users/me", UriKind.Absolute);

            var credential = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{site.Username}:{secret}")));

            var result = await SendAsync(
                "wordpress",
                HttpMethod.Get,
                uri,
                credential,
                $"Target '{target.Name}' accepted the application password.",
                cancellationToken).ConfigureAwait(false);

            if (!result.Ok)
            {
                return result;
            }
        }

        return ProbeResult.Success($"All {targets.Length} WordPress target(s) accepted their credentials.");
    }

    private async Task<ProbeResult> SendAsync(
        string name,
        HttpMethod method,
        Uri uri,
        AuthenticationHeaderValue credential,
        string successDetail,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = credential;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ProbeResult.Success(successDetail);
            }

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    ProbeResult.Failure($"probe.{name}.credentials_rejected", "The service rejected the configured credentials."),

                System.Net.HttpStatusCode.NotFound =>
                    ProbeResult.Failure($"probe.{name}.endpoint_missing", "The service answered, but not the endpoint this instance expects."),

                _ => ProbeResult.Failure(
                    $"probe.{name}.rejected",
                    $"The service answered {(int)response.StatusCode}."),
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Failure($"probe.{name}.timeout", "The service did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return ProbeResult.Failure($"probe.{name}.unreachable", "The service could not be reached.");
        }
    }

    /// <summary>
    /// Connects to the SMTP server and reads its greeting. No message is sent: a test connection that mailed
    /// something would put a meaningless message in the user's inbox every time they pressed the button.
    /// </summary>
    private async Task<ProbeResult> ProbeSmtpAsync(CancellationToken cancellationToken)
    {
        var settings = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            return ProbeResult.Failure("probe.smtp.disabled", "No SMTP server is configured.");
        }

        // The password is checked for presence only. Whether the server accepts it belongs to a real send, and §12
        // keeps that failure inside the notification job.
        if (!string.IsNullOrWhiteSpace(settings.Username) && _secrets.TryGet(settings.SecretName) is null)
        {
            return ProbeResult.Failure("probe.smtp.secret_missing", $"The secret '{settings.SecretName}' is not provisioned.");
        }

        using var client = new TcpClient();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);

            using var stream = client.GetStream();
            var buffer = new byte[256];

            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var greeting = Encoding.ASCII.GetString(buffer, 0, read).Trim();

            return greeting.StartsWith("220", StringComparison.Ordinal)
                ? ProbeResult.Success($"The SMTP server greeted with {greeting[..3]}.")
                : ProbeResult.Failure("probe.smtp.unexpected_greeting", "Something answered, but not with an SMTP greeting.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Failure("probe.smtp.timeout", "The SMTP server did not answer in time.");
        }
        catch (SocketException)
        {
            return ProbeResult.Failure("probe.smtp.unreachable", "The SMTP server could not be reached.");
        }
    }
}
