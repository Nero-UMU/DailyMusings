using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Retrieval;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// A clock the test owns. Phase three is almost entirely time-dependent — the slot, the catch-up scan, the
/// content-day boundary — so every test states its own "now" rather than waiting for one.
/// </summary>
internal sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }
}

internal sealed class TestContentProvider : IContentSettingsProvider, IContentCalendarProvider
{
    public TestContentProvider(ContentSettings settings) => Settings = settings;

    public ContentSettings Settings { get; set; }

    public Task<ContentSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task<ContentCalendar> GetCalendarAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Settings.CreateCalendar());
}

internal sealed class TestGenerationSettings : IGenerationSettingsProvider
{
    public GenerationSettings Settings { get; set; } = GenerationSettings.Default with
    {
        Enabled = true,
        Model = "test-writer",
        PromptVersion = "generation-test-v1",
    };

    public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}

internal sealed class TestEmbeddingSettings : IEmbeddingSettingsProvider
{
    public EmbeddingSettings Settings { get; set; } = EmbeddingSettings.Default with { Enabled = false };

    public Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}

internal sealed class TestRetrievalSettings : IRetrievalSettingsProvider
{
    public RetrievalSettings Settings { get; set; } = RetrievalSettings.Default;

    public Task<RetrievalSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}

/// <summary>
/// A controllable stand-in for the article endpoint (§17.2: external services must use test doubles).
/// <para>
/// It returns a draft whose citations quote text it actually writes, because the source map is built by locating
/// those quotes in the body — a double that invented offsets would not exercise the real path.
/// </para>
/// </summary>
internal sealed class FakeGenerationClient : IReflectionGenerationClient, IUnsourcedStatementChecker
{
    public int GenerateCalls { get; private set; }

    public int CheckCalls { get; private set; }

    public GenerationRequest? LastRequest { get; private set; }

    /// <summary>Sentences the fake check will report as untraceable. Set to empty for a clean result.</summary>
    public List<string> SuspiciousSentences { get; } = [];

    /// <summary>A sentence the draft should contain so a test can cite or flag it.</summary>
    public string ExtraParagraph { get; set; } = string.Empty;

    public Func<GenerationRequest, GeneratedDraft>? Override { get; set; }

    public Task<GeneratedDraft> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        GenerateCalls++;
        LastRequest = request;

        if (Override is not null)
        {
            return Task.FromResult(Override(request));
        }

        var body = string.IsNullOrWhiteSpace(ExtraParagraph)
            ? "今天试着记录了一点东西。\n\n说到底，只是想把这些话留下来。"
            : $"今天试着记录了一点东西。\n\n{ExtraParagraph}";

        var firstInput = request.DayInputs.FirstOrDefault();

        var citations = firstInput is null
            ? []
            : new List<GeneratedCitation>
            {
                new("今天试着记录了一点东西。", [firstInput.Id], 0.9, "与当天记录一致"),
            };

        if (!string.IsNullOrWhiteSpace(ExtraParagraph) && firstInput is not null)
        {
            citations.Add(new GeneratedCitation(ExtraParagraph, [firstInput.Id], 0.6, "与当天记录相关"));
        }

        return Task.FromResult(new GeneratedDraft(
            "今天的记录",
            "把今天的话留下来。",
            body,
            ["记录"],
            ["随想"],
            citations));
    }

    public Task<IReadOnlyList<UnsourcedFinding>> CheckAsync(
        UnsourcedCheckRequest request,
        CancellationToken cancellationToken)
    {
        CheckCalls++;

        return Task.FromResult<IReadOnlyList<UnsourcedFinding>>(
            SuspiciousSentences
                .Select(sentence => new UnsourcedFinding(sentence, "素材里没有提到这件事"))
                .ToArray());
    }
}

/// <summary>An embedding endpoint double whose vectors are chosen by the test, so cosine scores are predictable.</summary>
internal sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public Func<string, float[]> VectorFor { get; set; } = _ => [1f, 0f, 0f];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(
            request.Inputs
                .Select((text, index) => new EmbeddingResult(index, VectorFor(text)))
                .ToArray());
    }
}

