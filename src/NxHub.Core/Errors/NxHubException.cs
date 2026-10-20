namespace NxHub.Core.Errors;

/// <summary>Broad error categories. The server maps each to an HTTP status.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    NotConnected,
    SwitchTimeout,
    SwitchConnection,
    SwitchProtocol,
    Unsupported,
}

/// <summary>
/// An expected, user-facing failure with a stable machine-readable <see cref="Code"/>.
/// Anything that is not an <see cref="NxHubException"/> is treated as an internal error.
/// </summary>
public class NxHubException : Exception
{
    public NxHubException(ErrorKind kind, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    public NxHubException()
        : this(ErrorKind.Validation, "error", "An error occurred.")
    {
    }

    public NxHubException(string message)
        : this(ErrorKind.Validation, "error", message)
    {
    }

    public NxHubException(string message, Exception innerException)
        : this(ErrorKind.Validation, "error", message, innerException)
    {
    }

    public ErrorKind Kind { get; }

    /// <summary>Stable snake_case identifier, for example <c>switch_not_connected</c>.</summary>
    public string Code { get; }
}

public sealed class ValidationFailedException : NxHubException
{
    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base(ErrorKind.Validation, "validation_failed", "One or more fields are invalid.")
    {
        Errors = errors;
    }

    public ValidationFailedException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class Errors
{
    public static NxHubException SwitchNotFound(string id) =>
        new(ErrorKind.NotFound, "switch_not_found", $"No Switch with id '{id}' exists.");

    public static NxHubException NotConnected(string name) =>
        new(ErrorKind.NotConnected, "switch_not_connected", $"'{name}' is not connected. Connect it first.");

    public static NxHubException Timeout(string name, Exception inner) =>
        new(ErrorKind.SwitchTimeout, "switch_timeout", $"'{name}' did not respond in time: {inner.Message}", inner);

    public static NxHubException ConnectionLost(string name, Exception inner) =>
        new(ErrorKind.SwitchConnection, "switch_connection_lost", $"Lost connection to '{name}': {inner.Message}", inner);

    public static NxHubException Protocol(string name, Exception inner) =>
        new(ErrorKind.SwitchProtocol, "switch_protocol_error", $"Unexpected response from '{name}': {inner.Message}", inner);
}
