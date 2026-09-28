using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Admin.Shared;

/// <summary>
/// Runs one admin-page operation and turns any failure into a sentence on the page.
/// <para>
/// Blazor does <em>not</em> protect event handlers. An exception that escapes an <c>@onclick</c> handler
/// terminates the circuit, and from that moment every control on the page is dead — which an operator reports as
/// "the button does not work", not as "an error occurred". <c>ErrorBoundary</c> is no help either: it only
/// observes exceptions thrown while rendering, never those thrown by a handler.
/// </para>
/// <para>
/// So every handler body goes through here. Domain and use-case failures carry user-facing text and are shown as-is;
/// technical details stay in the server log.
/// </para>
/// </summary>
public static class AdminOperation
{
    /// <summary>
    /// Runs <paramref name="work"/>. Returns the message to show, or <c>null</c> when it succeeded.
    /// </summary>
    public static async Task<string?> RunAsync(Func<Task> work, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            await work();
            return null;
        }
        catch (UseCaseException exception)
        {
            return exception.Message;
        }
        catch (DomainException exception)
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            // Logged rather than shown: the operator gets a recognisable summary, and the detail stays in the log
            // where the redaction rules apply (§16).
            logger?.LogError(exception, "An administrator page operation failed.");

            return "操作没有完成，请稍后重试；如果问题持续，请查看系统设置中的运行日志。";
        }
    }
}
