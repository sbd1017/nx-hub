using System.Reflection;
using Microsoft.Extensions.Options;
using NxHub.Core;
using NxHub.Core.Diagnostics;
using NxHub.Core.Errors;
using NxHub.Core.Macros;
using NxHub.Protocol;
using NxHub.Server.Auth;

namespace NxHub.Server.Api;

public static class Endpoints
{
    public static string Version { get; } =
        typeof(Endpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void MapNxHubApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithTags("System").WithSummary("Liveness check. Does not require authentication.");

        api.MapGet("/info", (SwitchManager manager, AccessTokenService auth, IOptions<NxHubOptions> options) =>
                TypedResults.Ok(new InfoResponse("NX Hub", Version, auth.Enabled, manager.Sessions.Count, options.Value.Ui.Theme, options.Value.Demo.Enabled)))
            .WithTags("System").WithSummary("Server version and whether a token is required. No authentication.");

        var switches = api.MapGroup("/switches")
            .WithTags("Switches")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        switches.MapGet("/", (SwitchManager manager) =>
                TypedResults.Ok(manager.Sessions.Select(SwitchResponse.From).ToArray()))
            .WithSummary("List configured Switches with live status.");

        switches.MapPost("/", async (SwitchRequest request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = await manager.AddAsync(request.ToInput(), ct);
                return TypedResults.Created($"/api/switches/{session.SwitchId}", SwitchResponse.From(session));
            })
            .WithSummary("Add a Switch.");

        switches.MapGet("/{id}", (string id, SwitchManager manager) =>
                TypedResults.Ok(SwitchResponse.From(manager.GetSession(id))))
            .WithSummary("Get one Switch.");

        switches.MapPut("/{id}", async (string id, SwitchRequest request, SwitchManager manager, CancellationToken ct) =>
                TypedResults.Ok(SwitchResponse.From(await manager.UpdateAsync(id, request.ToInput(), ct))))
            .WithSummary("Replace a Switch's configuration. Address changes reconnect; disabling disconnects.");

        switches.MapDelete("/{id}", async (string id, SwitchManager manager, CancellationToken ct) =>
            {
                await manager.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Remove a Switch (releases held inputs and disconnects first).");

        switches.MapGet("/{id}/status", (string id, SwitchManager manager) =>
                TypedResults.Ok(manager.GetSession(id).GetStatus()))
            .WithSummary("Live connection status.");

        switches.MapPost("/{id}/connect", async (string id, SwitchManager manager, CancellationToken ct) =>
                TypedResults.Ok(await manager.GetSession(id).ConnectAsync(ct)))
            .WithSummary("Connect (and keep reconnecting). Waits up to 10 s for the attempt, then returns the status.");

        switches.MapPost("/{id}/disconnect", async (string id, SwitchManager manager, CancellationToken ct) =>
                TypedResults.Ok(await manager.GetSession(id).DisconnectAsync(ct)))
            .WithSummary("Release held inputs and disconnect. Auto-connect stays off until the next connect or restart.");

        switches.MapGet("/{id}/activity", (string id, int? limit, SwitchManager manager) =>
                TypedResults.Ok(manager.GetSession(id).Activity.GetRecent(limit ?? 50)))
            .WithSummary("Recent human-readable activity, newest first.");

        switches.MapGet("/{id}/screen", async (string id, int? maxAgeMs, SwitchManager manager, HttpContext http, CancellationToken ct) =>
            {
                var frame = await manager.GetSession(id).Screen.GetFrameAsync(TimeSpan.FromMilliseconds(Math.Clamp(maxAgeMs ?? 500, 0, 60_000)), ct);
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["X-Frame-Sequence"] = frame.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.Response.Headers["X-Captured-At"] = frame.CapturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                http.Response.Headers["X-Capture-Ms"] = frame.CaptureMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.File(frame.Jpeg, "image/jpeg");
            })
            .WithTags("Screen")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .WithSummary("Current screen as JPEG (live view). Frames up to maxAgeMs old (default 500) are shared between viewers.");

        MapController(switches.MapGroup("/{id}/controller").WithTags("Controller").WithDeviceErrors());
        MapMacroRuns(switches.MapGroup("/{id}/macro").WithTags("Macros").WithDeviceErrors());
        MapMacros(api.MapGroup("/macros").WithTags("Macros")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound));
        MapDiagnostics(switches.MapGroup("/{id}/diagnostics").WithTags("Diagnostics").ProducesProblem(StatusCodes.Status409Conflict));
    }

    private static void MapController(RouteGroupBuilder controller)
    {
        controller.MapGet("/", (string id, SwitchManager manager) =>
                TypedResults.Ok(manager.GetSession(id).Controller.GetSnapshot()))
            .WithSummary("Inputs NX Hub believes are currently held.");

        controller.MapPost("/press", async (string id, PressRequest request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                var button = Required(request.Button, "button");
                await session.Controller.PressAsync(button, Milliseconds(request.DurationMs), ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Press and release a button. Returns after the release has been sent.");

        controller.MapPost("/hold", async (string id, HoldRequest request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                await session.Controller.HoldAsync(Required(request.Button, "button"), Milliseconds(request.MaxHoldMs), ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Hold a button until released (or the safety limit expires).");

        controller.MapPost("/release", async (string id, ReleaseRequest request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                await session.Controller.ReleaseAsync(Required(request.Button, "button"), ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Release a held button.");

        controller.MapPost("/stick", async (string id, StickRequest request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                var stick = Required(request.Stick, "stick");
                var x = Required(request.X, "x");
                var y = Required(request.Y, "y");
                if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1 || Math.Abs(y) > 1)
                {
                    throw new ValidationFailedException("x", "x and y must be between -1 and 1.");
                }

                await session.Controller.SetStickAsync(stick, StickPosition.FromNormalised(x, y), Milliseconds(request.DurationMs), Milliseconds(request.MaxHoldMs), ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Move a stick. (0, 0) centres it.");

        controller.MapPost("/release-all", async (string id, ReleaseAllRequest? request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                await session.Controller.ReleaseAllAsync(request?.Everything ?? true, ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Release all buttons and centre both sticks.");

        controller.MapPost("/reset", async (string id, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                await session.Controller.ResetAsync(ct);
                return TypedResults.Ok(session.Controller.GetSnapshot());
            })
            .WithSummary("Release everything, then detach the virtual controller.");
    }

    private static void MapMacros(RouteGroupBuilder macros)
    {
        macros.MapGet("/", (MacroLibrary library, CancellationToken ct) => library.ListAsync(ct))
            .WithSummary("Saved macros.");

        macros.MapPost("/", async (MacroInput input, MacroLibrary library, CancellationToken ct) =>
            {
                var macro = await library.AddAsync(input, ct);
                return TypedResults.Created($"/api/macros/{macro.Id}", macro);
            })
            .WithSummary("Save a macro: {name, description?, steps, repeat?}. repeat 0 = until stopped.");

        macros.MapGet("/{macroId}", (string macroId, MacroLibrary library, CancellationToken ct) => library.GetAsync(macroId, ct))
            .WithSummary("One saved macro.");

        macros.MapPut("/{macroId}", (string macroId, MacroInput input, MacroLibrary library, CancellationToken ct) => library.UpdateAsync(macroId, input, ct))
            .WithSummary("Replace a saved macro.");

        macros.MapDelete("/{macroId}", async (string macroId, MacroLibrary library, CancellationToken ct) =>
            {
                await library.DeleteAsync(macroId, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a saved macro.");
    }

    private static void MapMacroRuns(RouteGroupBuilder runs)
    {
        runs.MapGet("/", (string id, SwitchManager manager) => TypedResults.Ok(new MacroRunResponse(manager.GetSession(id).Macros.State)))
            .WithSummary("The current or most recent macro run on this Switch.");

        runs.MapPost("/run", async (string id, RunMacroRequest request, SwitchManager manager, MacroLibrary library, IOptions<NxHubOptions> options, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                if (options.Value.Demo.Enabled && request.Macro is { } inline)
                {
                    // Visitor-written names would be shown to everyone on the public demo.
                    request = request with { Macro = inline with { Name = "Your recording", Description = null } };
                }

                var macro = request switch
                {
                    { MacroId: { Length: > 0 } macroId } => await library.GetAsync(macroId, ct),
                    { Macro: { } input } => library.Transient(input),
                    _ => throw new ValidationFailedException("macroId", "Give a saved macroId or an inline macro."),
                };
                return TypedResults.Accepted($"/api/switches/{id}/macro", new MacroRunResponse(session.Macros.Start(macro)));
            })
            .WithSummary("Start a macro on this Switch. One at a time; inputs are released when it ends.");

        runs.MapPost("/stop", async (string id, SwitchManager manager) =>
                TypedResults.Ok(new MacroRunResponse(await manager.GetSession(id).Macros.StopAsync())))
            .WithSummary("Stop the running macro and release its inputs.");

        runs.MapPost("/pause", (string id, SwitchManager manager) => TypedResults.Ok(new MacroRunResponse(manager.GetSession(id).Macros.Pause())))
            .WithSummary("Pause after the current step.");

        runs.MapPost("/resume", (string id, SwitchManager manager) => TypedResults.Ok(new MacroRunResponse(manager.GetSession(id).Macros.Resume())))
            .WithSummary("Resume a paused macro.");
    }

    private static void MapDiagnostics(RouteGroupBuilder diagnostics)
    {
        diagnostics.MapPost("/", async (string id, DiagnosticsRequest? request, SwitchManager manager, CancellationToken ct) =>
            {
                var session = manager.GetSession(id);
                var options = new DiagnosticOptions { IncludeControllerTest = request?.IncludeControllerTest ?? false };
                return TypedResults.Ok(await manager.Diagnostics.RunAsync(session, options, ct));
            })
            .WithSummary("Run setup diagnostics. Safe: no memory access; controller only if requested.");

        diagnostics.MapGet("/latest", (string id, SwitchManager manager) =>
            {
                manager.GetSession(id);
                return manager.Diagnostics.GetLatest(id) is { } report
                    ? TypedResults.Ok(report)
                    : throw new NxHubException(ErrorKind.NotFound, "no_diagnostics", "Diagnostics have not been run for this Switch yet.");
            })
            .WithSummary("The most recent diagnostic report since NX Hub started.");
    }

    /// <summary>Errors that come from talking to the Switch itself.</summary>
    private static RouteGroupBuilder WithDeviceErrors(this RouteGroupBuilder group) => group
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

    private static T Required<T>(T? value, string field)
        where T : struct =>
        value ?? throw new ValidationFailedException(field, $"'{field}' is required.");

    private static TimeSpan? Milliseconds(int? value) => value is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
}
