using DailyMusings.Application.Abstractions;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;

namespace DailyMusings.Server.Startup;

/// <summary>
/// Turns an escaping exception into a stable, diagnosable response instead of a bare 500 with an empty body.
/// <para>
/// The distinction it draws matters. A <see cref="DomainException"/> or <see cref="UseCaseException"/> is a
/// rule refusing a request, so its code is safe to return and useful to the client. Anything else is a defect:
/// the caller gets an opaque code, and the detail goes to the log — where §16 keeps only the error type and
/// the request id, never private content.
/// </para>
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (DomainException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, exception.Code, exception.Message)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, exception.Code, exception.Message)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Nothing to answer, and not worth an error-level log entry.
            _logger.LogDebug("Request was cancelled by the client.");
        }
        catch (Exception exception)
        {
            // The full exception, stack trace included, is logged here on purpose. §16 forbids logging private
            // content, not diagnostics: an unhandled exception is a defect, and a defect that can only be
            // identified by its type cannot be fixed. The obligation this creates sits with the code that talks
            // to the outside world — a model or SMTP adapter must redact content-bearing detail
            // before throwing, rather than relying on this handler to guess.
            _logger.LogError(
                exception,
                "Unhandled {ErrorType} for request {RequestId}.",
                exception.GetType().Name,
                context.TraceIdentifier);

            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.Unexpected,
                "The server failed to handle the request. The instance log records the error type and request id.")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Headers are already on the wire; anything we add now would corrupt the response.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;

        await context.Response
            .WriteAsJsonAsync(new ApiError(code, message), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
