namespace WTT.Domain.Exceptions;

/// <summary>Base class for all application-specific exceptions.</summary>
public abstract class WttException : Exception
{
    protected WttException(string message) : base(message) { }
    protected WttException(string message, Exception? inner) : base(message, inner) { }
}

public sealed class PlcConnectionException : WttException
{
    public string DeviceName { get; }
    public PlcConnectionException(string deviceName, string message, Exception? inner = null)
        : base($"[{deviceName}] {message}", inner) => DeviceName = deviceName;
}

public sealed class PlcReadException : WttException
{
    public PlcReadException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PlcWriteException : WttException
{
    public PlcWriteException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PlcConfigurationException : WttException
{
    public PlcConfigurationException(string message) : base(message) { }
}

public sealed class AuthenticationException : WttException
{
    public AuthenticationException(string message) : base(message) { }
}

public sealed class AuthorizationException : WttException
{
    public AuthorizationException(string message) : base(message) { }
}

public sealed class NotFoundException : WttException
{
    public NotFoundException(string entity, object key) : base($"{entity} '{key}' was not found.") { }
}

public sealed class DataTransferException : WttException
{
    public DataTransferException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ExternalQueryException : WttException
{
    public ExternalQueryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Business-rule violation surfaced to the user (already user-friendly).</summary>
public sealed class BusinessRuleException : WttException
{
    public BusinessRuleException(string message) : base(message) { }
}
