namespace NxHub.Protocol.Transport;

/// <summary>Where a sys-botbase module can be reached.</summary>
public sealed record SwitchEndpoint(string Host, int Port)
{
    public const int DefaultPort = 6000;

    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// A byte-level, line-oriented connection to a sys-botbase module. Implementations exist for
/// TCP; USB (usb-botbase framing) can be added behind this interface later.
/// </summary>
/// <remarks>Implementations are not thread-safe; <see cref="SysBotBase.SysBotBaseClient"/> serialises access.</remarks>
public interface ISysBotTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Human-readable description, for example <c>tcp://192.0.2.10:6000</c>.</summary>
    string Description { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one response line, without its trailing <c>\n</c> or <c>\r\n</c>.
    /// Throws <see cref="SysBotBase.SysBotConnectionException"/> if the peer closes the connection.
    /// </summary>
    Task<string> ReadLineAsync(int maxLength, CancellationToken cancellationToken);
}

public interface ISysBotTransportFactory
{
    ISysBotTransport Create(SwitchEndpoint endpoint);
}
