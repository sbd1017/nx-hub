using System.Security.Cryptography;

namespace NxHub.Core;

/// <summary>Short, URL-safe, unambiguous identifiers such as <c>sw_k3f9x2m7q1ab</c>.</summary>
public static class ShortId
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string New(string prefix, int length = 12)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return prefix + new string(chars);
    }
}
