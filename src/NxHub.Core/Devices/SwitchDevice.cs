using NxHub.Core.Errors;
using NxHub.Protocol.Transport;

namespace NxHub.Core.Devices;

/// <summary>A configured Switch. Persisted by <see cref="ISwitchDeviceRepository"/>.</summary>
public sealed record SwitchDevice
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = SwitchEndpoint.DefaultPort;

    /// <summary>When enabled, NX Hub keeps the Switch connected and reconnects automatically.</summary>
    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public SwitchEndpoint Endpoint => new(Host, Port);

    /// <summary>Generates a short, URL-safe identifier such as <c>sw_k3f9x2m7q1ab</c>.</summary>
    public static string NewId() => ShortId.New("sw_");
}

/// <summary>User-supplied fields for creating or replacing a Switch.</summary>
public sealed record SwitchDeviceInput(string? Name, string? Host, int? Port, bool? Enabled)
{
    public const int MaxNameLength = 64;
    public const int MaxHostLength = 253;

    /// <summary>Validates and normalises the input, throwing <see cref="ValidationFailedException"/>.</summary>
    public (string Name, string Host, int Port, bool Enabled) Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var name = Name?.Trim() ?? string.Empty;
        var host = Host?.Trim() ?? string.Empty;
        var port = Port ?? SwitchEndpoint.DefaultPort;

        if (name.Length == 0)
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"Name must be at most {MaxNameLength} characters."];
        }
        else if (name.Any(char.IsControl))
        {
            errors["name"] = ["Name must not contain control characters."];
        }

        if (host.Length == 0)
        {
            errors["host"] = ["Host is required (an IP address or hostname)."];
        }
        else if (host.Length > MaxHostLength || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            errors["host"] = ["Host must be a valid IP address or hostname, without a scheme or port."];
        }

        if (port is < 1 or > 65535)
        {
            errors["port"] = ["Port must be between 1 and 65535."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return (name, host, port, Enabled ?? true);
    }
}

public interface ISwitchDeviceRepository
{
    Task<IReadOnlyList<SwitchDevice>> ListAsync(CancellationToken cancellationToken);

    Task<SwitchDevice?> GetAsync(string id, CancellationToken cancellationToken);

    Task AddAsync(SwitchDevice device, CancellationToken cancellationToken);

    /// <summary>Returns false if the device does not exist.</summary>
    Task<bool> UpdateAsync(SwitchDevice device, CancellationToken cancellationToken);

    /// <summary>Returns false if the device does not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
