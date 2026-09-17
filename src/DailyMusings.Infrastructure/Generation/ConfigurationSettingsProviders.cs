using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Generation;

/// <summary>
/// Reads the model endpoint configuration (docs/开发指导.md §8.1).
/// <para>
/// Values come from the settings table first — the admin page writes them there — and fall back to the deployment
/// configuration and then to the built-in default, so an instance can be configured either way. Secrets are never
/// read here: only the secret's <em>name</em> is, and the value is resolved at call time (§10.4).
/// </para>
/// <para>
/// Every read goes to storage rather than to a record captured at start-up, because an administrator who saves a new
/// model name expects the next generation to use it, not the next restart.
/// </para>
/// </summary>
public sealed class ConfigurationGenerationSettingsProvider : IGenerationSettingsProvider
{
    public const string SectionName = "Generation";

    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public ConfigurationGenerationSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<GenerationSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var section = _configuration.GetSection(SectionName);
        var defaults = GenerationSettings.Default;
        var service = ModelService.Generation;

        return new GenerationSettings(
            Enabled: StoredSettings.Boolean(
                stored,
                ModelSettingKeys.Enabled(service),
                section.GetValue("Enabled", defaults.Enabled)),
            BaseUrl: StoredSettings.String(
                stored,
                ModelSettingKeys.BaseUrl(service),
                section.GetValue("BaseUrl", defaults.BaseUrl)) ?? defaults.BaseUrl,
            Model: StoredSettings.String(
                stored,
                ModelSettingKeys.Model(service),
                section.GetValue("Model", defaults.Model)) ?? defaults.Model,
            SecretName: StoredSettings.String(
                stored,
                ModelSettingKeys.SecretName(service),
                section.GetValue("SecretName", defaults.SecretName)) ?? defaults.SecretName,
            Timeout: TimeSpan.FromSeconds(StoredSettings.Integer(
                stored,
                ModelSettingKeys.TimeoutSeconds(service),
                section.GetValue("TimeoutSeconds", (int)defaults.Timeout.TotalSeconds))),
            PromptVersion: section.GetValue("PromptVersion", defaults.PromptVersion) ?? defaults.PromptVersion);
    }
}

/// <summary>
/// Reads the optional embedding endpoint's configuration (§8.1 item 3). Disabled by default: §3.1 keeps embeddings
/// optional, and the product works without them by falling back to topics and full text (§8.3).
/// </summary>
public sealed class ConfigurationEmbeddingSettingsProvider : IEmbeddingSettingsProvider
{
    public const string SectionName = "Embedding";

    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public ConfigurationEmbeddingSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var section = _configuration.GetSection(SectionName);
        var defaults = EmbeddingSettings.Default;
        var service = ModelService.Embedding;

        return new EmbeddingSettings(
            Enabled: StoredSettings.Boolean(
                stored,
                ModelSettingKeys.Enabled(service),
                section.GetValue("Enabled", defaults.Enabled)),
            BaseUrl: StoredSettings.String(
                stored,
                ModelSettingKeys.BaseUrl(service),
                section.GetValue("BaseUrl", defaults.BaseUrl)) ?? defaults.BaseUrl,
            Model: StoredSettings.String(
                stored,
                ModelSettingKeys.Model(service),
                section.GetValue("Model", defaults.Model)) ?? defaults.Model,
            SecretName: StoredSettings.String(
                stored,
                ModelSettingKeys.SecretName(service),
                section.GetValue("SecretName", defaults.SecretName)) ?? defaults.SecretName,
            Timeout: TimeSpan.FromSeconds(StoredSettings.Integer(
                stored,
                ModelSettingKeys.TimeoutSeconds(service),
                section.GetValue("TimeoutSeconds", (int)defaults.Timeout.TotalSeconds))),
            Dimensions: StoredSettings.NullableInteger(
                stored,
                ModelSettingKeys.Dimensions(service),
                section.GetValue<int?>("Dimensions")),
            BatchSize: Math.Clamp(section.GetValue("BatchSize", defaults.BatchSize), 1, 256));
    }
}

/// <summary>
/// Retrieval tuning (§8.3: 默认最多返回十条高相关材料，数量可配置). Deployment configuration only: §8.1's admin editor
/// covers the three endpoints, and these are tuning knobs rather than endpoints.
/// </summary>
public sealed class ConfigurationRetrievalSettingsProvider : IRetrievalSettingsProvider
{
    public const string SectionName = "Retrieval";

    private readonly RetrievalSettings _settings;

    public ConfigurationRetrievalSettingsProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = RetrievalSettings.Default;

        _settings = new RetrievalSettings(
            MaxMaterials: Math.Clamp(section.GetValue("MaxMaterials", defaults.MaxMaterials), 1, 100),
            CandidateScanLimit: Math.Clamp(section.GetValue("CandidateScanLimit", defaults.CandidateScanLimit), 1, 20_000),
            MinimumRelevance: section.GetValue("MinimumRelevance", defaults.MinimumRelevance),
            MinimumLexicalScore: section.GetValue("MinimumLexicalScore", defaults.MinimumLexicalScore));
    }

    public Task<RetrievalSettings> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_settings);
}
