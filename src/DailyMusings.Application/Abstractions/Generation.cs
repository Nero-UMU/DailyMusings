using System.Text.Json;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// The article-generation endpoint's configuration (docs/开发指导.md §8.1). Secrets are referenced by
/// <em>name</em> only and resolved at call time (§10.4), exactly like the transcription endpoint.
/// </summary>
public sealed record GenerationSettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    string SecretName,
    TimeSpan Timeout,
    string PromptVersion)
{
    /// <summary>Off until an operator configures an endpoint: nothing is sent anywhere by default.</summary>
    public static GenerationSettings Default { get; } = new(
        Enabled: false,
        BaseUrl: "https://api.deepseek.com",
        Model: "deepseek-flash",
        SecretName: "deepseek-api-key",
        Timeout: TimeSpan.FromMinutes(3),

        // v2 added the topic instruction: the model is now asked to pick the day's topics from the existing
        // vocabulary, or to propose short new names when nothing fits. The version travels with every version
        // row, so a draft written under the old instruction is still identifiable as such (§6.4).
        PromptVersion: "generation-v2");
}

public interface IGenerationSettingsProvider
{
    Task<GenerationSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One citation as the model reported it: the quoted text plus the material it came from.
/// <para>
/// Deliberately no character offsets. §6.5 needs a paragraph index, a half-open range and a hash of the exact
/// text, and models produce offsets that are wrong by a few characters often enough to be untrustworthy — in a
/// feature whose job is to let a user verify a sentence, highlighting the wrong one is worse than highlighting
/// none. The offsets are derived from the draft text by
/// <see cref="DailyMusings.Domain.Reflections.Sources.SourceQuoteLocator"/>; a quote that cannot be found is
/// dropped rather than approximated.
/// </para>
/// </summary>
public sealed record GeneratedCitation(
    string Quote,
    IReadOnlyList<InputEntryId> InputIds,
    double Relevance,
    string Reason);

/// <summary>Structured output of one generation call (§8.4: 要求输出结构化正文及来源映射).</summary>
/// <param name="Topics">
/// Topic names the model chose <em>from the list it was given</em>. Names rather than ids on purpose: the model
/// is asked to echo a label it can see, and identifiers it cannot verify are identifiers it will invent.
/// </param>
/// <param name="NewTopics">
/// Topic names the model proposes because the existing vocabulary had nothing that fitted (§6.2 as revised).
/// Kept in a separate field rather than mixed into <paramref name="Topics"/> so that "this day is about a theme
/// you already track" and "this day needs a new theme" stay distinguishable in the stored result — the second
/// is a claim the user should be able to review.
/// </param>
public sealed record GeneratedDraft(
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    IReadOnlyList<GeneratedCitation> Citations,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> NewTopics);

/// <summary>Everything a generation run is allowed to know.</summary>
/// <param name="DayInputs">The day's own inputs, already in capture order.</param>
/// <param name="HistoricalMaterial">Selected past material, oldest first, each flagged as historical.</param>
/// <param name="KnownTopics">
/// The topic vocabulary the model may pick from (§6.2 as revised). Supplied as names because that is what the
/// prompt can carry; the mapping back to real topics happens afterwards, in the application layer, so a model
/// that returns a name nobody has ever used cannot quietly create a topic that no rule approved.
/// </param>
public sealed record GenerationRequest(
    ContentDate ContentDate,
    IReadOnlyList<InputEntry> DayInputs,
    IReadOnlyList<RetrievedMaterial> HistoricalMaterial,
    WritingSettings Settings,
    string PromptVersion,
    IReadOnlyList<string> KnownTopics);

/// <summary>
/// Writes a day's reflection against an OpenAI-compatible endpoint. Implementations must never log the prompt
/// or the response (§16).
/// </summary>
public interface IReflectionGenerationClient
{
    Task<GeneratedDraft> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken);
}

/// <summary>Input to the second-stage check (§8.4).</summary>
public sealed record UnsourcedCheckRequest(
    ContentDate ContentDate,
    string Body,
    IReadOnlyList<InputEntry> DayInputs,
    IReadOnlyList<RetrievedMaterial> HistoricalMaterial);

/// <summary>
/// A sentence the checker could not trace to any input, reported as the quoted text for the same reason
/// citations are: the offsets are ours to compute, not the model's to guess.
/// </summary>
public sealed record UnsourcedFinding(string Quote, string Reason);

/// <summary>
/// The unsourced-statement check (§8.4). Its findings are warnings the user must see, never a block: they may
/// still publish after confirming.
/// </summary>
public interface IUnsourcedStatementChecker
{
    Task<IReadOnlyList<UnsourcedFinding>> CheckAsync(
        UnsourcedCheckRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// History-retrieval tuning (§8.3: 默认最多返回十条高相关材料，数量可配置).
/// <para>
/// <see cref="CandidateScanLimit"/> bounds the degraded full-text path, which scores candidates in process
/// rather than in SQL. A personal instance holds thousands of entries, so this is a guard rail against a
/// pathological instance rather than a limit anyone should reach.
/// </para>
/// </summary>
public sealed record RetrievalSettings(int MaxMaterials, int CandidateScanLimit, double MinimumRelevance, double MinimumLexicalScore)
{
    public static RetrievalSettings Default { get; } = new(
        MaxMaterials: RetrievalLimits.Default.MaxMaterials,
        CandidateScanLimit: 500,
        MinimumRelevance: RetrievalLimits.Default.MinimumRelevance,
        MinimumLexicalScore: RetrievalLimits.Default.MinimumLexicalScore);

    public RetrievalLimits ToLimits() => new(MaxMaterials, MinimumRelevance, MinimumLexicalScore);
}

public interface IRetrievalSettingsProvider
{
    Task<RetrievalSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The parameters a persisted generation job carries, so a user decision survives a restart with the job.
/// </summary>
public sealed record ReflectionGenerationPayload(
    bool AllowOverwriteOfManualEdits,
    GenerationReason Reason,
    bool IgnoreTranscriptionFailures,
    WritingSettings? Settings = null)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Reads a stored payload. A malformed one is treated as "no explicit decisions".</summary>
    public static ReflectionGenerationPayload FromJson(string? json)
    {
        var fallback = new ReflectionGenerationPayload(false, GenerationReason.Scheduled, false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<ReflectionGenerationPayload>(json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
