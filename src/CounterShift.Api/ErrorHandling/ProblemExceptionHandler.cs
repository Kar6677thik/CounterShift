using System.Net.Sockets;
using CounterShift.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace CounterShift.Api.ErrorHandling;

/// <summary>
/// Turns exceptions into RFC 9457 problem+json responses, in one place, so handlers stay focused on
/// the happy path.
/// <list type="bullet">
/// <item><see cref="DomainValidationException"/> → 400, or 422 if every error is "well-formed but not now" (ADR 0003).</item>
/// <item>Storage unreachable → 503. The decision API fails <b>closed</b>: we never answer "allow" without data (ADR 0004).</item>
/// </list>
/// </summary>
internal sealed partial class ProblemExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int status;
        string title;
        var extensions = new Dictionary<string, object?>();

        switch (exception)
        {
            case DomainValidationException validation:
                status = validation.Errors.All(e => e.Kind == ValidationErrorKind.Unprocessable)
                    ? StatusCodes.Status422UnprocessableEntity
                    : StatusCodes.Status400BadRequest;
                title = "The request is not valid.";
                extensions["errors"] = validation.Errors
                    .GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
                break;

            case BadHttpRequestException badRequest:
                // Unreadable JSON and similar. In Development, minimal APIs *throw* this rather than
                // returning 400; without this case it would surface as a misleading 500.
                status = badRequest.StatusCode;
                title = "The request body could not be read.";
                break;

            case NpgsqlException or SocketException or TimeoutException:
                status = StatusCodes.Status503ServiceUnavailable;
                title = "Counter storage is unavailable.";
                LogStorageUnavailable(logger, exception);
                break;

            default:
                return false; // let the framework's default 500 handling (and logging) take it
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Extensions = extensions },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Counter storage unavailable")]
    private static partial void LogStorageUnavailable(ILogger logger, Exception exception);
}
