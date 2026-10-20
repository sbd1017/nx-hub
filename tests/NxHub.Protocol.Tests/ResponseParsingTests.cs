using NxHub.Protocol.SysBotBase;

namespace NxHub.Protocol.Tests;

public class ResponseParsingTests
{
    [Fact]
    public void Classic_sys_botbase_version_is_recognised()
    {
        var caps = SysBotCapabilities.FromVersion("2.5");
        Assert.Equal(SysBotImplementation.SysBotBase, caps.Implementation);
        Assert.Equal(new Version(2, 5), caps.Version);
        Assert.True(caps.Buttons);
        Assert.True(caps.ClickSequence);
        Assert.False(caps.Ping);
        Assert.False(caps.TickPreciseController);
        Assert.True(caps.IsInferred);
        Assert.Empty(caps.Warnings);
    }

    [Fact]
    public void Cpp_rewrite_version_is_recognised()
    {
        var caps = SysBotCapabilities.FromVersion("3.33\r");
        Assert.Equal(SysBotImplementation.SysBotBaseCpp, caps.Implementation);
        Assert.Equal("3.33", caps.RawVersion);
        Assert.True(caps.Ping);
        Assert.True(caps.TickPreciseController);
        Assert.False(caps.Freeze);
        Assert.Empty(caps.Warnings);
    }

    [Fact]
    public void Cpp_with_backwards_compatibility_disabled_warns()
    {
        var caps = SysBotCapabilities.FromVersion("3.32");
        Assert.Single(caps.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData("2.x")]
    [InlineData("1.2.3.4.5")]
    public void Unknown_versions_fall_back_to_basic_capabilities(string version)
    {
        var caps = SysBotCapabilities.FromVersion(version);
        Assert.Equal(SysBotImplementation.Unknown, caps.Implementation);
        Assert.True(caps.Buttons);
        Assert.NotEmpty(caps.Warnings);
    }

    [Theory]
    [InlineData("0100ABCD12340000", 0x0100ABCD12340000UL)]
    [InlineData("0x0100abcd12340000", 0x0100ABCD12340000UL)]
    [InlineData("  01  ", 1UL)]
    public void Hex_values_parse(string line, ulong expected)
    {
        Assert.Equal(expected, SysBotResponses.ParseHexUInt64OrNull(line));
    }

    [Fact]
    public void Empty_title_line_means_nothing_running()
    {
        Assert.Null(SysBotResponses.ParseHexUInt64OrNull(""));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("0100ABCD123400001")]
    public void Malformed_hex_is_a_protocol_error(string line)
    {
        Assert.Throws<SysBotProtocolException>(() => SysBotResponses.ParseHexUInt64OrNull(line));
    }

    [Fact]
    public void Printable_strips_control_characters_and_truncates()
    {
        var printable = SysBotResponses.Printable("ok\u0001\u0007" + new string('a', 200));
        Assert.StartsWith("okaaa", printable);
        Assert.EndsWith("…", printable);
    }
}
