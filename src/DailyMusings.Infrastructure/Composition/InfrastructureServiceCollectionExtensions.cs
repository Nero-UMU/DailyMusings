using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Reflections;
using DailyMusings.Infrastructure.Diagnostics;
using DailyMusings.Infrastructure.Generation;
using DailyMusings.Infrastructure.Jobs;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Security;
using DailyMusings.Infrastructure.Storage;
using DailyMusings.Infrastructure.Time;
using DailyMusings.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DailyMusings.Infrastructure.Composition;

/// <summary>
/// The composition root for everything outside the domain: storage, SQLite, secrets, hashing and diagnostics.
/// <para>
/// Lifetimes are chosen deliberately. Singletons are stateless or process-wide (clock, hasher, secret store,
/// executor heartbeat). Everything holding a database connection is scoped, so a request or a job gets one
/// connection and one transaction and cannot leak either into the next piece of work. The executor is a
/// hosted service and therefore must not depend on anything scoped — a singleton holding an open connection
/// for the process lifetime is exactly the mistake that makes SQLite deployments flaky.
/// </para>
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDailyMusingsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var paths = InstancePaths.FromConfiguration(configuration);
        paths.EnsureCreated();

        services.AddSingleton(paths);

        // Process-wide, stateless services.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher());
        services.AddSingleton<ISecretGenerator, CryptoSecretGenerator>();
        services.AddSingleton<ISecretStore, FileSecretStore>();
        services.AddSingleton<JobExecutorHeartbeat>();

        // The one executor instance §14 allows.
        services.AddHostedService<JobExecutorService>();

        // Per-scope database access.
        services.AddScoped(_ => new SqliteConnectionAccessor(paths.DatabasePath));
        services.AddScoped<IUnitOfWork, SqliteUnitOfWork>();
        services.AddScoped<IMigrationRunner, SqliteMigrator>();

        services.AddScoped<IAdminAccountRepository, SqliteAdminAccountRepository>();
        services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.AddScoped<IPairingCodeRepository, SqlitePairingCodeRepository>();
        services.AddScoped<IInputEntryRepository, SqliteInputEntryRepository>();
        services.AddScoped<IJobRepository, SqliteJobRepository>();
        services.AddScoped<ITopicRepository, SqliteTopicRepository>();
        services.AddScoped<IReflectionRepository, SqliteReflectionRepository>();
        services.AddScoped<IEmbeddingIndexRepository, SqliteEmbeddingIndexRepository>();
        services.AddScoped<IAppSettingStore, SqliteAppSettingStore>();
        services.AddScoped<IContentSettingsProvider, AppSettingContentSettingsProvider>();
        services.AddScoped<IContentCalendarProvider, AppSettingContentCalendarProvider>();

        // Which fingerprint the stored index is complete for (A.8). Scoped because it reads the settings table,
        // which holds the scoped connection.
        services.AddScoped<IEmbeddingIndexState, AppSettingEmbeddingIndexState>();

        // Audio storage: the media volume, behind the port that enforces "durable before acknowledged" (§8.2).
        services.AddSingleton<IAudioStore, FileAudioStore>();

        // The transcription endpoint. Configuration and the secret store are singletons, so the client can be one
        // too — and a single long-lived HttpClient is the recommended shape for one upstream.
        services.AddSingleton<ITranscriptionSettingsProvider, ConfigurationTranscriptionSettingsProvider>();
        services.AddSingleton<IGenerationSettingsProvider, ConfigurationGenerationSettingsProvider>();
        services.AddSingleton<IEmbeddingSettingsProvider, ConfigurationEmbeddingSettingsProvider>();
        services.AddSingleton<IRetrievalSettingsProvider, ConfigurationRetrievalSettingsProvider>();
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<ITranscriptionClient, OpenAiCompatibleTranscriptionClient>();

        // Generation and the source check share one client because they are the same call shape against the same
        // endpoints; they are registered separately because the jobs that use them are separate.
        services.AddSingleton<OpenAiCompatibleGenerationClient>();
        services.AddSingleton<IReflectionGenerationClient>(provider =>
            provider.GetRequiredService<OpenAiCompatibleGenerationClient>());
        services.AddSingleton<IUnsourcedStatementChecker>(provider =>
            provider.GetRequiredService<OpenAiCompatibleGenerationClient>());
        services.AddSingleton<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>();

        // Use cases. They hold repositories (scoped) and clients (singletons), so they belong to the scope.
        services.AddScoped<Application.Jobs.JobEnqueuer>();
        services.AddScoped<Application.Topics.CreateTopicUseCase>();
        services.AddScoped<Application.Topics.ListTopicsUseCase>();
        services.AddScoped<Application.Topics.RenameTopicUseCase>();
        services.AddScoped<Application.Topics.MergeTopicsUseCase>();
        services.AddScoped<Application.Topics.AssignInputTopicsUseCase>();
        services.AddScoped<Application.Topics.AssignTopicsAutomaticallyUseCase>();
        services.AddScoped<Application.Embeddings.GetSemanticSearchStateUseCase>();
        services.AddScoped<Application.Embeddings.EnsureEmbeddingIndexedUseCase>();
        services.AddScoped<Application.Embeddings.EmbedInputUseCase>();
        services.AddScoped<Application.Embeddings.RebuildEmbeddingIndexUseCase>();
        services.AddScoped<HistoryRetrievalUseCase>();
        services.AddScoped<GetReflectionUseCase>();
        services.AddScoped<ListReflectionsUseCase>();
        services.AddScoped<ConfirmReflectionUseCase>();
        services.AddScoped<SwitchReflectionVersionUseCase>();
        services.AddScoped<EditReflectionVersionUseCase>();
        services.AddScoped<GetReflectionSourcesUseCase>();
        services.AddScoped<RunUnsourcedStatementCheckUseCase>();
        services.AddScoped<RequestReflectionGenerationUseCase>();
        services.AddScoped<GenerateReflectionUseCase>();

        // Job handlers are scoped because they use repositories, which hold a scoped connection.
        services.AddScoped<IJobHandler, TranscriptionJobHandler>();
        services.AddScoped<IJobHandler, ReflectionGenerationJobHandler>();
        services.AddScoped<IJobHandler, UnsourcedStatementCheckJobHandler>();
        services.AddScoped<IJobHandler, EmbeddingIndexJobHandler>();
        services.AddScoped<IJobHandler, EmbeddingRebuildJobHandler>();

        // The nightly generation slot and the catch-up scan (§7). One instance only, like the executor.
        services.AddHostedService<ReflectionSchedulerService>();

        // Basic health (§16): only what this instance controls. External services get "test connection".
        services.AddScoped<IHealthProbe, DatabaseWritableProbe>();
        services.AddScoped<IHealthProbe, MediaDirectoryWritableProbe>();
        services.AddScoped<IHealthProbe, JobExecutorProbe>();

        return services;
    }

    /// <summary>
    /// Applies outstanding migrations. Called once at startup, before the instance serves traffic, so that
    /// no request can observe a half-migrated schema (§15.3).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyMigrationsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        return await runner.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
