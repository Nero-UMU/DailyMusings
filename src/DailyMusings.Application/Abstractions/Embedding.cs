using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DailyMusings.Domain.Common;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// The identity of an embedding configuration: model name, dimensions and base URL (docs/开发指导.md §8.3).
/// <para>
/// This exists because vectors from two different models are not comparable, and mixing them would produce
/// confident nonsense rather than an obvious error. §8.3 makes that explicit: the index is bound to this
/// fingerprint, and changing the model invalidates the whole index at once so retrieval falls back to topics
/// and full text until a rebuild finishes (decision A.8).
/// </para>
/// </summary>
public sealed record EmbeddingConfigVersion(string Fingerprint, string Model, int? Dimensions, string BaseUrl)
{
    /// <summary>
    /// Derives the fingerprint. The base URL is part of it on purpose: two hosts serving a model with the same
    /// name may serve different weights, so they are not interchangeable.
    /// </summary>
    public static EmbeddingConfigVersion From(string baseUrl, string model, int? dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var trimmedModel = model.Trim();
        var material = string.Join(
            '\n',
            NormalizeUrl(baseUrl),
            trimmedModel,
            dimensions?.ToString(CultureInfo.InvariantCulture) ?? "-");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new EmbeddingConfigVersion(Convert.ToHexStringLower(hash), trimmedModel, dimensions, baseUrl.Trim());
    }

    private static string NormalizeUrl(string baseUrl) =>
        baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
}

/// <summary>
/// The optional embedding endpoint (§8.1 item 3). §3.1 keeps it optional: with it switched off the product
/// still works, using topic tags and full text for history retrieval.
/// </summary>
public sealed record EmbeddingSettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    string SecretName,
    TimeSpan Timeout,
    int? Dimensions,
    int BatchSize)
{
    public static EmbeddingSettings Default { get; } = new(
        Enabled: false,
        BaseUrl: "https://api.openai.com/v1",
        Model: "text-embedding-3-small",
        SecretName: "embedding-api-key",
        Timeout: TimeSpan.FromMinutes(2),
        Dimensions: null,
        BatchSize: 32);

    /// <summary>The fingerprint this configuration produces; compare it with the indexed version.</summary>
    public EmbeddingConfigVersion ResolveConfigVersion() =>
        EmbeddingConfigVersion.From(BaseUrl, Model, Dimensions);
}

public interface IEmbeddingSettingsProvider
{
    Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken);
}

public sealed record EmbeddingRequest(IReadOnlyList<string> Inputs, string Model, int? Dimensions);

/// <summary>One vector, at the index it had in the request's input list.</summary>
public sealed record EmbeddingResult(int Index, float[] Vector);

public interface IEmbeddingClient
{
    Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Whether semantic retrieval is usable right now (decision A.8).
/// <para>
/// It has to be a queryable state rather than something probed per search: §8.3 requires the client to be told
/// "语义检索重建中", and §16 requires that state to stay out of the basic health check, because an unavailable
/// embedding service must not make the instance look unhealthy.
/// </para>
/// </summary>
public sealed record SemanticSearchState(
    bool Enabled,
    bool Available,
    string? ConfiguredVersion,
    string? IndexedVersion)
{
    /// <summary>Enabled, configured, and not yet matching the index: the rebuild §8.3 describes.</summary>
    public bool Rebuilding => Enabled && !Available;

    public static SemanticSearchState Disabled { get; } = new(false, false, null, null);
}

public interface IEmbeddingIndexState
{
    /// <summary>The fingerprint the stored index was completely built for, or <c>null</c> when there is none.</summary>
    Task<string?> GetIndexedVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that the index for a fingerprint is complete. This single write is the switch A.8 describes:
    /// until it lands, retrieval keeps using the previously indexed version.
    /// </summary>
    Task MarkIndexedAsync(string configVersion, CancellationToken cancellationToken);
}
