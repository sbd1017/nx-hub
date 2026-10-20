using Microsoft.AspNetCore.Mvc;
using NxHub.Core;
using NxHub.Server.Auth;

namespace NxHub.Server;

/// <summary>
/// Requires the admin bearer token (or a trusted reverse-proxy login) on every <c>/api</c> route
/// except health and info.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AccessTokenService _auth;

    public TokenAuthMiddleware(RequestDelegate next, AccessTokenService auth)
    {
        _next = next;
        _auth = auth;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        var path = context.Request.Path;
        var needsToken = _auth.Enabled
            && path.StartsWithSegments("/api")
            && !path.StartsWithSegments("/api/health")
            && !path.StartsWithSegments("/api/info")
            // The WebSocket authenticates with its first message instead of a header.
            && !path.StartsWithSegments("/api/events");

        if (!needsToken || _auth.Validate(AccessTokenService.FromAuthorizationHeader(context.Request.Headers.Authorization)))
        {
            await _next(context);
            return;
        }

        if (_auth.IsTrustedProxyUser(context))
        {
            var safeMethod = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
            if (safeMethod || AccessTokenService.IsSameOrigin(context.Request))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var forbidden = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Cross-site request blocked",
                Detail = "Changes must come from the NX Hub page itself.",
            };
            forbidden.Extensions["code"] = "cross_site_request";
            await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = forbidden });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Authentication required",
            Detail = "Send 'Authorization: Bearer <token>'. The token is stored in the NX Hub data directory (admin-token).",
        };
        problem.Extensions["code"] = "unauthorized";
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}

/// <summary>Conservative browser security headers. The UI is same-origin only.</summary>
public sealed class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        // The Scalar API reference page needs inline scripts; keep the strict policy everywhere else.
        if (!context.Request.Path.StartsWithSegments("/docs") && !context.Request.Path.StartsWithSegments("/scalar"))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        return _next(context);
    }
}

/// <summary>Starts sessions on boot; on shutdown releases held inputs and disconnects.</summary>
public sealed class SwitchManagerHostedService : IHostedService
{
    private readonly SwitchManager _manager;

    public SwitchManagerHostedService(SwitchManager manager) => _manager = manager;

    public Task StartAsync(CancellationToken cancellationToken) => _manager.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await _manager.DisposeAsync();
}
