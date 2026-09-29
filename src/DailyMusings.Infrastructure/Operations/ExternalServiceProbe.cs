using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Transcription;
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
    private readonly ILogger<ExternalServiceProbe> _logger;
    private readonly RemoteCertificateValidationCallback? _trustServer;

    /// <param name="trustServer">
    /// Certificate validation for the SMTP probe. Null — what the container passes — means the platform's own
    /// validation, which is the only sane default; the parameter exists so a relay behind a private CA can be trusted
    /// explicitly, and so the test can point the probe at a throwaway certificate. Same seam as the transport's.
    /// </param>
    public ExternalServiceProbe(
        HttpClient httpClient,
        ISecretStore secrets,
        ITranscriptionSettingsProvider transcription,
        IGenerationSettingsProvider generation,
        IEmbeddingSettingsProvider embedding,
        ISmtpSettingsProvider smtp,
        ILogger<ExternalServiceProbe> logger,
        RemoteCertificateValidationCallback? trustServer = null)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _transcription = transcription;
        _generation = generation;
        _embedding = embedding;
        _smtp = smtp;
        _logger = logger;
        _trustServer = trustServer;
    }

    public async Task<ProbeResult> ProbeAsync(ExternalService service, CancellationToken cancellationToken)
    {
        var result = service switch
        {
            ExternalService.Transcription => await ProbeConfiguredTranscriptionAsync(cancellationToken).ConfigureAwait(false),

            ExternalService.Generation => await ProbeConfiguredModelAsync(
                ExternalService.Generation,
                (await _generation.GetAsync(cancellationToken).ConfigureAwait(false)) is { Enabled: true } g ? (g.BaseUrl, g.Model, g.SecretName) : null,
                cancellationToken).ConfigureAwait(false),

            ExternalService.Embedding => await ProbeConfiguredModelAsync(
                ExternalService.Embedding,
                (await _embedding.GetAsync(cancellationToken).ConfigureAwait(false)) is { Enabled: true } e ? (e.BaseUrl, e.Model, e.SecretName) : null,
                cancellationToken).ConfigureAwait(false),

            ExternalService.Smtp => await ProbeSmtpAsync(cancellationToken).ConfigureAwait(false),
            _ => ProbeResult.Failure("probe.unknown_service", "That is not a service this instance talks to."),
        };

        // The outcome and the code only: a probe never logs what it sent or what came back (§16).
        _logger.LogInformation("Test connection for {Service} reported {Outcome}.", service, result.Code);

        return result;
    }

    public async Task<ProbeResult> ProbeModelAsync(
        ModelEndpointProbeRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Service switch
        {
            ExternalService.Transcription => "transcription",
            ExternalService.Generation => "generation",
            ExternalService.Embedding => "embedding",
            _ => null,
        };

        if (name is null)
        {
            return ProbeResult.Failure("probe.unknown_service", "That is not a model service.");
        }

        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return ProbeResult.Failure($"probe.{name}.url_invalid", "Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return ProbeResult.Failure($"probe.{name}.model_missing", "请填写要测试的模型名。");
        }

        var secret = string.IsNullOrWhiteSpace(request.ApiKey)
            ? _secrets.TryGet(request.SecretName)
            : request.ApiKey.Trim();

        if (string.IsNullOrWhiteSpace(secret))
        {
            return ProbeResult.Failure($"probe.{name}.secret_missing", "请填写 API Key，或先保存一个可用的 Key。");
        }

        if (request.Service == ExternalService.Transcription)
        {
            var apiType = request.ApiType?.Trim().ToLowerInvariant();
            if (!TranscriptionApiTypes.IsSupported(apiType))
            {
                return ProbeResult.Failure(
                    "probe.transcription.api_type_invalid",
                    "请选择 openai_transcription、openai_chat_audio 或 dashscope_async。");
            }

            if (apiType == TranscriptionApiTypes.DashScopeAsync)
            {
                return ProbeResult.Failure(
                    "probe.transcription.public_audio_url_required",
                    "dashscope_async 需要公网可访问的音频 URL；当前测试录音只有本地文件，不能伪装成 URL 提交。");
            }
        }

        using var httpRequest = await BuildModelProbeRequestAsync(request, baseUri, secret, cancellationToken)
            .ConfigureAwait(false);
        var result = await SendAsync(
            name,
            httpRequest,
            $"已实际调用 {request.Model}，接口与密钥均可用。",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Test candidate connection for {Service} reported {Outcome}.", request.Service, result.Code);
        return result;
    }

    private async Task<ProbeResult> ProbeConfiguredModelAsync(
        ExternalService service,
        (string BaseUrl, string Model, string SecretName)? settings,
        CancellationToken cancellationToken)
    {
        var name = service.ToString().ToLowerInvariant();
        if (settings is not { } config)
        {
            return ProbeResult.Failure($"probe.{name}.disabled", $"No {name} endpoint is configured.");
        }

        return await ProbeModelAsync(
            new ModelEndpointProbeRequest(service, config.BaseUrl, config.Model, config.SecretName),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeConfiguredTranscriptionAsync(CancellationToken cancellationToken)
    {
        var settings = await _transcription.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return ProbeResult.Failure("probe.transcription.disabled", "No transcription endpoint is configured.");
        }

        return await ProbeModelAsync(
            new ModelEndpointProbeRequest(
                ExternalService.Transcription,
                settings.BaseUrl,
                settings.Model,
                settings.SecretName,
                ApiType: settings.ApiType),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult> SendAsync(
        string name,
        HttpRequestMessage request,
        string successDetail,
        CancellationToken cancellationToken)
    {
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

    private static async Task<HttpRequestMessage> BuildModelProbeRequestAsync(
        ModelEndpointProbeRequest request,
        Uri baseUri,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var root = baseUri.ToString().TrimEnd('/');
        if (request.Service == ExternalService.Transcription)
        {
            var settings = TranscriptionSettings.Default with
            {
                Enabled = true,
                BaseUrl = root,
                Model = request.Model,
                ApiType = request.ApiType!,
            };
            var audio = CreateProbeWave();
            return await TranscriptionHttpAdapter.BuildRequestAsync(
                new TranscriptionRequest(
                    _ => Task.FromResult<Stream>(new MemoryStream(audio, writable: false)),
                    "connection-test.wav",
                    "audio/wav"),
                settings,
                apiKey,
                cancellationToken).ConfigureAwait(false);
        }

        HttpRequestMessage message;
        if (request.Service == ExternalService.Generation)
        {
            message = new HttpRequestMessage(HttpMethod.Post, $"{root}/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = request.Model,
                    messages = new[] { new { role = "user", content = "Reply with OK." } },
                    max_tokens = 2,
                    stream = false,
                }),
            };
        }
        else
        {
            message = new HttpRequestMessage(HttpMethod.Post, $"{root}/embeddings")
            {
                Content = JsonContent.Create(new { model = request.Model, input = "connection test" }),
            };
        }

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    private static byte[] CreateProbeWave()
    {
        const int sampleRate = 8_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = 800;
        var dataSize = sampleCount * channels * bitsPerSample / 8;

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Connects to the SMTP server and reads its greeting. No message is sent: a test connection that mailed
    /// something would put a meaningless message in the user's inbox every time they pressed the button.
    /// <para>
    /// The two switches are not symmetric here, and pretending otherwise is what makes this probe lie: with SSL on
    /// (465) the relay says nothing at all until the handshake is done, so reading a plaintext greeting would just
    /// sit there until the timeout and report a working mailbox as unreachable. With STARTTLS on (587) the greeting
    /// is in the clear by definition, so the probe reads exactly what it always read — and it still proves only that
    /// the relay is there, which is why the page tells the operator to use 「发送测试邮件」 for the real question.
    /// </para>
    /// </summary>
    private async Task<ProbeResult> ProbeSmtpAsync(CancellationToken cancellationToken)
    {
        var settings = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            return ProbeResult.Failure("probe.smtp.disabled", "No SMTP server is configured.");
        }

        // A password is not required and not checked: a relay on localhost authenticates nobody, so a missing secret
        // is not a misconfiguration here. Whether the relay accepts whatever password is stored belongs to a real
        // send, and §12 keeps that failure inside the notification job.
        using var client = new TcpClient();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);

            Stream stream = client.GetStream();

            if (settings.UseSsl)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false, _trustServer);

                await secure.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = settings.Host,
                        EnabledSslProtocols = SslProtocols.None,
                    },
                    timeout.Token).ConfigureAwait(false);

                stream = secure;
            }

            var buffer = new byte[256];

            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var greeting = Encoding.ASCII.GetString(buffer, 0, read).Trim();

            return greeting.StartsWith("220", StringComparison.Ordinal)
                ? ProbeResult.Success($"The SMTP server greeted with {greeting[..3]}.")
                : ProbeResult.Failure("probe.smtp.unexpected_greeting", "Something answered, but not with an SMTP greeting.");
        }
        catch (AuthenticationException exception)
        {
            // Permanent by nature: the certificate or the protocol version will not change on its own.
            return ProbeResult.Failure("probe.smtp.tls_failed", $"TLS failed: {exception.Message}");
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