/// <summary>
/// Real repositories over a real SQLite file, plus doubles for everything external. Phase three's rules span the
/// domain, the use cases and storage at once — "the source map survives a topic merge" is only a real statement
/// if the rows are real — so these tests use the actual schema.
/// </summary>
internal sealed class ReflectionTestContext : IAsyncDisposable
{
    private ReflectionTestContext(
        TestDatabase database,
        TestClock clock,
        TestContentProvider content,
        TestGenerationSettings generation,
        TestEmbeddingSettings embedding,
        TestRetrievalSettings retrieval,
        FakeGenerationClient client,
        FakeEmbeddingClient embeddings)
    {
        Database = database;
        Clock = clock;
        Content = content;
        Generation = generation;
        Embedding = embedding;
        Retrieval = retrieval;
        Client = client;
        EmbeddingClient = embeddings;

        var accessor = database.Accessor;
        Inputs = new SqliteInputEntryRepository(accessor);
        Jobs = new SqliteJobRepository(accessor);
        Reflections = new SqliteReflectionRepository(accessor);
        Topics = new SqliteTopicRepository(accessor);
        EmbeddingIndex = new SqliteEmbeddingIndexRepository(accessor);
        Settings = new SqliteAppSettingStore(accessor, clock);
        UnitOfWork = new SqliteUnitOfWork(accessor);
        IndexState = new AppSettingEmbeddingIndexState(Settings);
        Enqueuer = new Application.Jobs.JobEnqueuer(Jobs, clock);

        SemanticState = new Application.Embeddings.GetSemanticSearchStateUseCase(embedding, IndexState);

        ThisRetrieval = new HistoryRetrievalUseCase(
            Inputs,
            Topics,
            EmbeddingIndex,
            embedding,
            retrieval,
            SemanticState);

        RequestGeneration = new RequestReflectionGenerationUseCase(
            Inputs,
            Reflections,
            content,
            content,
            clock,
            Enqueuer);

        Generate = new GenerateReflectionUseCase(
            Inputs,
            Reflections,
            client,
            generation,
            content,
            UnitOfWork,
            clock,
            ThisRetrieval,
            Enqueuer);

        Check = new RunUnsourcedStatementCheckUseCase(Reflections, Inputs, client, clock);
        GetReflection = new GetReflectionUseCase(Reflections, ThisRetrieval);
        Confirm = new ConfirmReflectionUseCase(Reflections, clock, GetReflection);
        SwitchVersion = new SwitchReflectionVersionUseCase(Reflections, clock, GetReflection);
        EditVersion = new EditReflectionVersionUseCase(Reflections, UnitOfWork, clock, GetReflection);
        GetSources = new GetReflectionSourcesUseCase(Reflections, ThisRetrieval);
        AssignTopics = new Application.Topics.AssignInputTopicsUseCase(Inputs, Topics);
        MergeTopics = new Application.Topics.MergeTopicsUseCase(Topics, UnitOfWork, clock);
        CreateTopic = new Application.Topics.CreateTopicUseCase(Topics, clock);
        AssignTopicsAutomatically = new Application.Topics.AssignTopicsAutomaticallyUseCase(Inputs, Topics);
        EmbedInput = new Application.Embeddings.EmbedInputUseCase(Inputs, embedding, embeddings, EmbeddingIndex, clock);
        EnsureIndexed = new Application.Embeddings.EnsureEmbeddingIndexedUseCase(embedding, Enqueuer);
        RebuildIndex = new Application.Embeddings.RebuildEmbeddingIndexUseCase(
            embedding,
            embeddings,
            EmbeddingIndex,
            IndexState,
            clock);
        IngestText = new Application.Inputs.IngestTextInputUseCase(
            Inputs,
            Jobs,
            UnitOfWork,
            content,
            Reflections,
            AssignTopicsAutomatically,
            EnsureIndexed,
            clock);
    }

    public TestDatabase Database { get; }

    public TestClock Clock { get; }

    public TestContentProvider Content { get; }

    public TestGenerationSettings Generation { get; }

    public TestEmbeddingSettings Embedding { get; }

    public TestRetrievalSettings Retrieval { get; }

    public FakeGenerationClient Client { get; }

    public FakeEmbeddingClient EmbeddingClient { get; }

    public SqliteInputEntryRepository Inputs { get; }

    public SqliteJobRepository Jobs { get; }

    public SqliteReflectionRepository Reflections { get; }

    public SqliteTopicRepository Topics { get; }

    public SqliteEmbeddingIndexRepository EmbeddingIndex { get; }

    public SqliteAppSettingStore Settings { get; }

    public SqliteUnitOfWork UnitOfWork { get; }

    public AppSettingEmbeddingIndexState IndexState { get; }

    public Application.Jobs.JobEnqueuer Enqueuer { get; }

    public Application.Embeddings.GetSemanticSearchStateUseCase SemanticState { get; }

    public HistoryRetrievalUseCase ThisRetrieval { get; }

    public RequestReflectionGenerationUseCase RequestGeneration { get; }

    public GenerateReflectionUseCase Generate { get; }

    public RunUnsourcedStatementCheckUseCase Check { get; }

    public GetReflectionUseCase GetReflection { get; }

    public ConfirmReflectionUseCase Confirm { get; }

    public SwitchReflectionVersionUseCase SwitchVersion { get; }

    public EditReflectionVersionUseCase EditVersion { get; }

    public GetReflectionSourcesUseCase GetSources { get; }

    public Application.Topics.AssignInputTopicsUseCase AssignTopics { get; }

    public Application.Topics.MergeTopicsUseCase MergeTopics { get; }

