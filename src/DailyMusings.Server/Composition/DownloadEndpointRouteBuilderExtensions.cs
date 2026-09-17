using System.IO.Compression;
using DailyMusings.Contracts;
using DailyMusings.Infrastructure.Storage;
using DailyMusings.Server.Authentication;

namespace DailyMusings.Server.Composition;

/// <summary>
/// Hands over the packages the operations page produces (docs/开发指导.md §15.1, §15.2).
/// <para>
/// Administrator-only, like everything else that touches the whole instance: a backup holds every captured thought
/// and an export holds the readable form of the same, neither of which is something a capture credential may fetch.
/// </para>
/// <para>
/// The page could list these packages but not hand them over, which is half a feature — the operator's next step
/// was always <c>docker cp</c> on the host, from a shell they may not have. The names come from the listing the page
/// already shows, and are re-validated here because a URL is not a trusted source.
/// </para>
/// </summary>
public static class DownloadEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/backups/{fileName}/download", DownloadBackup)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        endpoints
            .MapGet("/api/exports/{name}/download", DownloadExport)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        return endpoints;
    }

    private static IResult DownloadBackup(string fileName, InstancePaths paths)
    {
        if (!IsPlainName(fileName))
        {
            return InvalidName();
        }

        var path = Path.Combine(paths.BackupPath, fileName);

        return File.Exists(path)
            ? Results.File(path, "application/zip", fileName)
            : NotFound("No backup with that name.");
    }

    /// <summary>
    /// An export is a directory rather than a file, so it is archived on demand. The archive goes to the operating
    /// system's temporary directory and is removed when the response finishes: an instance should not accumulate
    /// copies of its own exports just because somebody looked at one.
    /// </summary>
    private static IResult DownloadExport(string name, InstancePaths paths, HttpContext context)
    {
        if (!IsPlainName(name))
        {
            return InvalidName();
        }

        var directory = Path.Combine(paths.ExportPath, name);

        if (!Directory.Exists(directory))
        {
            return NotFound("No export with that name.");
        }

        var archive = Path.Combine(Path.GetTempPath(), $"dailymusings-export-{Guid.CreateVersion7():N}.zip");

        ZipFile.CreateFromDirectory(directory, archive, CompressionLevel.Fastest, includeBaseDirectory: true);

        context.Response.OnCompleted(() =>
        {
            try
            {
                File.Delete(archive);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a download that already succeeded.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Task.CompletedTask;
        });

        return Results.File(archive, "application/zip", $"{name}.zip");
    }

    /// <summary>
    /// A bare name, no path component. Checked here rather than trusted from the listing: the parameter arrives
    /// from a URL, and <c>Path.GetInvalidFileNameChars</c> alone is not enough on Linux, where the only invalid
    /// character is the null byte.
    /// </summary>
    private static bool IsPlainName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static IResult InvalidName() =>
        Results.Json(
            new ApiError(ApiErrorCodes.ValidationFailed, "The name must be a plain file or package name."),
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string message) =>
        Results.Json(
            new ApiError(ApiErrorCodes.NotFound, message),
            statusCode: StatusCodes.Status404NotFound);
}
