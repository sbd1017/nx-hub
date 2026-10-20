using System.Globalization;
using System.Text;

namespace NxHub.Protocol.SysBotBase;

/// <summary>Whether sys-botbase writes a response line for a command.</summary>
public enum ResponseKind
{
    /// <summary>The module writes nothing back (for example <c>press</c>, <c>release</c>, <c>setStick</c>).</summary>
    None,

    /// <summary>The module writes exactly one line terminated by <c>\n</c>.</summary>
    Line,
}

/// <summary>
/// A single, validated sys-botbase command. Instances are only created through
/// <see cref="SysBotCommands"/>. This is the only place raw command strings are built.
/// </summary>
public sealed class SysBotCommand
{
    /// <summary>
    /// Conservative cap on an encoded command line. sys-botbase 2.x reads into a fixed
    /// 21,888-byte buffer without bounds checking, so we never send anything close to that.
    /// </summary>
    public const int MaxEncodedLength = 4096;

    internal SysBotCommand(string name, IReadOnlyList<string> arguments, ResponseKind response, TimeSpan? responseTimeout = null)
    {
        ValidateToken(name);
        foreach (var argument in arguments)
        {
            ValidateToken(argument);
        }

        Name = name;
        Arguments = arguments;
        Response = response;
        ResponseTimeout = responseTimeout;
        Text = arguments.Count == 0 ? name : name + " " + string.Join(' ', arguments);
        if (Text.Length + 2 > MaxEncodedLength)
        {
            throw new ArgumentException($"Encoded command exceeds {MaxEncodedLength} bytes.", nameof(arguments));
        }
    }

    public string Name { get; }

    public IReadOnlyList<string> Arguments { get; }

    public ResponseKind Response { get; }

    /// <summary>Overrides the client's default response timeout for slow commands (screenshots).</summary>
    public TimeSpan? ResponseTimeout { get; }

    /// <summary>The command text without the line terminator, for logs and diagnostics.</summary>
    public string Text { get; }

    /// <summary>
    /// Encodes the command as ASCII terminated by <c>\r\n</c>. sys-botbase-cpp requires
    /// <c>\r\n</c>; sys-botbase 2.x splits on <c>\n</c> and strips <c>\r</c>, so CRLF works for both.
    /// </summary>
    public byte[] Encode() => Encoding.ASCII.GetBytes(Text + "\r\n");

    public override string ToString() => Text;

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("Command tokens must not be empty.", nameof(token));
        }

        foreach (var c in token)
        {
            // Printable ASCII excluding space: tokens are space separated on the wire and
            // control characters (notably CR/LF) would allow command injection.
            if (c is < '!' or > '~')
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Command token contains an invalid character (0x{(int)c:X2})."),
                    nameof(token));
            }
        }
    }
}

/// <summary>Factory for every sys-botbase command NX Hub is allowed to send.</summary>
/// <remarks>
/// Memory read/write commands are deliberately absent until a capability and safety model for
/// them is designed (see docs/ARCHITECTURE.md, "Security").
/// </remarks>
public static class SysBotCommands
{
    public static SysBotCommand Press(SwitchButton button) =>
        new("press", [SysBotBaseInputNames.ToToken(button)], ResponseKind.None);

    public static SysBotCommand Release(SwitchButton button) =>
        new("release", [SysBotBaseInputNames.ToToken(button)], ResponseKind.None);

    /// <summary>
    /// Module-timed click. The hold time is a global module setting (<c>buttonClickSleepTime</c>,
    /// default 50 ms) and it blocks the module's main loop, so NX Hub prefers press/release pairs.
    /// </summary>
    public static SysBotCommand Click(SwitchButton button) =>
        new("click", [SysBotBaseInputNames.ToToken(button)], ResponseKind.None);

    public static SysBotCommand SetStick(SwitchStick stick, StickPosition position) =>
        new(
            "setStick",
            [
                SysBotBaseInputNames.ToToken(stick),
                position.X.ToString(CultureInfo.InvariantCulture),
                position.Y.ToString(CultureInfo.InvariantCulture),
            ],
            ResponseKind.None);

    /// <summary>Detaches the virtual controller. The next controller command re-attaches it.</summary>
    public static SysBotCommand DetachController() => new("detachController", [], ResponseKind.None);

    public static SysBotCommand GetVersion() => new("getVersion", [], ResponseKind.Line);

    public static SysBotCommand GetTitleId() => new("getTitleID", [], ResponseKind.Line);

    public static SysBotCommand GetTitleVersion() => new("getTitleVersion", [], ResponseKind.Line);

    public static SysBotCommand GetBuildId() => new("getBuildID", [], ResponseKind.Line);

    public static SysBotCommand GetSystemLanguage() => new("getSystemLanguage", [], ResponseKind.Line);

    /// <summary>
    /// Current frame as JPEG, hex-encoded on one line (up to ~1 MB of text). The module blocks
    /// while capturing, and upstream notes it can fail unless the debug-unit flag is patched.
    /// </summary>
    public static SysBotCommand PixelPeek() => new("pixelPeek", [], ResponseKind.Line, TimeSpan.FromSeconds(10));

    /// <summary>Turns off command echo so every response line maps to exactly one command.</summary>
    public static SysBotCommand DisableEcho() => new("configure", ["echoCommands", "0"], ResponseKind.None);
}
