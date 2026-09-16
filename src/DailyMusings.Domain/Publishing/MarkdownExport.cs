using System.Globalization;
using System.Text;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Publishing;

/// <summary>
/// A front matter template with placeholders, and the document it renders (docs/开发指导.md §11.2).
/// <para>
/// The template is data rather than code so an operator can match their own site's conventions without a
/// release. Rendering refuses an unknown placeholder instead of dropping it: a typo in a template would
/// otherwise silently produce a file that looks fine and loses a field — the worst possible failure for an
/// export whose whole purpose is to be read by another tool.
/// </para>
/// </summary>
public sealed record MarkdownTemplate(string Text)
{
    /// <summary>Default template: the fields §11.2 names, in the order Hexo's own scaffolding uses.</summary>
    public const string Default = """
        ---
        title: {title}
        date: {date}
        updated: {updated}
        tags: {tags}
        categories: {categories}
        draft: {draft}
        ---

        {body}
        """;

    public static MarkdownTemplate DefaultTemplate { get; } = new(Default);

    /// <summary>Placeholders the renderer understands. Anything else in the text is an error.</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
        ["title", "date", "updated", "tags", "categories", "draft", "body", "summary", "slug"];

    public void Validate()
    {
        foreach (var placeholder in FindPlaceholders(Text))
        {
            if (!Placeholders.Contains(placeholder, StringComparer.Ordinal))
            {
                throw new DomainException(
                    "markdown.template.unknown_placeholder",
                    $"The template uses {{{placeholder}}}, which is not a known placeholder.");
            }
        }

        if (!FindPlaceholders(Text).Contains("body", StringComparer.Ordinal))
        {
            // Without it the export would produce everything except the article.
            throw new DomainException(
                "markdown.template.no_body",
                "The template must contain the {body} placeholder.");
        }
    }

    public string Render(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate();

        var builder = new StringBuilder(Text.Length + document.Body.Length);
        var index = 0;

        while (index < Text.Length)
        {
            var open = Text.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(Text, index, Text.Length - index);
                break;
            }

            var close = Text.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(Text, index, Text.Length - index);
                break;
            }

            builder.Append(Text, index, open - index);
            var name = Text[(open + 1)..close];
            builder.Append(Value(name, document));
            index = close + 1;
        }

        return builder.ToString();
    }

    private static string Value(string name, MarkdownDocument document) => name switch
    {
        "title" => YamlScalar.Escape(document.Title),
        "date" => document.ContentDate.Value
            .ToDateTime(TimeOnly.MinValue)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        "updated" => document.UpdatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        "tags" => YamlScalar.FlowSequence(document.Tags),
        "categories" => YamlScalar.FlowSequence(document.Categories),
        "draft" => document.IsDraft ? "true" : "false",
        "summary" => YamlScalar.Escape(document.Summary),

        // Quoted like every other value: a slug of "true" or "2026" would otherwise be read back as a boolean or
        // a number by the generator that consumes this file.
        "slug" => YamlScalar.Escape(document.Slug),
        "body" => document.Body,
        _ => throw new DomainException(
            "markdown.template.unknown_placeholder",
            $"The template uses {{{name}}}, which is not a known placeholder."),
    };

    private static IEnumerable<string> FindPlaceholders(string text)
    {
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);
            if (open < 0)
            {
                yield break;
            }

            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                yield break;
            }

            yield return text[(open + 1)..close];
            index = close + 1;
        }
    }
}

/// <summary>Everything a Markdown export needs, already resolved from the draft.</summary>
public sealed record MarkdownDocument(
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    ContentDate ContentDate,
    DateTimeOffset UpdatedAtUtc,
    bool IsDraft,
    string Slug)
{
    /// <summary>
    /// Builds the document for a version. The post date is the <em>content day</em>, not today: §7 makes that
    /// day the day the material belongs to, and a blog whose dates drift from that would misreport when things
    /// happened — which is the same promise the recall boundary protects.
    /// </summary>
    public static MarkdownDocument From(ReflectionVersion version, ContentDate contentDate)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new MarkdownDocument(
            version.Title,
            version.Summary,
            version.Body,
            version.Tags,
            version.Categories,
            contentDate,
            version.EditedAtUtc ?? version.CreatedAtUtc,
            IsDraft: true,
            Slug: MarkdownSlug.From(version.Title));
    }
}

/// <summary>
/// Turns a title into the slug half of <c>YYYY-MM-DD-slug.md</c> (§11.2).
/// <para>
/// CJK characters are kept rather than transliterated. Hexo, Git and every filesystem in play handle UTF-8
/// names, and a transliteration step would either need a dictionary for Chinese or produce a meaningless
/// ASCII string — both worse than a readable filename. Characters that <em>are</em> unsafe in a path (slashes,
/// colons, control characters, wildcards) are folded to a dash.
/// </para>
/// </summary>
public static class MarkdownSlug
{
    /// <summary>Keeps a filename well inside every filesystem's limit, including the date and extension.</summary>
    public const int MaxLength = 60;

