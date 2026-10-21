using System.ComponentModel;
using NxHub.Core.Devices;
using NxHub.Core.Sessions;
using NxHub.Protocol;

namespace NxHub.Server.Api;

/// <summary>A configured Switch together with its live status.</summary>
public sealed record SwitchResponse(
    string Id,
    string Name,
    string Host,
    int Port,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SwitchStatus Status)
{
    public static SwitchResponse From(SwitchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var d = session.Device;
        return new SwitchResponse(d.Id, d.Name, d.Host, d.Port, d.Enabled, d.CreatedAt, d.UpdatedAt, session.GetStatus());
    }
}

/// <summary>Fields for creating or replacing a Switch. <c>port</c> defaults to 6000 and <c>enabled</c> to true.</summary>
public sealed record SwitchRequest(
    [property: Description("Friendly name, 1–64 characters.")] string? Name,
    [property: Description("IP address or hostname of the Switch on your local network.")] string? Host,
    [property: Description("sys-botbase TCP port. Default 6000.")] int? Port,
    [property: Description("When true NX Hub keeps the Switch connected automatically.")] bool? Enabled)
{
    public SwitchDeviceInput ToInput() => new(Name, Host, Port, Enabled);
}

public sealed record PressRequest(
    [property: Description("Button to press.")] SwitchButton? Button,
    [property: Description("How long to hold before releasing, in ms. Default 100, range 20–10000.")] int? DurationMs);

public sealed record HoldRequest(
    SwitchButton? Button,
    [property: Description("Safety limit: auto-release after this many ms. Default 30000.")] int? MaxHoldMs);

public sealed record ReleaseRequest(SwitchButton? Button);

public sealed record StickRequest(
    SwitchStick? Stick,
    [property: Description("Horizontal position, -1 (left) to 1 (right).")] double? X,
    [property: Description("Vertical position, -1 (down) to 1 (up).")] double? Y,
    [property: Description("Optional: return to centre after this many ms.")] int? DurationMs,
    [property: Description("Safety limit when no duration is given: auto-centre after this many ms.")] int? MaxHoldMs);

public sealed record ReleaseAllRequest(
    [property: Description("Release every button and centre both sticks, not just inputs NX Hub knows are held. Default true.")] bool? Everything);

public sealed record LeaseRequest(
    [property: Description("Who is taking the Switch, shown in NX Hub (for example \"Trade bot\"). 1–64 characters.")] string? Holder,
    [property: Description("The lease ends this many seconds after the last renewal unless renewed. Default 60, range 5–3600.")] int? TtlSeconds);

public sealed record DiagnosticsRequest(
    [property: Description("Also send a harmless controller update. Attaches a virtual controller to the Switch.")] bool? IncludeControllerTest);

/// <summary>Run a saved macro (<c>macroId</c>) or a one-off definition (<c>macro</c>).</summary>
public sealed record RunMacroRequest(string? MacroId, NxHub.Core.Macros.MacroInput? Macro);

/// <summary>The current or most recent macro run on a Switch, or null if none since start.</summary>
public sealed record MacroRunResponse(NxHub.Core.Macros.MacroRunState? Run);

public sealed record HealthResponse(string Status);

public sealed record InfoResponse(string Name, string Version, bool AuthRequired, int Switches, UiTheme Theme, bool Demo);
