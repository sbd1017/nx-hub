using NxHub.Core.Sessions;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Server;

public enum AuthMode
{
    /// <summary>Every API call needs the admin bearer token (default).</summary>
    Token,

    /// <summary>No authentication. Only for loopback-only or otherwise isolated deployments.</summary>
    Disabled,
}

/// <summary>Bound from the <c>NxHub</c> configuration section (appsettings.json or <c>NxHub__*</c> env vars).</summary>
public sealed class NxHubOptions
{
    public const string SectionName = "NxHub";

    /// <summary>
    /// Where the database and admin token live. Defaults to <c>$STATE_DIRECTORY</c> (set by systemd's
    /// <c>StateDirectory=</c>) or <c>./data</c>.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Optional URL prefix when served behind a reverse proxy under a sub-path, for example
    /// <c>/nxhub</c>. Requests without the prefix (direct LAN access) keep working.
    /// </summary>
    public string? PathBase { get; set; }

    public AuthOptions Auth { get; set; } = new();

    public UiOptions Ui { get; set; } = new();

    /// <summary>
    /// Public demo: a simulated Switch runs in-process, nothing is stored, there is no login, and
    /// configuration changes and saving are refused. Never point a demo at real hardware.
    /// </summary>
    public DemoOptions Demo { get; set; } = new();

    public SessionSettings Sessions { get; set; } = new();

    public string ResolveDataDirectory(string contentRoot)
    {
        var configured = DataDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var state = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
            configured = string.IsNullOrWhiteSpace(state) ? Path.Combine(contentRoot, "data") : state.Split(':')[0];
        }

        return Path.GetFullPath(configured, contentRoot);
    }
}

public enum UiTheme
{
    Dark,
    Light,
    System,
}

public sealed class DemoOptions
{
    public bool Enabled { get; set; }
}

public sealed class UiOptions
{
    /// <summary>Web UI theme. Dark by default; <c>Light</c> or <c>System</c> (follow the device) are opt-in.</summary>
    public UiTheme Theme { get; set; } = UiTheme.Dark;

    /// <summary>
    /// Other pages to link from the top bar, for example a trade bot running beside NX Hub.
    /// Only site paths (<c>/…</c>) and http(s) URLs are shown.
    /// </summary>
    public List<UiLink> Links { get; set; } = [];
}

public sealed class UiLink
{
    /// <summary>Shown in the top bar, for example "Trade bot". When a program holding the Switch uses the same name, its hand-over notice links here too.</summary>
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    public bool IsValid() =>
        Name.Trim().Length is > 0 and <= 40
        && ((Url.StartsWith('/') && !Url.StartsWith("//", StringComparison.Ordinal))
            || (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)));
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Token;

    /// <summary>Optional fixed token (for example from a secret store). Otherwise one is generated on first start.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Header set by a trusted reverse proxy that has already authenticated the user (for example
    /// <c>X-Site-User</c> from nginx <c>auth_request</c>). Only honoured on connections from the
    /// loopback address, so LAN clients cannot spoof it. Off when null.
    /// </summary>
    public string? TrustedProxyUserHeader { get; set; }
}

/// <summary>Operator-tunable session settings, in friendly units.</summary>
public sealed class SessionSettings
{
    public double ConnectTimeoutSeconds { get; set; } = 5;

    public double ResponseTimeoutSeconds { get; set; } = 3;

    public double HeartbeatSeconds { get; set; } = 10;

    public double ReconnectInitialSeconds { get; set; } = 1;

    public double ReconnectMaxSeconds { get; set; } = 30;

    public double MaxHoldSeconds { get; set; } = 30;

    public NeutraliseMode NeutraliseOnConnect { get; set; } = NeutraliseMode.OnlyIfHeld;

    public NxHub.Core.Sessions.SessionOptions ToSessionOptions() => new()
    {
        Client = new SysBotClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
            ResponseTimeout = TimeSpan.FromSeconds(ResponseTimeoutSeconds),
        },
        HeartbeatInterval = TimeSpan.FromSeconds(HeartbeatSeconds),
        ReconnectInitialDelay = TimeSpan.FromSeconds(ReconnectInitialSeconds),
        ReconnectMaxDelay = TimeSpan.FromSeconds(ReconnectMaxSeconds),
        Controller = new ControllerOptions
        {
            DefaultMaxHold = TimeSpan.FromSeconds(MaxHoldSeconds),
            NeutraliseOnConnect = NeutraliseOnConnect,
        },
    };
}
