using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Publishing;
using DailyMusings.Application.Reflections;
using DailyMusings.Infrastructure.Configuration;
using DailyMusings.Infrastructure.Diagnostics;
using DailyMusings.Infrastructure.Generation;
using DailyMusings.Infrastructure.Jobs;
using DailyMusings.Infrastructure.Notifications;
using DailyMusings.Infrastructure.Operations;
using DailyMusings.Infrastructure.Persistence;
using DailyMusings.Infrastructure.Persistence.Repositories;
using DailyMusings.Infrastructure.Publishing;
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

        // The bootstrap overrides (§8.1). Read before the host is built and written by the admin page, so it is a
        // plain file rather than a settings-table row — see RuntimeOverridesFile for why.
        services.AddSingleton<IRuntimeOverridesStore, FileRuntimeOverridesStore>();

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
        services.AddScoped<IPublishTargetRepository, SqlitePublishTargetRepository>();
        services.AddScoped<IPublicationRepository, SqlitePublicationRepository>();
        services.AddScoped<IAppSettingStore, SqliteAppSettingStore>();
        services.AddScoped<IContentSettingsProvider, AppSettingContentSettingsProvider>();
        services.AddScoped<IContentCalendarProvider, AppSettingContentCalendarProvider>();

        // Which fingerprint the stored index is complete for (A.8). Scoped because it reads the settings table,
        // which holds the scoped connection.
        services.AddScoped<IEmbeddingIndexState, AppSettingEmbeddingIndexState>();

        // Audio storage: the media volume, behind the port that enforces "durable before acknowledged" (§8.2).
        services.AddSingleton<IAudioStore, FileAudioStore>();

        services.AddScoped<IInstanceSettingsProvider, AppSettingInstanceSettingsProvider>();

        // The transcription endpoint. Configuration and the secret store are singletons, so the client can be one
        // too — and a single long-lived HttpClient is the recommended shape for one upstream.
        services.AddSingleton<ITranscriptionSettingsProvider, ConfigurationTranscriptionSettingsProvider>();
        services.AddSingleton<IGenerationSettingsProvider, ConfigurationGenerationSettingsProvider>();
        services.AddSingleton<IEmbeddingSettingsProvider, ConfigurationEmbeddingSettingsProvider>();

        // Scoped, not singleton: it now reads the settings table per call, so it holds the scoped setting store (and
        // through it the scoped connection). It used to be a singleton that cached its values in the constructor,
        // which meant a change needed a restart even in deployment configuration.
        services.AddScoped<IRetrievalSettingsProvider, ConfigurationRetrievalSettingsProvider>();
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

        // Publishing and mail. Each is a singleton because they hold no per-call state.
        services.AddScoped<IPublishDestinationProvider, ConfigurationPublishDestinationProvider>();
        services.AddSingleton<IMarkdownWriter, FileMarkdownWriter>();
        services.AddSingleton<ISmtpSettingsProvider, ConfigurationSmtpSettingsProvider>();

        // §15's operations: a readable export, a complete backup, the staged restore and the retention sweep. The
        // snapshotter is scoped because it uses the scoped connection; the writers are singletons because they only
        // touch the filesystem.
        services.AddScoped<SqliteDatabaseSnapshotter>();
        services.AddSingleton<IInstanceExportWriter, FileInstanceExportWriter>();

        // Scoped, not singleton: it takes the scoped snapshotter (which holds a connection) and reads the settings
        // table for the retention count. As a singleton it was a captive dependency — resolved once from the root
        // scope, keeping one connection for the process lifetime, which is exactly what this file's own comment
        // above warns against.
        services.AddScoped<IBackupWriter, ZipBackupWriter>();
        services.AddSingleton<IRestoreStager, StagedRestoreService>();

        // §16: the temporary debug switch and the on-demand test connections. Scoped, because both read settings.
        services.AddScoped<IDiagnosticMode, AppSettingDiagnosticMode>();
        services.AddScoped<IExternalServiceProbe, ExternalServiceProbe>();
        services.AddSingleton<INotificationSettingsProvider, ConfigurationNotificationSettingsProvider>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

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

        // Phase four: publishing and notifications.
        services.AddScoped<Application.Operations.BuildInstanceDataUseCase>();
        services.AddScoped<Application.Operations.CreateExportUseCase>();
        services.AddScoped<Application.Operations.CreateBackupUseCase>();
        services.AddScoped<Application.Operations.ListInstanceDataUseCase>();
        services.AddScoped<Application.Operations.StageRestoreUseCase>();
        services.AddScoped<Application.Operations.RunAudioCleanupUseCase>();
        services.AddScoped<Application.Operations.ManageDiagnosticModeUseCase>();
        services.AddScoped<Application.Operations.ProbeExternalServiceUseCase>();
        services.AddScoped<Application.Operations.RequestIndexRebuildUseCase>();
        services.AddScoped<Application.Operations.GetIndexStatusUseCase>();
        services.AddScoped<Application.Operations.RunMaintenanceJobUseCase>();
        services.AddScoped<Application.Notifications.QueueNotificationUseCase>();
        services.AddScoped<Application.Notifications.SendNotificationUseCase>();
        services.AddScoped<Application.Configuration.UpdateContentSettingsUseCase>();
        services.AddScoped<Application.Configuration.UpdateInstanceSettingsUseCase>();
        services.AddScoped<Application.Publishing.ListPublishTargetsUseCase>();
        services.AddScoped<Application.Publishing.ListPublicationsUseCase>();
        services.AddScoped<Application.Publishing.CreatePublishTargetUseCase>();
        services.AddScoped<Application.Publishing.UpdatePublishTargetUseCase>();
        services.AddScoped<Application.Publishing.SetAutomaticPublishUseCase>();
        services.AddScoped<Application.Publishing.RequestPublicationUseCase>();
        services.AddScoped<Application.Publishing.RunPublicationUseCase>();
        services.AddScoped<Application.Publishing.RetryPublicationUseCase>();
        services.AddScoped<Application.Publishing.CheckRemoteUseCase>();
        services.AddScoped<Application.Publishing.ResolveRemoteDivergenceUseCase>();
        services.AddScoped<Application.Publishing.SchedulePublicationsUseCase>();

        // Job handlers are scoped because they use repositories, which hold a scoped connection.
        services.AddScoped<IJobHandler, TranscriptionJobHandler>();
        services.AddScoped<IJobHandler, ReflectionGenerationJobHandler>();
        services.AddScoped<IJobHandler, UnsourcedStatementCheckJobHandler>();
        services.AddScoped<IJobHandler, EmbeddingIndexJobHandler>();
        services.AddScoped<IJobHandler, EmbeddingRebuildJobHandler>();
        services.AddScoped<IJobHandler, PublicationJobHandler>();
        services.AddScoped<IJobHandler, NotificationJobHandler>();

        // The notification preferences, read and written through one place so the admin page and the API agree.
        services.AddScoped<Application.Notifications.ReadNotificationSettingsUseCase>();
        services.AddScoped<Application.Notifications.UpdateNotificationSettingsUseCase>();

        // §8.1's admin-side configuration of the three model endpoints and §12's SMTP server.
        services.AddScoped<Application.Configuration.ReadModelEndpointsUseCase>();
        services.AddScoped<Application.Configuration.UpdateModelEndpointUseCase>();
        services.AddScoped<Application.Configuration.ReadSmtpSettingsUseCase>();
        services.AddScoped<Application.Configuration.UpdateSmtpSettingsUseCase>();
        services.AddScoped<IJobHandler, AudioCleanupJobHandler>();
        services.AddScoped<IJobHandler, BackupJobHandler>();

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
