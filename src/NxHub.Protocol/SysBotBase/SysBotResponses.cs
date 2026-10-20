using System.Globalization;

namespace NxHub.Protocol.SysBotBase;

/// <summary>Parsers for sys-botbase response lines. Inputs have already had CR/LF removed.</summary>
public static class SysBotResponses
{
    /// <summary>
    /// Parses a hex <c>u64</c> such as a title ID (<c>%016lX</c>). An empty line means
    /// "no application running" and returns <see langword="null"/>.
    /// </summary>
    public static ulong? ParseHexUInt64OrNull(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length > 16
            || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw new SysBotProtocolException($"Expected a hexadecimal 64-bit value but received '{Truncate(line)}'.");
        }

        return value;
    }

    /// <summary>Returns a trimmed, printable form of a response for logs and diagnostics.</summary>
    public static string Printable(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var chars = line.Trim().Where(c => c is >= ' ' and <= '~').ToArray();
        return Truncate(new string(chars));
    }

    internal static string Truncate(string text, int max = 80) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