    public static string From(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        var pendingDash = false;

        foreach (var ch in title.Trim())
        {
            if (IsSafe(ch))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingDash = false;
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            pendingDash = true;
        }

        // Leading and trailing dots go too: a name starting with one is hidden on Unix, and a name ending with one
        // is quietly truncated on Windows — either way the file the user looks for would not be the file we wrote.
        var slug = builder.ToString().Trim('-', '.', '_');

        if (slug.Length <= MaxLength)
        {
            return slug;
        }

        return slug[..MaxLength].Trim('-', '.', '_');
    }

    private static bool IsSafe(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '.' ||
        (ch >= '\u4E00' && ch <= '\u9FFF'); // CJK unified ideographs
}

/// <summary>The filename a version exports to, and how a collision is resolved (§11.2).</summary>
public static class MarkdownFileName
{
    /// <summary>How many versioned names to try before giving up. Far beyond any real draft directory.</summary>
    public const int MaxAttempts = 500;

    /// <summary><c>YYYY-MM-DD-slug.md</c>, or <c>YYYY-MM-DD.md</c> when the title yields no usable slug.</summary>
    public static string BaseName(ContentDate contentDate, string slug)
    {
        var date = contentDate.ToString();
        return string.IsNullOrWhiteSpace(slug) ? date : $"{date}-{slug}";
    }

    /// <summary>
    /// The first free name for this base, versioned on collision. Never returns an occupied name, which is what
    /// makes "再次导出默认创建带版本号的新文件" true by construction rather than by a check at the call site.
    /// </summary>
    public static string NextVersionedName(string baseName, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(exists);

        var candidate = $"{baseName}.md";
        if (!exists(candidate))
        {
            return candidate;
        }

        for (var version = 2; version <= MaxAttempts; version++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{baseName}-{version}.md");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new DomainException(
            "markdown.file.too_many_versions",
            $"{baseName} already has {MaxAttempts} exported versions.");
    }
}

/// <summary>What to do about a target filename (§11.2: 不得静默覆盖已存在或被外部修改的文件).</summary>
public enum MarkdownWritePlan
{
    /// <summary>Write a new file, versioned on collision. The default for a re-export.</summary>
    CreateNewFile = 0,

    /// <summary>Overwrite, because the file is ours, untouched, and the user explicitly asked.</summary>
    ReplaceExistingFile = 1,

    /// <summary>The file exists but this instance never wrote it. Never touched, under any circumstances.</summary>
    RefuseUnowned = 2,

    /// <summary>The file changed after we wrote it: someone edited it outside the product.</summary>
    RefuseExternallyModified = 3,
}

/// <summary>
/// Decides whether an export may write, replace or must refuse (docs/开发指导.md §11.2).
/// <para>
/// The rule the product promises is "no silent overwrite", and the rule that makes it safe is the recorded hash
/// of what we last wrote. Without it, "the file is already there" cannot be told apart from "the user has been
/// editing this file by hand in their blog repository" — and the second case is the one where a careless write
/// destroys work that exists nowhere else.
/// </para>
/// </summary>
public static class MarkdownWritePolicy
{
    public static MarkdownWritePlan Decide(
        bool fileExists,
        bool fileIsOurs,
        bool externallyModified,
        bool userConfirmedReplace)
    {
        if (!fileExists)
        {
            return MarkdownWritePlan.CreateNewFile;
        }

        if (!fileIsOurs)
        {
            return MarkdownWritePlan.RefuseUnowned;
        }

        if (externallyModified)
        {
            // Refused even when the user asked to replace: the confirmation was given for a file whose contents
            // nobody has seen, and what is on disk now may be their own writing.
            return MarkdownWritePlan.RefuseExternallyModified;
        }

        return userConfirmedReplace
            ? MarkdownWritePlan.ReplaceExistingFile
            : MarkdownWritePlan.CreateNewFile;
    }
}

/// <summary>
/// YAML scalars that survive a round trip through a parser.
/// <para>
/// A title is user writing, so it can contain a colon, a quote, a leading dash or a newline — all of which turn
/// an unquoted YAML value into either a parse error or a different value. Everything is therefore emitted as a
/// double-quoted scalar with escapes, which is valid YAML for any string.
/// </para>
/// </summary>
public static class YamlScalar
{
    public static string Escape(string? value)
    {
        var builder = new StringBuilder((value?.Length ?? 0) + 2);
        builder.Append('"');

        foreach (var ch in value ?? string.Empty)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>A flow sequence of quoted scalars: <c>["a", "b"]</c>.</summary>
    public static string FlowSequence(IEnumerable<string>? values) =>
        "[" + string.Join(", ", (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(Escape)) + "]";
}
