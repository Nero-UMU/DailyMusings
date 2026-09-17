using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Notifications;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Notifications;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Notifications;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Publishing;
using DailyMusings.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// A remote site that records what it was asked to do.
/// <para>
/// It distinguishes create from update because that distinction is the whole of §17.2's "WordPress 重试不产生重复
/// 文章": a retry that calls create twice is the bug, and only a double that counts both calls can catch it.
/// </para>
/// </summary>
internal sealed class FakeRemotePublisher : IRemotePublisher
{
    private readonly Dictionary<string, RemoteArticle> _articles = new(StringComparer.Ordinal);
    private int _nextId = 100;

    public int CreateCalls { get; private set; }

    public int UpdateCalls { get; private set; }

    public List<string> WrittenContents { get; } = [];

    public List<string> WrittenTitles { get; } = [];

    public List<string> WrittenStatuses { get; } = [];

    /// <summary>Set to make the next call fail the way a real site can.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Set to edit the remote behind the product's back, for the divergence tests.</summary>
    public void EditRemotely(string remoteId, string title, string content)
    {
        if (_articles.TryGetValue(remoteId, out var article))
        {
            _articles[remoteId] = article with { Title = title, Content = content };
        }
    }

    public Task<RemoteArticle> CreateAsync(
        WordPressSite site,
        RemoteArticleDraft draft,
        CancellationToken cancellationToken)
    {
        CreateCalls++;
        ThrowIfFailing();
        Record(draft);

        var id = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var article = new RemoteArticle(id, draft.Title, draft.Content, draft.IsDraft ? "draft" : "publish", $"http://blog/{id}", null);
        _articles[id] = article;

        return Task.FromResult(article);
    }

    public Task<RemoteArticle> UpdateAsync(
        WordPressSite site,
        string remoteId,
        RemoteArticleDraft draft,
        CancellationToken cancellationToken)
    {
        UpdateCalls++;
        ThrowIfFailing();
        Record(draft);

        var article = new RemoteArticle(remoteId, draft.Title, draft.Content, draft.IsDraft ? "draft" : "publish", $"http://blog/{remoteId}", null);
        _articles[remoteId] = article;

        return Task.FromResult(article);
    }

    public Task<RemoteArticle?> GetAsync(WordPressSite site, string remoteId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(_articles.GetValueOrDefault(remoteId));
    }

    private void Record(RemoteArticleDraft draft)
    {
        WrittenTitles.Add(draft.Title);
        WrittenContents.Add(draft.Content);
        WrittenStatuses.Add(draft.IsDraft ? "draft" : "publish");
    }

    private void ThrowIfFailing()
    {
        if (FailWith is { } failure)
        {
            FailWith = null;
            throw failure;
        }
    }
}

/// <summary>A mail transport that records messages, and can be told to fail.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Exception? FailWith { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            FailWith = null;
            throw failure;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Notification settings the test owns.</summary>
internal sealed class TestNotificationSettings : INotificationSettingsProvider
{
    public NotificationSettings Settings { get; set; } = new(ToAddress: "owner@example.test", InstanceUrl: "http://instance.test")
    {
        Events = new Dictionary<NotificationEvent, bool>
        {
            [NotificationEvent.DraftReady] = true,
            [NotificationEvent.JobFailed] = true,
            [NotificationEvent.AutomaticPublication] = true,
        },
    };

    public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}

internal sealed class TestSmtpSettings : ISmtpSettingsProvider
{
    public SmtpSettings Settings { get; set; } = SmtpSettings.Default with { Enabled = true };

    public Task<SmtpSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}

/// <summary>
/// Real repositories, a real markdown directory, and doubles for everything that would leave the machine.
/// </summary>
internal sealed class PublishingTestContext : IAsyncDisposable
{
    private PublishingTestContext(
        TestDatabase database,
        TestClock clock,
        TestContentProvider content,
        TestNotificationSettings notifications,
        TestSmtpSettings smtp,
        FakeRemotePublisher remote,
        FakeEmailSender email,
        InstancePaths paths)
    {
        Database = database;
        Clock = clock;
        Content = content;
        Notifications = notifications;
        Smtp = smtp;
        Remote = remote;
        Email = email;
        Paths = paths;

        var accessor = database.Accessor;
        Inputs = new SqliteInputEntryRepository(accessor);
        Jobs = new SqliteJobRepository(accessor);
        Reflections = new SqliteReflectionRepository(accessor);
        Targets = new SqlitePublishTargetRepository(accessor);
        Publications = new SqlitePublicationRepository(accessor);
        UnitOfWork = new SqliteUnitOfWork(accessor);
        Enqueuer = new Application.Jobs.JobEnqueuer(Jobs, clock);
        Markdown = new FileMarkdownWriter(paths, NullLogger<FileMarkdownWriter>.Instance);

        // The destination provider needs the configuration surface; a Markdown target only needs the path, so an
        // empty configuration is honest here — the WordPress path is exercised by the integration tests.
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        // It also reads the per-target WordPress override from the settings table (§8.1). These tests only exercise
        // Markdown targets, so an empty store is the honest stand-in — the same construction the reflection context
        // uses.
        var appSettings = new DailyMusings.Infrastructure.Persistence.Repositories.SqliteAppSettingStore(accessor, clock);
        Destinations = new ConfigurationPublishDestinationProvider(
            configuration,
            paths,
            new DailyMusings.Infrastructure.Publishing.AppSettingWordPressSiteStore(appSettings));

        QueueNotifications = new QueueNotificationUseCase(notifications, smtp, Enqueuer);
        SendNotifications = new SendNotificationUseCase(email);

        Request = new RequestPublicationUseCase(Reflections, Targets, Publications, content, clock, Enqueuer);

        Run = new RunPublicationUseCase(
            Publications,
            Targets,
            Reflections,
            Destinations,
            remote,
            Markdown,
            content,
            clock,
            QueueNotifications);

        Check = new CheckRemoteUseCase(Publications, Targets, Reflections, Destinations, remote, Markdown, clock);

        Resolve = new ResolveRemoteDivergenceUseCase(
            Publications,
            Targets,
            Reflections,
            Destinations,
            remote,
            Markdown,
            Check,
            UnitOfWork,
            clock,
            Enqueuer);

        Retry = new RetryPublicationUseCase(Publications, clock, Enqueuer);

        Schedule = new SchedulePublicationsUseCase(
            Reflections,
            Targets,
            Publications,
            content,
            clock,
            Request,
            QueueNotifications);
    }