    public Application.Topics.CreateTopicUseCase CreateTopic { get; }

    public Application.Topics.AssignTopicsAutomaticallyUseCase AssignTopicsAutomatically { get; }

    public Application.Embeddings.EmbedInputUseCase EmbedInput { get; }

    public Application.Embeddings.EnsureEmbeddingIndexedUseCase EnsureIndexed { get; }

    public Application.Embeddings.RebuildEmbeddingIndexUseCase RebuildIndex { get; }

    public Application.Inputs.IngestTextInputUseCase IngestText { get; }

    /// <summary>2026-03-10T04:00:00Z — 12:00 in Asia/Shanghai, comfortably inside the content day.</summary>
    public static DateTimeOffset DefaultNow { get; } = new(2026, 3, 10, 4, 0, 0, TimeSpan.Zero);

    public ContentCalendar Calendar => Content.Settings.CreateCalendar();

    public ContentDate Today => Calendar.ContentDateOf(Clock.UtcNow);

    public static async Task<ReflectionTestContext> CreateAsync(
        DateTimeOffset? now = null,
        ContentSettings? content = null)
    {
        var database = await TestDatabase.CreateAsync();
        var clock = new TestClock(now ?? DefaultNow);

        return new ReflectionTestContext(
            database,
            clock,
            new TestContentProvider(content ?? ContentSettings.Default),
            new TestGenerationSettings(),
            new TestEmbeddingSettings(),
            new TestRetrievalSettings(),
            new FakeGenerationClient(),
            new FakeEmbeddingClient());
    }

    /// <summary>Captures a text entry through the real ingestion path, so staleness marking is exercised too.</summary>
    public async Task<InputEntry> CaptureTextAsync(
        string text,
        ContentDate? contentDate = null,
        DateTimeOffset? capturedAtUtc = null,
        string? idempotencyKey = null)
    {
        var capturedAt = capturedAtUtc ?? (contentDate is { } day
            ? Calendar.AtLocalTime(day, new TimeOnly(12, 0))
            : Clock.UtcNow);

        var result = await IngestText.ExecuteAsync(
            text,
            new Application.Inputs.CaptureContext(
                capturedAt,
                (int)TimeSpan.FromHours(8).TotalMinutes,
                idempotencyKey,
                DeviceId: null),
            CancellationToken.None);

        return result.Entry;
    }

    /// <summary>Writes a draft directly, for tests that need a pre-existing state rather than a generated one.</summary>
    public async Task<(Reflection Reflection, ReflectionVersion Version)> SeedDraftAsync(
        ContentDate contentDate,
        ReflectionStatus status,
        bool workingVersionHasManualEdits = false)
    {
        var reflection = Reflection.Create(ReflectionId.New(), contentDate, GenerationReason.Scheduled, Clock.UtcNow);
        reflection.MarkReady(Clock.UtcNow);
        reflection.BeginGeneration(GenerationReason.Scheduled, Clock.UtcNow);

        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflection.Id,
            "标题",
            "摘要",
            "第一段内容。\n\n第二段内容。",
            WritingSettings.Default,
            new ModelInfo("test-writer"),
            "generation-test-v1",
            Clock.UtcNow,
            tags: ["记录"],
            categories: ["随想"]);

        if (workingVersionHasManualEdits)
        {
            version.Edit("标题", "摘要", "我自己改过的正文。", Clock.UtcNow);
        }

        reflection.ApplyGeneratedVersion(version.Id, false, false, Clock.UtcNow);

        while (reflection.Status != status)
        {
            switch (status)
            {
                case ReflectionStatus.ReviewRequired:
                    throw new InvalidOperationException($"Cannot move a fresh draft to {status}.");

                case ReflectionStatus.Confirmed:
                    reflection.Confirm(version.Id, Clock.UtcNow);
                    break;

                case ReflectionStatus.StaleByLateInput:
                    reflection.MarkStaleByLateInput(Clock.UtcNow);
                    break;

                default:
                    throw new InvalidOperationException($"Seeding {status} is not supported.");
            }
        }

        // One transaction, like the real write path: the draft and its versions reference each other, so the
        // foreign keys can only be satisfied as a whole.
        await using var transaction = await UnitOfWork.BeginAsync(CancellationToken.None);

        await Reflections.AddAsync(reflection, CancellationToken.None);
        await Reflections.AddVersionAsync(version, CancellationToken.None);
        await Reflections.ReplaceSourcesAsync(
            version.Id,
            [
                SourceReference.Create(
                    SourceReferenceId.New(),
                    version.Id,
                    0,
                    0,
                    5,
                    "第一段内容",
                    (await Inputs.ListByContentDateAsync(contentDate, CancellationToken.None)).FirstOrDefault()?.Id
                        ?? InputEntryId.New(),
                    0.9,
                    "与当天记录一致",
                    isHistorical: false),
            ],
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return (reflection, version);
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
