using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Inputs;

namespace DailyMusings.Application.Operations;

/// <summary>What a content sweep did. Counts only: never a file name, a transcript or a topic (§16).</summary>
public sealed record ContentCleanupResult(int CandidateDays, int CleanedEntries, int FailedDeletions, long ReleasedBytes);

/// <summary>
/// The retention sweep for captured content (docs/开发指导.md §15.1 and the content window added with this
/// feature).
/// <para>
/// Runs as its own persisted job, for the same reason the audio sweep does: it is housekeeping, and a failure
/// here must not affect anything else. The worst outcome of a sweep that cannot delete one file is that the
/// file is still there next time.
/// </para>
/// <para>
/// The entries are soft-deleted, never removed. <c>source_reference</c> rows point at them, so a real delete
/// would either fail on the foreign key or take a historical article's provenance with it — and "you deleted
/// what I said, and now the article claims I never said it" is exactly the kind of silent rewriting this
/// product exists not to do. What the user asked the window to mean — the words and the recording are gone —
/// is what it does.
/// </para>
/// </summary>
public sealed class RunContentCleanupUseCase
{
    /// <summary>How many confirmed days one sweep looks at. Bounds the work a single tick can do.</summary>
    private const int ConfirmedDayScanLimit = 500;

    private readonly IReflectionRepository _reflections;
    private readonly IInputEntryRepository _inputs;
    private readonly IAudioStore _audio;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;

    public RunContentCleanupUseCase(
        IReflectionRepository reflections,
        IInputEntryRepository inputs,
        IAudioStore audio,
        IContentSettingsProvider settings,
        IClock clock)
    {
        _reflections = reflections;
        _inputs = inputs;
        _audio = audio;
        _settings = settings;
        _clock = clock;
    }

    public async Task<ContentCleanupResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var policy = settings.ResolveContentRetention();
        policy.Validate();

        if (policy.KeepsForever)
        {
            // The default. Nothing is deleted because nobody asked, which is the whole point of the default.
            return new ContentCleanupResult(0, 0, 0, 0);
        }

        var now = _clock.UtcNow;

        // The cutoff is the earliest 计时起点 that could already be due, so the query uses the index instead of
        // loading every day the instance has ever finished with. 起点 = 首次确认与首次公开发布中较早的那个（A.35）。
        var cutoff = policy.DeletesImmediately ? now : now.AddDays(-policy.Days);

        var days = await _reflections
            .ListRetentionCandidatesAsync(cutoff, ConfirmedDayScanLimit, cancellationToken)
            .ConfigureAwait(false);

        var cleaned = 0;
        var failed = 0;
        long released = 0;
        var candidateDays = 0;

        foreach (var (reflection, countdownFrom) in days)
        {
            var entries = await _inputs
                .ListByContentDateAsync(reflection.ContentDate, cancellationToken)
                .ConfigureAwait(false);

            var cleanable = entries
                .Where(entry => ContentCleanupPolicy.IsCleanable(entry, countdownFrom, policy, now))
                .ToArray();

            if (cleanable.Length == 0)
            {
                continue;
            }

            candidateDays++;

            foreach (var entry in cleanable)
            {
                var path = entry.AudioPath;
                long size = 0;

                if (path is not null)
                {
                    size = await _audio.GetSizeAsync(path, cancellationToken).ConfigureAwait(false) ?? 0;

                    try
                    {
                        await _audio.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The entry still names its blob, so nothing is lost track of and the next sweep tries
                        // again. Deliberately not a partial purge: blanking the text while the recording stays
                        // would leave the user with content they can no longer see but which is still on disk.
                        failed++;
                        continue;
                    }
                }

                entry.PurgeContent(now);
                await _inputs.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);

                cleaned++;
                released += size;
            }
        }

        return new ContentCleanupResult(candidateDays, cleaned, failed, released);
    }
}
