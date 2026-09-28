using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Publishing;

/// <summary>Exports the current working version as a private Hexo draft immediately after generation.</summary>
public sealed class ExportWorkingDraftUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IContentSettingsProvider _settings;
    private readonly IPublishDestinationProvider _destinations;
    private readonly IMarkdownWriter _markdown;

    public ExportWorkingDraftUseCase(
        IReflectionRepository reflections,
        IContentSettingsProvider settings,
        IPublishDestinationProvider destinations,
        IMarkdownWriter markdown)
    {
        _reflections = reflections;
        _settings = settings;
        _destinations = destinations;
        _markdown = markdown;
    }

    public async Task<string?> ExecuteAsync(ContentDate contentDate, CancellationToken cancellationToken)
    {
        var reflection = await _reflections.FindByContentDateAsync(contentDate, cancellationToken).ConfigureAwait(false);
        if (reflection?.WorkingVersionId is not { } versionId)
        {
            return null;
        }

        var version = await _reflections.FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var target = PublishTarget.Create(
            PublishTargetId.New(),
            "稿件输出目录",
            PublishTargetType.Markdown,
            settings.DraftDirectory);
        var destination = await _destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        var document = MarkdownDocument.From(version, contentDate, isDraft: true);
        var content = new MarkdownTemplate(settings.HexoFrontMatterTemplate).Render(document);
        var write = await _markdown.WriteAsync(
            new MarkdownWriteRequest(
                destination.MarkdownDirectory,
                MarkdownFileName.BaseName(contentDate, document.Slug),
                content,
                PreviousFileName: null,
                PreviousContentHash: null,
                ReplaceExisting: false),
            cancellationToken).ConfigureAwait(false);

        return write.FileName;
    }
}