    public TestDatabase Database { get; }

    public TestClock Clock { get; }

    public TestContentProvider Content { get; }

    public TestNotificationSettings Notifications { get; }

    public TestSmtpSettings Smtp { get; }

    public FakeRemotePublisher Remote { get; }

    public FakeEmailSender Email { get; }

    public InstancePaths Paths { get; }

    public SqliteInputEntryRepository Inputs { get; }

    public SqliteJobRepository Jobs { get; }

    public SqliteReflectionRepository Reflections { get; }

    public SqlitePublishTargetRepository Targets { get; }

    public SqlitePublicationRepository Publications { get; }

    public SqliteUnitOfWork UnitOfWork { get; }

    public Application.Jobs.JobEnqueuer Enqueuer { get; }

    public FileMarkdownWriter Markdown { get; }

    public ConfigurationPublishDestinationProvider Destinations { get; }

    public QueueNotificationUseCase QueueNotifications { get; }

    public SendNotificationUseCase SendNotifications { get; }

    public RequestPublicationUseCase Request { get; }

    public RunPublicationUseCase Run { get; }

    public CheckRemoteUseCase Check { get; }

    public ResolveRemoteDivergenceUseCase Resolve { get; }

    public RetryPublicationUseCase Retry { get; }

    public SchedulePublicationsUseCase Schedule { get; }

    public DateOnly Today => DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);

    /// <summary>The calendar the content settings imply, which is what the publish slot is computed from.</summary>
    public ContentCalendar Calendar => Content.Settings.CreateCalendar();

    public static async Task<PublishingTestContext> CreateAsync(DateTimeOffset? now = null)
    {
        var database = await TestDatabase.CreateAsync();
        var clock = new TestClock(now ?? new DateTimeOffset(new DateOnly(2026, 3, 11), new TimeOnly(9, 0), TimeSpan.Zero));

        var paths = new InstancePaths(new StorageOptions
        {
            RootPath = database.RootPath,
            SecretsPath = Path.Combine(database.RootPath, "secrets"),
            KeyRingPath = Path.Combine(database.RootPath, "keys"),
        });

        paths.EnsureCreated();

        return new PublishingTestContext(
            database,
            clock,
            new TestContentProvider(ContentSettings.Default),
            new TestNotificationSettings(),
            new TestSmtpSettings(),
            new FakeRemotePublisher(),
            new FakeEmailSender(),
            paths);
    }

    /// <summary>Creates a target of either kind.</summary>
    public async Task<PublishTarget> AddTargetAsync(
        string name,
        PublishTargetType type = PublishTargetType.WordPress,
        string? destination = null,
        bool automatic = false)
    {
        var target = PublishTarget.Create(PublishTargetId.New(), name, type, destination);

        if (automatic)
        {
            target.EnableAutomaticPublish("owner", Clock.UtcNow);
        }

        await Targets.AddAsync(target, CancellationToken.None);
        return target;
    }

    /// <summary>Writes a confirmed draft for a day, which is the only state §11.1 allows publishing from.</summary>
    public async Task<(Reflection Reflection, ReflectionVersion Version)> SeedConfirmedDraftAsync(
        ContentDate contentDate,
        string title = "今天的记录",
        string body = "第一段。\n\n第二段。")
    {
        var reflection = Reflection.Create(ReflectionId.New(), contentDate, GenerationReason.Scheduled, Clock.UtcNow);
        reflection.MarkReady(Clock.UtcNow);
        reflection.BeginGeneration(GenerationReason.Scheduled, Clock.UtcNow);

        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflection.Id,
            title,
            "摘要",
            body,
            WritingSettings.Default,
            new ModelInfo("test-writer"),
            "generation-test-v1",
            Clock.UtcNow,
            tags: ["记录"],
            categories: ["随想"]);

        reflection.ApplyGeneratedVersion(version.Id, false, false, Clock.UtcNow);
        reflection.Confirm(version.Id, Clock.UtcNow);

        // One transaction, like the real write path: the draft and its version reference each other, so the
        // foreign keys can only be satisfied as a whole.
        await using var transaction = await UnitOfWork.BeginAsync(CancellationToken.None);

        await Reflections.AddAsync(reflection, CancellationToken.None);
        await Reflections.AddVersionAsync(version, CancellationToken.None);
        await Reflections.ReplaceSourcesAsync(version.Id, [], CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return (reflection, version);
    }

    public async Task<ProcessingJob> QueueAndRunAsync(Publication publication, bool replaceExistingFile = false)
    {
        var job = await Enqueuer.EnsureAsync(
            Domain.Jobs.JobType.Publication,
            publication.Id.ToString(),
            Domain.Jobs.IdempotencyKeys.Publication(
                publication.ReflectionVersionId,
                publication.PublishTargetId,
                publication.ExportRound),
            new PublicationPayload(replaceExistingFile).ToJson(),
            requeueFailed: false,
            CancellationToken.None);

        await Run.ExecuteAsync(publication.Id, new PublicationPayload(replaceExistingFile), CancellationToken.None);
        return job;
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
