using System.Globalization;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Application.Configuration;

/// <summary>Which of the three model endpoints docs/开发指导.md §8.1 asks an instance to configure.</summary>
public enum ModelService
{
    Transcription = 0,
    Generation = 1,
    Embedding = 2,
}

/// <summary>
/// One model endpoint as the admin page shows it: everything §8.1 lists — Base URL, model name, the secret's
/// <em>name</em>, timeout and whether it is on — plus the embedding dimension when that is meaningful, and
/// where the credential actually comes from.
/// </summary>
public sealed record ModelEndpointView(
    string Service,
    bool Enabled,
    string BaseUrl,
    string Model,
    string SecretName,
    int TimeoutSeconds,
    int? Dimensions,
    SecretSource PasswordSource)
{
    /// <summary>
    /// Whether a key resolves at all. Reported rather than inferred: "配置好了但发不出去" and "还没填密钥" are
    /// different problems, and an operator looking at a switched-on endpoint needs to know which one they have.
    /// </summary>
    public bool HasPassword => PasswordSource != SecretSource.None;
}

/// <summary>
/// What an administrator may change. Every field is optional: a page that toggles 启用 should not have to echo the
/// rest back, and a field left out is left alone.
/// <para>
/// <c>Password</c> is write-only and mirrors the SMTP form: a model key that can only be provisioned by editing
/// compose and recreating the container is a key most instances will never set. The value is encrypted at rest
/// outside the instance root, so it still never reaches the settings table, an export or a backup (§10.4).
/// </para>
/// </summary>
public sealed record ModelEndpointUpdate(
    bool? Enabled,
    string? BaseUrl,
    string? Model,
    string? SecretName,
    int? TimeoutSeconds,
    int? Dimensions,
    string? Password = null,
    bool? ClearPassword = null);

/// <summary>
/// The one place the model endpoints' stored settings live (docs/开发指导.md §8.1, §10.4).
/// <para>
/// Values are written to the settings table; the deployment configuration stays as the fallback, so an instance
/// configured entirely from compose keeps working and an instance configured from the admin page keeps working after
/// a redeploy. Only the secret's <em>name</em> is ever stored — §10.4's rule that a value never leaves the secret
/// store is what makes this safe to expose in a web form.
/// </para>
/// </summary>
public static class ModelSettingKeys
{
    public static string Prefix(ModelService service) => service switch
    {
        ModelService.Transcription => "model.transcription",
        ModelService.Generation => "model.generation",
        _ => "model.embedding",
    };

    public static string Enabled(ModelService service) => Prefix(service) + ".enabled";
    public static string BaseUrl(ModelService service) => Prefix(service) + ".baseUrl";
    public static string Model(ModelService service) => Prefix(service) + ".model";
    public static string SecretName(ModelService service) => Prefix(service) + ".secretName";
    public static string TimeoutSeconds(ModelService service) => Prefix(service) + ".timeoutSeconds";
    public static string Dimensions(ModelService service) => Prefix(service) + ".dimensions";
}

/// <summary>
/// Reads the effective settings for one endpoint: what the admin page stored, else the deployment configuration,
/// else the built-in default.
/// </summary>
public sealed class ReadModelEndpointsUseCase
{
    private readonly ITranscriptionSettingsProvider _transcription;
    private readonly IGenerationSettingsProvider _generation;
    private readonly IEmbeddingSettingsProvider _embedding;
    private readonly ISecretStore _secrets;

    public ReadModelEndpointsUseCase(
        ITranscriptionSettingsProvider transcription,
        IGenerationSettingsProvider generation,
        IEmbeddingSettingsProvider embedding,
        ISecretStore secrets)
    {
        _transcription = transcription;
        _generation = generation;
        _embedding = embedding;
        _secrets = secrets;
    }

    public async Task<IReadOnlyList<ModelEndpointView>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var transcription = await _transcription.GetAsync(cancellationToken).ConfigureAwait(false);
        var generation = await _generation.GetAsync(cancellationToken).ConfigureAwait(false);
        var embedding = await _embedding.GetAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            new ModelEndpointView(
                "transcription",
                transcription.Enabled,
                transcription.BaseUrl,
                transcription.Model,
                transcription.SecretName,
                (int)transcription.Timeout.TotalSeconds,
                null,
                Resolve(transcription.SecretName)),
            new ModelEndpointView(
                "generation",
                generation.Enabled,
                generation.BaseUrl,
                generation.Model,
                generation.SecretName,
                (int)generation.Timeout.TotalSeconds,
                null,
                Resolve(generation.SecretName)),
            new ModelEndpointView(
                "embedding",
                embedding.Enabled,
                embedding.BaseUrl,
                embedding.Model,
                embedding.SecretName,
                (int)embedding.Timeout.TotalSeconds,
                embedding.Dimensions,
                Resolve(embedding.SecretName)),
        ];

        // A name that cannot be resolved as a secret name at all reads as "not provisioned" rather than throwing:
        // the admin page is exactly where a broken configuration should be visible, not a 500.
        SecretSource Resolve(string name) =>
            string.IsNullOrWhiteSpace(name) ? SecretSource.None : _secrets.ResolveSource(name);
    }
}

