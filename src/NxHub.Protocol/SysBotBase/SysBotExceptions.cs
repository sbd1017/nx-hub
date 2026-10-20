namespace NxHub.Protocol.SysBotBase;

/// <summary>Base type for all sys-botbase client failures.</summary>
public class SysBotException : Exception
{
    public SysBotException()
    {
    }

    public SysBotException(string message)
        : base(message)
    {
    }

    public SysBotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Connecting failed, or an established connection was lost.</summary>
public class SysBotConnectionException : SysBotException
{
    public SysBotConnectionException()
    {
    }

    public SysBotConnectionException(string message)
        : base(message)
    {
    }

    public SysBotConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The module did not respond in time. The connection is no longer trustworthy.</summary>
public class SysBotTimeoutException : SysBotConnectionException
{
    public SysBotTimeoutException()
    {
    }

    public SysBotTimeoutException(string message)
        : base(message)
    {
    }

    public SysBotTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The module responded with something NX Hub could not interpret.</summary>
public class SysBotProtocolException : SysBotException
{
    public SysBotProtocolException()
    {
    }

    public SysBotProtocolException(string message)
        : base(message)
    {
    }

    public SysBotProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
