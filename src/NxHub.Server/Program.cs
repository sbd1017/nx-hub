using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NxHub.Core;
using NxHub.Core.Devices;
using NxHub.Core.Diagnostics;
using NxHub.Core.Events;
using NxHub.Core.Macros;
using NxHub.Protocol.Transport;
using NxHub.Server;
using NxHub.Server.Api;
using NxHub.Server.Auth;
using NxHub.Server.Demo;
using NxHub.Server.Storage;
using Scalar.AspNetCore;

// A published install is self-contained in its own directory; resolve config and the web UI from
// there rather than from whatever working directory systemd or a shell happens to use.
var installDirectory = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(installDirectory, "appsettings.json")) && Directory.Exists(Path.Combine(installDirectory, "wwwroot"))
        ? installDirectory
        : null,
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
});

builder.Services.Configure<NxHubOptions>(builder.Configuration.GetSection(NxHubOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(json => ConfigureJson(json.SerializerOptions));
// Surface binding failures (bad JSON, unknown enum values) through the problem-details handler.
builder.Services.Configure<RouteHandlerOptions>(routes => routes.ThrowOnBadRequest = true);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<NxHubOptions>>().Value;
    var path = options.ResolveDataDirectory(builder.Environment.ContentRootPath);
    Directory.CreateDirectory(path);
    return new DataDirectory(path);
});
builder.Services.AddSingleton<AccessTokenService>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventHub>());
builder.Services.AddSingleton<ISysBotTransportFactory, TcpSysBotTransportFactory>();
builder.Services.AddSingleton<INetworkProbe, SystemNetworkProbe>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<NxHubOptions>>().Value.Sessions.ToSessionOptions());
builder.Services.AddSingleton(sp =>
{
    var database = new SqliteDatabase(Path.Combine(sp.GetRequiredService<DataDirectory>().Path, "nxhub.db"));
    database.Initialise();
    return database;
});
builder.Services.AddSingleton<ISwitchDeviceRepository, SqliteSwitchDeviceRepository>();
builder.Services.AddSingleton<IMacroRepository, SqliteMacroRepository>();
builder.Services.AddSingleton<MacroLibrary>();
builder.Services.AddSingleton<DiagnosticsRunner>();

var demo = builder.Configuration.GetValue<bool>($"{NxHubOptions.SectionName}:Demo:Enabled");
if (demo)
{
    // Registered after the SQLite repositories, so these in-memory ones win.
    builder.Services.AddDemoMode();
    builder.Services.PostConfigure<NxHubOptions>(o => o.Auth.Mode = AuthMode.Disabled);
}

builder.Services.AddSingleton<SwitchManager>();
builder.Services.AddHostedService<SwitchManagerHostedService>();

builder.Services.AddProblemDetails(problems => problems.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
});
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new()
    {
        Title = "NX Hub API",
        Version = Endpoints.Version,
        Description = """
            Self-hosted automation gateway for CFW Nintendo Switch consoles running sys-botbase.

            Authenticate with `Authorization: Bearer <token>`. The token is in `<data dir>/admin-token`.
            Errors are RFC 9457 problem details with a stable `code` field.
            Live events: WebSocket `GET /api/events` (see docs/API.md).
            """,
    };
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Admin token from <data dir>/admin-token.",
    };
    document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
    return Task.CompletedTask;
}));

var app = builder.Build();

// Trust X-Forwarded-Proto only from a proxy on this machine (the defaults: loopback), so redirects
// generated behind an HTTPS reverse proxy use https.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });

// Behind a reverse proxy at a sub-path (for example https://example.com/nxhub/). Must run before routing.
var pathBase = app.Configuration[$"{NxHubOptions.SectionName}:PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
{
    app.UsePathBase("/" + pathBase.Trim().Trim('/'));
}

app.UseExceptionHandler();
app.UseRouting();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseMiddleware<TokenAuthMiddleware>();
if (demo)
{
    app.UseMiddleware<DemoGuardMiddleware>();
}

app.MapOpenApi();
app.MapScalarApiReference("/docs", scalar => scalar.WithTitle("NX Hub API"));
app.MapNxHubApi();
app.MapNxHubEvents();
app.MapFallback("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "Not found",
    detail: "No such API endpoint.",
    extensions: new Dictionary<string, object?> { ["code"] = "not_found" }));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallback(async context =>
{
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    context.Response.ContentType = "text/html; charset=utf-8";
    if (index.Exists)
    {
        await context.Response.SendFileAsync(index);
        return;
    }

    await context.Response.WriteAsync("<!doctype html><title>NX Hub</title><p>NX Hub is running. The web UI has not been built; see docs/DEVELOPMENT.md. API docs: <a href=\"/docs\">/docs</a>.</p>");
});

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NxHub");
var dataDirectory = app.Services.GetRequiredService<DataDirectory>();
var auth = app.Services.GetRequiredService<AccessTokenService>();
if (demo)
{
    startupLogger.LogWarning("DEMO MODE: simulated Switch, no login, nothing stored, configuration changes refused.");
}

startupLogger.LogInformation("NX Hub {Version} starting. Data directory: {DataDirectory}. Auth: {AuthMode}", Endpoints.Version, dataDirectory.Path, auth.Enabled ? "token" : "disabled");
if (auth.Enabled)
{
    startupLogger.LogInformation("Admin token file: {TokenFile}", auth.TokenFilePath);
}

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
}

public partial class Program;
