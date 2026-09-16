using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DailyMusings.Infrastructure.Generation;

/// <summary>
/// Reads the model endpoint configuration from the deployment configuration (docs/开发指导.md §8.1).
/// <para>
/// All three endpoints stay disabled until an operator turns them on, so a fresh instance never sends the user's
/// private material anywhere. Secrets are never read here: only the secret's <em>name</em> is, and the value is
/// resolved at call time (§10.4).
/// </para>
/// <para>
/// The admin-page editor for these values belongs to the phase that owns model configuration; until then the
/// environment is the configuration surface, and the shape is already the one an editor would write.
/// </para>
/// </summary>
public sealed class ConfigurationGenerationSettingsProvider : IGenerationSettingsProvider
{
    public const string SectionName = "Generation";

    private readonly GenerationSettings _settings;

    public ConfigurationGenerationSettingsProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = GenerationSettings.Default;

        _settings = new GenerationSettings(
            Enabled: section.GetValue("Enabled", defaults.Enabled),
            BaseUrl: section.GetValue("BaseUrl", defaults.BaseUrl) ?? defaults.BaseUrl,
            Model: section.GetValue("Model", defaults.Model) ?? defaults.Model,
            SecretName: section.GetValue("SecretName", defaults.SecretName) ?? defaults.SecretName,
            Timeout: TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 180)),
            PromptVersion: section.GetValue("PromptVersion", defaults.PromptVersion) ?? defaults.PromptVersion);
    }

    public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_settings);
}

/// <summary>
/// Reads the optional embedding endpoint's configuration (§8.1 item 3). Disabled by default: §3.1 keeps
/// embeddings optional, and the product works without them by falling back to topics and full text (§8.3).
/// </summary>
public sealed class ConfigurationEmbeddingSettingsProvider : IEmbeddingSettingsProvider
{
    public const string SectionName = "Embedding";

    private readonly EmbeddingSettings _settings;

    public ConfigurationEmbeddingSettingsProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = EmbeddingSettings.Default;

        _settings = new EmbeddingSettings(
            Enabled: section.GetValue("Enabled", defaults.Enabled),
            BaseUrl: section.GetValue("BaseUrl", defaults.BaseUrl) ?? defaults.BaseUrl,
            Model: section.GetValue("Model", defaults.Model) ?? defaults.Model,
            SecretName: section.GetValue("SecretName", defaults.SecretName) ?? defaults.SecretName,
            Timeout: TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", 120)),
            Dimensions: section.GetValue<int?>("Dimensions"),
            BatchSize: Math.Clamp(section.GetValue("BatchSize", defaults.BatchSize), 1, 256));
    }

    public Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_settings);
}

/// <summary>
/// Retrieval tuning (§8.3: 默认最多返回十条高相关材料，数量可配置).
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
