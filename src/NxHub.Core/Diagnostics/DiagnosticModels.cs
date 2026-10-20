namespace NxHub.Core.Diagnostics;

public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Skipped,
    Info,
}

public enum OverallStatus
{
    /// <summary>Everything required passed.</summary>
    Ready,

    /// <summary>Usable, but at least one warning.</summary>
    Degraded,

    /// <summary>A required check failed.</summary>
    NotReady,
}

public sealed record DiagnosticCheck(
    string Id,
    string Title,
    CheckStatus Status,
    string Message,
    double DurationMs,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record DiagnosticReport(
    string SwitchId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    OverallStatus Overall,
    IReadOnlyList<DiagnosticCheck> Checks);

public sealed record DiagnosticOptions
{
    /// <summary>
    /// Sends a harmless controller update. This attaches sys-botbase's virtual controller to the
    /// Switch, which shows up as a new controller, so it is opt-in.
    /// </summary>
    public bool IncludeControllerTest { get; init; }

    public int LatencySamples { get; init; } = 5;

    public double LatencyWarnMs { get; init; } = 150;

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
