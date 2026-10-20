using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NxHub.Server.Auth;

/// <summary>
/// Manages the single admin bearer token. Milestone 1 has one administrator; the token is generated
/// on first start and stored in the data directory with owner-only permissions. It is never logged.
/// </summary>
public sealed class AccessTokenService
{
    public const string TokenFileName = "admin-token";

    private readonly byte[]? _tokenHash;
    private readonly string? _trustedProxyUserHeader;

    public AccessTokenService(IOptions<NxHubOptions> options, DataDirectory dataDirectory, ILogger<AccessTokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var auth = options.Value.Auth;
        Enabled = auth.Mode == AuthMode.Token;
        _trustedProxyUserHeader = string.IsNullOrWhiteSpace(auth.TrustedProxyUserHeader) ? null : auth.TrustedProxyUserHeader.Trim();
        TokenFilePath = Path.Combine(dataDirectory.Path, TokenFileName);
        if (!Enabled)
        {
            logger.LogWarning("API authentication is DISABLED (NxHub:Auth:Mode=Disabled). Anyone who can reach this server can control your Switch.");
            return;
        }

        var token = string.IsNullOrWhiteSpace(auth.Token) ? LoadOrCreate(TokenFilePath, logger) : auth.Token.Trim();
        if (token.Length < 16)
        {
            throw new InvalidOperationException("NxHub:Auth:Token must be at least 16 characters.");
        }

        _tokenHash = Hash(token);
    }

    public bool Enabled { get; }

    public string TokenFilePath { get; }

    public bool Validate(string? presented)
    {
        if (!Enabled)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presented) || presented.Length > 512)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Hash(presented), _tokenHash);
    }

    /// <summary>
    /// True when a trusted reverse proxy on this machine vouches for the user. The proxy must
    /// overwrite the header on every request (nginx <c>proxy_set_header</c>).
    /// </summary>
    public bool IsTrustedProxyUser(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_trustedProxyUserHeader is null || context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote))
        {
            return false;
        }

        var user = context.Request.Headers[_trustedProxyUserHeader].ToString();
        return user.Length is > 0 and <= 256;
    }

    /// <summary>
    /// Browser same-origin check for requests authenticated by a cookie-backed proxy session,
    /// which (unlike a bearer token) other websites could otherwise trigger. Non-browser clients
    /// send neither header and are allowed.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none"))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the token from an <c>Authorization: Bearer …</c> header value.</summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        const string prefix = "Bearer ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16)
            {
                return existing;
            }
        }

        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, fileOptions))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(token);
        }

        logger.LogWarning("Generated a new admin token in {TokenFile} (owner-only permissions). Read it with sudo cat.", path);
        return token;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The resolved, created data directory.</summary>
public sealed record DataDirectory(string Path);
