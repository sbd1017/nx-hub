using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using NxHub.Core.Activity;
using NxHub.Core.Errors;
using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;
using NxHub.Protocol.Transport;

namespace NxHub.Core.Diagnostics;

/// <summary>
/// Runs a sequence of setup checks against one Switch. Checks that depend on an earlier failed
/// check are reported as skipped rather than producing a cascade of failures.
/// </summary>
/// <remarks>
/// When the session is connected, protocol checks go through it (sys-botbase-cpp only accepts one
/// client at a time). Otherwise a short-lived connection is opened just for the checks.
/// Diagnostics never write memory and only touch the controller when explicitly asked.
/// </remarks>
public sealed class DiagnosticsRunner
{
    private readonly INetworkProbe _network;
    private readonly ISysBotTransportFactory _transports;
    private readonly SessionOptions _sessionOptions;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;
    private readonly ILogger<DiagnosticsRunner> _logger;
    private readonly ConcurrentDictionary<string, DiagnosticReport> _latest = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _running = new();

    public DiagnosticsRunner(
        INetworkProbe network,
        ISysBotTransportFactory transports,
        SessionOptions sessionOptions,
        IEventPublisher events,
        TimeProvider time,
        ILogger<DiagnosticsRunner> logger)
    {
        _network = network;
        _transports = transports;
        _sessionOptions = sessionOptions;
        _events = events;
        _time = time;
        _logger = logger;
    }

    public DiagnosticReport? GetLatest(string switchId) => _latest.GetValueOrDefault(switchId);

    public void Forget(string switchId) => _latest.TryRemove(switchId, out _);

