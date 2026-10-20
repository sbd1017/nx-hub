using System.Text.Json.Serialization;

namespace NxHub.Protocol;

/// <summary>
/// Buttons on the virtual Pro Controller that sys-botbase attaches to the Switch.
/// Names are NX Hub's own; <see cref="SysBotBase.SysBotBaseInputNames"/> maps them to wire tokens.
/// </summary>
public enum SwitchButton
{
    A,
    B,
    X,
    Y,
    L,
    R,
    ZL,
    ZR,
    Plus,
    Minus,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
    Home,
    Capture,
    LeftStick,
    RightStick,
}

/// <summary>Analog sticks on the virtual controller.</summary>
public enum SwitchStick
{
    Left,
    Right,
}

/// <summary>A raw stick position as sent on the wire (-32768..32767 on each axis).</summary>
public readonly record struct StickPosition(short X, short Y)
{
    public static StickPosition Centre { get; } = new(0, 0);

    [JsonIgnore]
    public bool IsCentred => X == 0 && Y == 0;

    /// <summary>Converts normalised coordinates (-1..1, up is +Y) to a raw position.</summary>
    public static StickPosition FromNormalised(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Stick coordinates must be finite numbers.");
        }

        return new StickPosition(ToAxis(x), ToAxis(y));
    }

    private static short ToAxis(double value)
    {
        var clamped = Math.Clamp(value, -1.0, 1.0);
        return clamped >= 0
            ? (short)Math.Round(clamped * short.MaxValue)
            : (short)Math.Round(clamped * -(double)short.MinValue);
    }
}
