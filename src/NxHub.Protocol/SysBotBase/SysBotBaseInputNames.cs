namespace NxHub.Protocol.SysBotBase;

/// <summary>
/// Wire tokens for controller inputs. Both sys-botbase (2.x) and sys-botbase-cpp (3.x) accept
/// these exact, case-sensitive tokens.
/// </summary>
/// <remarks>
/// sys-botbase 2.x silently treats an unknown button token as <c>A</c>, so NX Hub must only ever
/// emit tokens from this table. See docs/PROTOCOL.md.
/// </remarks>
public static class SysBotBaseInputNames
{
    public static string ToToken(SwitchButton button) => button switch
    {
        SwitchButton.A => "A",
        SwitchButton.B => "B",
        SwitchButton.X => "X",
        SwitchButton.Y => "Y",
        SwitchButton.L => "L",
        SwitchButton.R => "R",
        SwitchButton.ZL => "ZL",
        SwitchButton.ZR => "ZR",
        SwitchButton.Plus => "PLUS",
        SwitchButton.Minus => "MINUS",
        SwitchButton.DpadUp => "DUP",
        SwitchButton.DpadDown => "DDOWN",
        SwitchButton.DpadLeft => "DLEFT",
        SwitchButton.DpadRight => "DRIGHT",
        SwitchButton.Home => "HOME",
        SwitchButton.Capture => "CAPTURE",
        SwitchButton.LeftStick => "LSTICK",
        SwitchButton.RightStick => "RSTICK",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown button."),
    };

    public static string ToToken(SwitchStick stick) => stick switch
    {
        SwitchStick.Left => "LEFT",
        SwitchStick.Right => "RIGHT",
        _ => throw new ArgumentOutOfRangeException(nameof(stick), stick, "Unknown stick."),
    };
}
