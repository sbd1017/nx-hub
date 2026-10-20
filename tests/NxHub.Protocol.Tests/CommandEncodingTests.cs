using System.Text;
using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Protocol.Tests;

public class CommandEncodingTests
{
    [Theory]
    [InlineData(SwitchButton.A, "press A")]
    [InlineData(SwitchButton.Plus, "press PLUS")]
    [InlineData(SwitchButton.DpadUp, "press DUP")]
    [InlineData(SwitchButton.DpadLeft, "press DLEFT")]
    [InlineData(SwitchButton.Home, "press HOME")]
    [InlineData(SwitchButton.LeftStick, "press LSTICK")]
    public void Press_encodes_wire_token(SwitchButton button, string expected)
    {
        Assert.Equal(expected, SysBotCommands.Press(button).Text);
    }

    [Fact]
    public void Commands_are_ascii_with_crlf_terminator()
    {
        var bytes = SysBotCommands.Release(SwitchButton.ZR).Encode();
        Assert.Equal("release ZR\r\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void SetStick_uses_invariant_signed_decimal()
    {
        var command = SysBotCommands.SetStick(SwitchStick.Right, new StickPosition(-32768, 32767));
        Assert.Equal("setStick RIGHT -32768 32767", command.Text);
        Assert.Equal(ResponseKind.None, command.Response);
    }

    [Fact]
    public void Query_commands_expect_a_line_and_input_commands_do_not()
    {
        Assert.Equal(ResponseKind.Line, SysBotCommands.GetVersion().Response);
        Assert.Equal(ResponseKind.Line, SysBotCommands.GetTitleId().Response);
        Assert.Equal(ResponseKind.None, SysBotCommands.Press(SwitchButton.A).Response);
        Assert.Equal(ResponseKind.None, SysBotCommands.DetachController().Response);
        Assert.Equal(ResponseKind.None, SysBotCommands.DisableEcho().Response);
    }

    [Fact]
    public void Every_button_has_a_distinct_known_token()
    {
        string[] upstreamTokens =
        [
            "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "DUP", "DDOWN", "DLEFT", "DRIGHT",
            "HOME", "CAPTURE", "LSTICK", "RSTICK",
        ];
        var tokens = Enum.GetValues<SwitchButton>().Select(SysBotBaseInputNames.ToToken).ToArray();
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Contains(t, upstreamTokens));
    }

    [Theory]
    [InlineData("press A\r\nrelease B")]
    [InlineData("press A")]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("é")]
    public void Invalid_tokens_are_rejected_to_prevent_injection(string token)
    {
        Assert.Throws<ArgumentException>(() => new SysBotCommand("press", [token], ResponseKind.None));
    }

    [Fact]
    public void Oversized_commands_are_rejected()
    {
        var huge = new string('A', SysBotCommand.MaxEncodedLength);
        Assert.Throws<ArgumentException>(() => new SysBotCommand("peek", [huge], ResponseKind.Line));
    }

    [Theory]
    [InlineData(0.0, 0.0, 0, 0)]
    [InlineData(1.0, -1.0, 32767, -32768)]
    [InlineData(2.0, -5.0, 32767, -32768)]
    [InlineData(0.5, -0.5, 16384, -16384)]
    public void Normalised_stick_positions_map_to_raw_axes(double x, double y, short rawX, short rawY)
    {
        Assert.Equal(new StickPosition(rawX, rawY), StickPosition.FromNormalised(x, y));
    }

    [Fact]
    public void Non_finite_stick_positions_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StickPosition.FromNormalised(double.NaN, 0));
    }
}
