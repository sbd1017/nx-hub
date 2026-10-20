using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NxHub.Core.Errors;
using NxHub.Protocol;

namespace NxHub.Server.Api;

/// <summary>
/// Maps exceptions to RFC 9457 problem details. Every error body has <c>type</c>, <c>title</c>,
/// <c>status</c>, <c>detail</c> and a stable <c>code</c>; validation errors add <c>errors</c>.
/// </summary>
public sealed class ProblemExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problems;
    private readonly ILogger<ProblemExceptionHandler> _logger;

    public ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger)
    {
        _problems = problems;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationFailedException validation:
                problem = new HttpValidationProblemDetails(validation.Errors.ToDictionary(kv => kv.Key, kv => kv.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation failed",
                    Detail = validation.Message,
                };
                problem.Extensions["code"] = validation.Code;
                break;
            case NxHubException known:
                problem = new ProblemDetails
                {
                    Status = StatusFor(known.Kind),
                    Title = TitleFor(known.Kind),
                    Detail = known.Message,
                };
                problem.Extensions["code"] = known.Code;
                break;
            case BadHttpRequestException bad:
                problem = new ProblemDetails
                {
                    Status = bad.StatusCode,
                    Title = "Invalid request",
                    Detail = DescribeBadRequest(bad),
                };
                problem.Extensions["code"] = "invalid_request";
                break;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                // Client went away; nothing useful to send.
                return true;
            default:
                _logger.LogError(exception, "Unhandled error processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal error",
                    Detail = "An unexpected error occurred. Check the server logs.",
                };
                problem.Extensions["code"] = "internal_error";
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    private static string DescribeBadRequest(BadHttpRequestException bad)
    {
        if (bad.InnerException is not JsonException { Path: { } path })
        {
            return bad.InnerException?.Message ?? bad.Message;
        }

        var field = path.TrimStart('$', '.');
        var hint = field switch
        {
            "button" => " Valid buttons: " + string.Join(", ", Enum.GetNames<SwitchButton>().Select(JsonNamingPolicy.CamelCase.ConvertName)) + ".",
            "stick" => " Valid sticks: left, right.",
            _ => string.Empty,
        };
        return $"Invalid value for '{field}'.{hint}";
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.NotConnected => StatusCodes.Status409Conflict,
        ErrorKind.SwitchTimeout => StatusCodes.Status504GatewayTimeout,
        ErrorKind.SwitchConnection => StatusCodes.Status502BadGateway,
        ErrorKind.SwitchProtocol => StatusCodes.Status502BadGateway,
        ErrorKind.Unsupported => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Validation failed",
        ErrorKind.NotFound => "Not found",
        ErrorKind.Conflict => "Conflict",
        ErrorKind.NotConnected => "Switch not connected",
        ErrorKind.SwitchTimeout => "Switch timed out",
        ErrorKind.SwitchConnection => "Switch connection failed",
        ErrorKind.SwitchProtocol => "Unexpected Switch response",
        ErrorKind.Unsupported => "Not supported",
        _ => "Error",
    };
}
