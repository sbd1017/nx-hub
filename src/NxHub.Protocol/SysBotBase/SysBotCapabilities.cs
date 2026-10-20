using System.Globalization;

namespace NxHub.Protocol.SysBotBase;

/// <summary>Which sys-botbase implementation appears to be running on the Switch.</summary>
public enum SysBotImplementation
{
    Unknown,

    /// <summary>olliz0r/sys-botbase (C). Reports 1.x or 2.x from <c>getVersion</c>.</summary>
    SysBotBase,

    /// <summary>PokemonAutomation/sys-botbase-cpp. Reports 3.x from <c>getVersion</c>.</summary>
    SysBotBaseCpp,
}

/// <summary>
/// Capabilities inferred from the module's <c>getVersion</c> response.
/// </summary>
/// <remarks>
/// sys-botbase has no capability-discovery command, and unknown commands produce <em>no</em>
/// response (so probing them would hang until timeout). Capabilities are therefore inferred
/// from the version string using what each upstream implementation is documented to support.
/// They are a best guess. <see cref="IsInferred"/> is always true until a real capability
/// handshake exists upstream.
/// </remarks>
public sealed record SysBotCapabilities
{
    public required string RawVersion { get; init; }

    public Version? Version { get; init; }

    public SysBotImplementation Implementation { get; init; }

    public bool IsInferred { get; init; } = true;

    public bool Buttons { get; init; }

    public bool Sticks { get; init; }

    public bool Touch { get; init; }

    public bool Keyboard { get; init; }

    /// <summary><c>pixelPeek</c>. Upstream notes it can fail unless the debug-unit flag is patched.</summary>
    public bool Screenshot { get; init; }

    public bool ClickSequence { get; init; }

    public bool Freeze { get; init; }

    /// <summary>sys-botbase-cpp's <c>ping</c> echo command.</summary>
    public bool Ping { get; init; }

    /// <summary>sys-botbase-cpp's tick-precise <c>cq*</c> controller queue.</summary>
    public bool TickPreciseController { get; init; }

    /// <summary>Warnings worth surfacing to the user (for example a configuration NX Hub cannot use).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static SysBotCapabilities FromVersion(string rawVersion)
    {
        ArgumentNullException.ThrowIfNull(rawVersion);
        var trimmed = rawVersion.Trim();
        var version = TryParseVersion(trimmed);

        if (version is null)
        {
            return new SysBotCapabilities
            {
                RawVersion = trimmed,
                Implementation = SysBotImplementation.Unknown,
                Buttons = true,
                Sticks = true,
                Warnings = ["Unrecognised sys-botbase version string; assuming basic controller support only."],
            };
        }

        if (version.Major >= 3)
        {
            var warnings = new List<string>();
            // sys-botbase-cpp reports 3.33 with backwards compatibility on and 3.32 with it off.
            // With it off, responses become raw binary that is not line-safe.
            if (version is { Major: 3, Minor: 32 })
            {
                warnings.Add("sys-botbase-cpp appears to have backwards compatibility disabled (3.32). NX Hub requires it enabled (the default).");
            }

            return new SysBotCapabilities
            {
                RawVersion = trimmed,
                Version = version,
                Implementation = SysBotImplementation.SysBotBaseCpp,
                Buttons = true,
                Sticks = true,
                Touch = true,
                Keyboard = true,
                Screenshot = true,
                ClickSequence = false,
                Freeze = false,
                Ping = true,
                TickPreciseController = true,
                Warnings = warnings,
            };
        }

        return new SysBotCapabilities
        {
            RawVersion = trimmed,
            Version = version,
            Implementation = SysBotImplementation.SysBotBase,
            Buttons = true,
            Sticks = true,
            Touch = version.Major >= 2,
            Keyboard = version.Major >= 2,
            Screenshot = true,
            ClickSequence = version.Major >= 2,
            Freeze = version.Major >= 2,
            Ping = false,
            TickPreciseController = false,
        };
    }

    private static Version? TryParseVersion(string text)
    {
        if (text.Length is 0 or > 32)
        {
            return null;
        }

        // Accept "2.5", "3.33", "2.4.1"; reject anything with non-numeric parts.
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers.Length switch
        {
            1 => new Version(numbers[0], 0),
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
    }
}