/// <summary>
/// Writes one endpoint's settings, refusing anything the client would later fail on.
/// <para>
/// The validation is deliberately the same shape as the deployment configuration's expectations: an absolute HTTP(S)
/// URL, a model name that exists, a secret <em>name</em> that is a plain file name (it is resolved under the secrets
/// directory — a name with a slash in it would be a path, §10.4), a timeout inside the range the retry policy can
/// live with, and dimensions only where they mean something.
/// </para>
/// </summary>
public sealed class UpdateModelEndpointUseCase
{
    private const int MinimumTimeoutSeconds = 5;
    private const int MaximumTimeoutSeconds = 600;

    private readonly IAppSettingStore _settings;
    private readonly IUiSecretStore _uiSecrets;

    public UpdateModelEndpointUseCase(IAppSettingStore settings, IUiSecretStore uiSecrets)
    {
        _settings = settings;
        _uiSecrets = uiSecrets;
    }

    public async Task ExecuteAsync(
        ModelService service,
        ModelEndpointUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (update.BaseUrl is { } baseUrl)
        {
            await SetAsync(ModelSettingKeys.BaseUrl(service), ValidateBaseUrl(baseUrl), cancellationToken).ConfigureAwait(false);
        }

        if (update.Model is { } model)
        {
            await SetAsync(ModelSettingKeys.Model(service), ValidateModel(model), cancellationToken).ConfigureAwait(false);
        }

        if (update.SecretName is { } secretName)
        {
            await SetAsync(ModelSettingKeys.SecretName(service), ValidateSecretName(secretName), cancellationToken).ConfigureAwait(false);
        }

        // The key is stored under the endpoint's own secret name, so "which file would this have come from" and
        // "what did the operator just type" are the same key. Resolving the name means reading the settings back
        // when the request did not carry one.
        if (update.Password is not null || update.ClearPassword == true)
        {
            var target = update.SecretName ?? await ReadSecretNameAsync(service, cancellationToken).ConfigureAwait(false);

            if (update.ClearPassword == true)
            {
                await _uiSecrets.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
            }

            if (update.Password is { } password)
            {
                if (password.Length is 0 or > 4096)
                {
                    throw new UseCaseException("model.password.invalid", "密钥不能为空，也不能超过 4096 个字符。");
                }

                await _uiSecrets.SetAsync(target, password, cancellationToken).ConfigureAwait(false);
            }
        }

        if (update.TimeoutSeconds is { } timeout)
        {
            await SetAsync(
                ModelSettingKeys.TimeoutSeconds(service),
                ValidateTimeout(timeout).ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }

        if (update.Dimensions is { } dimensions)
        {
            if (service != ModelService.Embedding)
            {
                throw new UseCaseException(
                    "model.dimensions.not_applicable",
                    "Only the embedding endpoint has a dimension.");
            }

            await SetAsync(
                ModelSettingKeys.Dimensions(service),
                ValidateDimensions(dimensions).ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }

        if (update.Enabled is { } enabled)
        {
            await SetAsync(
                ModelSettingKeys.Enabled(service),
                enabled ? "true" : "false",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SetAsync(string key, string value, CancellationToken cancellationToken) =>
        _settings.SetAsync(key, value, cancellationToken);

    /// <summary>
    /// The secret name this endpoint currently resolves to. Read from the settings table (which is what the admin
    /// page writes) and falling back to the built-in default, so a password typed before the name was ever set
    /// still lands under the name the model clients will look up.
    /// </summary>
    private async Task<string> ReadSecretNameAsync(ModelService service, CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var key = ModelSettingKeys.SecretName(service);

        if (stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return service switch
        {
            ModelService.Transcription => TranscriptionSettings.Default.SecretName,
            ModelService.Generation => GenerationSettings.Default.SecretName,
            _ => EmbeddingSettings.Default.SecretName,
        };
    }

    private static string ValidateBaseUrl(string value)
    {
        var trimmed = value.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new UseCaseException(
                "model.baseUrl.invalid",
                "Base URL 需要是完整的 http:// 或 https:// 地址。");
        }

        // A trailing slash is stripped rather than kept: every client appends its own path, and "…/v1/" + "/chat"
        // is the kind of double slash some providers answer with a 404.
        return trimmed.TrimEnd('/');
    }

    private static string ValidateModel(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is 0 or > 200)
        {
            throw new UseCaseException("model.name.invalid", "模型名不能为空，也不能超过 200 个字符。");
        }

        return trimmed;
    }

    private static string ValidateSecretName(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is 0 or > 100 ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed is "." or "..")
        {
            throw new UseCaseException(
                "model.secret_name.invalid",
                "Secret 名只能是文件名（不含路径分隔符），值本身放在 Secrets 目录里，不经过管理页。");
        }

        return trimmed;
    }

    private static int ValidateTimeout(int seconds) =>
        seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds
            ? throw new UseCaseException(
                "model.timeout.out_of_range",
                $"超时需要介于 {MinimumTimeoutSeconds} 与 {MaximumTimeoutSeconds} 秒之间。")
            : seconds;

    private static int ValidateDimensions(int dimensions) =>
        dimensions is < 1 or > 4096
            ? throw new UseCaseException("model.dimensions.out_of_range", "Embedding 维度需要介于 1 与 4096 之间。")
            : dimensions;
}