    public async Task<DiagnosticReport> RunAsync(SwitchSession session, DiagnosticOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        var gate = _running.GetOrAdd(session.SwitchId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new NxHubException(ErrorKind.Conflict, "diagnostics_in_progress", "Diagnostics are already running for this Switch.");
        }

        try
        {
            var report = await RunCoreAsync(session, options, cancellationToken).ConfigureAwait(false);
            _latest[session.SwitchId] = report;
            _events.Publish(new NxHubEvent(EventTypes.DiagnosticsCompleted, session.SwitchId, report.CompletedAt, report));
            var counts = report.Checks.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            session.LogEvent(
                report.Overall == OverallStatus.Ready ? ActivityLevel.Info : ActivityLevel.Warning,
                ActivityCategory.Diagnostics,
                $"Diagnostics: {report.Overall} ({counts.GetValueOrDefault(CheckStatus.Pass)} passed, {counts.GetValueOrDefault(CheckStatus.Warn)} warnings, {counts.GetValueOrDefault(CheckStatus.Fail)} failed).");
            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DiagnosticReport> RunCoreAsync(SwitchSession session, DiagnosticOptions options, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var device = session.Device;
        var checks = new List<DiagnosticCheck>();
        var status = session.GetStatus();
        var connected = status.State == ConnectionState.Connected;

        checks.Add(new DiagnosticCheck(
            "config",
            "Configuration",
            CheckStatus.Pass,
            $"{device.Name} → {device.Host}:{device.Port}",
            0));

        // DNS
        var dns = await TimedAsync(
            "dns",
            "Name resolution",
            async () =>
            {
                if (IPAddress.TryParse(device.Host, out _))
                {
                    return (CheckStatus.Skipped, "An IP address is configured; no lookup needed.", null);
                }

                var addresses = await _network.ResolveAsync(device.Host, cancellationToken).ConfigureAwait(false);
                return addresses.Count == 0
                    ? (CheckStatus.Fail, $"'{device.Host}' did not resolve to any address.", null)
                    : (CheckStatus.Pass, $"Resolved to {string.Join(", ", addresses)}.", null);
            }).ConfigureAwait(false);
        checks.Add(dns);
        var networkOk = dns.Status != CheckStatus.Fail;

        // ICMP is informative only: many networks drop ping.
        checks.Add(networkOk
            ? await TimedAsync(
                "icmp",
                "Host reachable (ping)",
                async () =>
                {
                    var ping = await _network.PingAsync(device.Host, options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
                    return ping.Success
                        ? (CheckStatus.Pass, ping.Message, null)
                        : (CheckStatus.Warn, ping.Message + " Not fatal: ping is often blocked. The TCP check is authoritative.", null);
                }).ConfigureAwait(false)
            : Skipped("icmp", "Host reachable (ping)", "Name resolution failed."));

        // TCP
        DiagnosticCheck tcp;
        if (!networkOk)
        {
            tcp = Skipped("tcp", "TCP port reachable", "Name resolution failed.");
        }
        else if (connected)
        {
            tcp = new DiagnosticCheck("tcp", "TCP port reachable", CheckStatus.Pass, $"Active connection to port {device.Port}.", 0);
        }
        else
        {
            tcp = await TimedAsync(
                "tcp",
                "TCP port reachable",
                async () =>
                {
                    var (success, _, message) = await _network.ProbeTcpAsync(device.Host, device.Port, options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
                    return (success ? CheckStatus.Pass : CheckStatus.Fail, message, null);
                }).ConfigureAwait(false);
        }

        checks.Add(tcp);
        var tcpOk = tcp.Status == CheckStatus.Pass;

        // Protocol checks run through the live session, or through a temporary client.
        SysBotBaseClient? temporary = null;
        try
        {
            Func<SysBotCommand, Task<SysBotReply>> query;
            SysBotCapabilities? capabilities = null;
            DiagnosticCheck handshake;

            if (!tcpOk)
            {
                handshake = Skipped("sysbotbase", "sys-botbase responded", "TCP port is not reachable.");
                query = _ => throw new InvalidOperationException();
            }
            else if (connected)
            {
                query = command => session.QueryAsync(command, cancellationToken);
                handshake = await TimedAsync(
                    "sysbotbase",
                    "sys-botbase responded",
                    async () =>
                    {
                        var reply = await query(SysBotCommands.GetVersion()).ConfigureAwait(false);
                        capabilities = SysBotCapabilities.FromVersion(reply.Line);
                        return (CheckStatus.Pass, $"getVersion → '{capabilities.RawVersion}' in {reply.RoundTrip.TotalMilliseconds:0} ms (via active session).", null);
                    }).ConfigureAwait(false);
            }
            else
            {
                temporary = new SysBotBaseClient(_transports.Create(device.Endpoint), _sessionOptions.Client);
                var client = temporary;
                query = command => client.QueryAsync(command, cancellationToken);
                handshake = await TimedAsync(
                    "sysbotbase",
                    "sys-botbase responded",
                    async () =>
                    {
                        capabilities = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        return (CheckStatus.Pass, $"getVersion → '{capabilities.RawVersion}' (temporary diagnostic connection).", null);
                    }).ConfigureAwait(false);
            }

            checks.Add(handshake);
            var protocolOk = handshake.Status == CheckStatus.Pass && capabilities is not null;

            checks.Add(protocolOk ? CapabilitiesCheck(capabilities!) : Skipped("capabilities", "Version & capabilities", "sys-botbase did not respond."));
            checks.Add(protocolOk ? await LatencyCheckAsync(query, options).ConfigureAwait(false) : Skipped("latency", "Latency", "sys-botbase did not respond."));
            checks.Add(protocolOk ? await TitleCheckAsync(query).ConfigureAwait(false) : Skipped("title", "Running title", "sys-botbase did not respond."));
            checks.Add(await ControllerCheckAsync(session, options, protocolOk, connected, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (temporary is not null)
            {
                await temporary.DisposeAsync().ConfigureAwait(false);
            }
        }

        checks.Add(SessionCheck(session.GetStatus()));

        var overall = checks.Any(c => c.Status == CheckStatus.Fail)
            ? OverallStatus.NotReady
            : checks.Any(c => c.Status == CheckStatus.Warn) ? OverallStatus.Degraded : OverallStatus.Ready;
        return new DiagnosticReport(session.SwitchId, started, _time.GetUtcNow(), overall, checks);
    }

    private static DiagnosticCheck CapabilitiesCheck(SysBotCapabilities capabilities)
    {
        var features = new List<string>();
        if (capabilities.Buttons)
        {
            features.Add("buttons");
        }

        if (capabilities.Sticks)
        {
            features.Add("sticks");
        }

        if (capabilities.Touch)
        {
            features.Add("touch");
        }

        if (capabilities.Screenshot)
        {
            features.Add("screenshot");
        }

        if (capabilities.TickPreciseController)
        {
            features.Add("tick-precise controller");
        }

        var details = new Dictionary<string, string>
        {
            ["implementation"] = capabilities.Implementation.ToString(),
            ["version"] = capabilities.RawVersion,
            ["inferred"] = capabilities.IsInferred ? "true" : "false",
        };
        var message = $"{capabilities.Implementation} {capabilities.RawVersion}. Features (inferred from version): {string.Join(", ", features)}.";
        if (capabilities.Warnings.Count > 0)
        {
            return new DiagnosticCheck("capabilities", "Version & capabilities", CheckStatus.Warn, message + " " + string.Join(" ", capabilities.Warnings), 0, details);
        }

        return new DiagnosticCheck(
            "capabilities",
            "Version & capabilities",
            capabilities.Implementation == SysBotImplementation.Unknown ? CheckStatus.Warn : CheckStatus.Info,
            message,
            0,
            details);
    }

    private async Task<DiagnosticCheck> LatencyCheckAsync(Func<SysBotCommand, Task<SysBotReply>> query, DiagnosticOptions options)
    {
        return await TimedAsync(
            "latency",
            "Latency",
            async () =>
            {
                var samples = new List<double>();
                for (var i = 0; i < Math.Clamp(options.LatencySamples, 1, 20); i++)
                {
                    var reply = await query(SysBotCommands.GetVersion()).ConfigureAwait(false);
                    samples.Add(reply.RoundTrip.TotalMilliseconds);
                }

                var average = samples.Average();
                var details = new Dictionary<string, string>
                {
                    ["samples"] = samples.Count.ToString(CultureInfo.InvariantCulture),
                    ["minMs"] = samples.Min().ToString("0.0", CultureInfo.InvariantCulture),
                    ["avgMs"] = average.ToString("0.0", CultureInfo.InvariantCulture),
                    ["maxMs"] = samples.Max().ToString("0.0", CultureInfo.InvariantCulture),
                };
                var summary = $"min {samples.Min():0} / avg {average:0} / max {samples.Max():0} ms over {samples.Count} round trips.";
                return average > options.LatencyWarnMs
                    ? (CheckStatus.Warn, summary + $" Above {options.LatencyWarnMs:0} ms: timing-sensitive automation may be unreliable. Wired Pi + 5 GHz Wi-Fi helps.", details)
                    : (CheckStatus.Pass, summary, details);
            }).ConfigureAwait(false);
    }

    private async Task<DiagnosticCheck> TitleCheckAsync(Func<SysBotCommand, Task<SysBotReply>> query)
    {
        return await TimedAsync(
            "title",
            "Running title",
            async () =>
            {
                var reply = await query(SysBotCommands.GetTitleId()).ConfigureAwait(false);
                var titleId = SysBotResponses.ParseHexUInt64OrNull(reply.Line);
                return titleId is null or 0
                    ? (CheckStatus.Info, "No game is running (HOME menu or system applet).", null)
                    : (CheckStatus.Info, $"Title ID {titleId:X16}.", new Dictionary<string, string> { ["titleId"] = titleId.Value.ToString("X16", CultureInfo.InvariantCulture) });
            }).ConfigureAwait(false);
    }

    private async Task<DiagnosticCheck> ControllerCheckAsync(SwitchSession session, DiagnosticOptions options, bool protocolOk, bool connected, CancellationToken cancellationToken)
    {
        const string id = "controller";
        const string title = "Controller command";
        if (!options.IncludeControllerTest)
        {
            return Skipped(id, title, "Opt-in. It attaches a virtual Pro Controller to the Switch. Run with the controller test enabled.");
        }

        if (!protocolOk)
        {
            return Skipped(id, title, "sys-botbase did not respond.");
        }

        if (!connected)
        {
            return Skipped(id, title, "Connect the Switch first; the controller test runs through the live session.");
        }

        return await TimedAsync(
            id,
            title,
            async () =>
            {
                var held = session.Controller.GetSnapshot().HeldButtons;
                var button = Enum.GetValues<SwitchButton>().First(b => !held.Contains(b) && b is not SwitchButton.Home and not SwitchButton.Capture);
                await session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.Release(button), ct), cancellationToken).ConfigureAwait(false);
                var reply = await session.QueryAsync(SysBotCommands.GetVersion(), cancellationToken).ConfigureAwait(false);
                return (CheckStatus.Pass,
                    $"Sent a no-op controller update (release {button}) and the module kept responding ({reply.RoundTrip.TotalMilliseconds:0} ms). sys-botbase gives no acknowledgement for input, so confirm visually with the hardware checklist.",
                    null);
            }).ConfigureAwait(false);
    }

    private static DiagnosticCheck SessionCheck(SwitchStatus status)
    {
        var details = new Dictionary<string, string>
        {
            ["state"] = status.State.ToString(),
            ["connections"] = status.ConnectionCount.ToString(CultureInfo.InvariantCulture),
            ["connectionLosses"] = status.ConnectionLossCount.ToString(CultureInfo.InvariantCulture),
        };
        if (status.LastError is { } error)
        {
            details["lastError"] = error.Message;
        }

        var message = $"State {status.State}; {status.ConnectionCount} connection(s), {status.ConnectionLossCount} loss(es) since NX Hub started.";
        if (status.Controller.PendingNeutralise)
        {
            return new DiagnosticCheck("session", "Session health", CheckStatus.Warn, message + " Some inputs may still be held on the Switch; they will be released on reconnect.", 0, details);
        }

        return new DiagnosticCheck(
            "session",
            "Session health",
            status.ConnectionLossCount >= 3 ? CheckStatus.Warn : CheckStatus.Info,
            status.ConnectionLossCount >= 3 ? message + " Frequent drops suggest Wi-Fi or power-saving problems." : message,
            0,
            details);
    }

    private static DiagnosticCheck Skipped(string id, string title, string reason) => new(id, title, CheckStatus.Skipped, reason, 0);

    private async Task<DiagnosticCheck> TimedAsync(
        string id,
        string title,
        Func<Task<(CheckStatus Status, string Message, IReadOnlyDictionary<string, string>? Details)>> check)
    {
        var started = _time.GetTimestamp();
        try
        {
            var (status, message, details) = await check().ConfigureAwait(false);
            return new DiagnosticCheck(id, title, status, message, Elapsed(started), details);
        }
        catch (Exception ex) when (ex is SysBotException or NxHubException or System.Net.Sockets.SocketException)
        {
            _logger.LogInformation("Diagnostic check {CheckId} failed: {Reason}", id, ex.Message);
            return new DiagnosticCheck(id, title, CheckStatus.Fail, ex.Message, Elapsed(started));
        }
    }

    private double Elapsed(long started) => Math.Round(_time.GetElapsedTime(started).TotalMilliseconds, 1);
}
