namespace HHelper.Core.Wmi;

/// <summary>
/// Raw access to WMI data blocks by GUID and instance index (spec 1.2: by GUID, never by class name).
/// Implementations do no permission checks; only <see cref="Broker.MsiBroker"/> may call them.
/// </summary>
public interface IWmiBlockTransport : IDisposable
{
    /// <summary>
    /// Finds the instance naming of the device that hosts the blocks, using one known-safe
    /// (allowlisted, read-only) instance as the probe. Returns false if no device answers.
    /// </summary>
    bool Resolve(Guid probeGuid, int probeInstance);

    /// <summary>The resolved instance name base, for diagnostics; null before <see cref="Resolve"/>.</summary>
    string? InstanceBase { get; }

    /// <summary>Queries one instance and returns its data bytes.</summary>
    byte[] Query(Guid guid, int instance);

    /// <summary>Sets one instance to the given data bytes.</summary>
    void Set(Guid guid, int instance, ReadOnlySpan<byte> data);
}

/// <summary>A WMI event subscription. Each notification's payload is delivered exactly once.</summary>
public interface IWmiEventSource : IDisposable
{
    /// <summary>Starts delivering payloads of the event GUID to <paramref name="onPayload"/>.</summary>
    void Subscribe(Guid eventGuid, Action<byte[]> onPayload);
}

/// <summary>A WMI call failed with a Win32 error code.</summary>
public sealed class WmiException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;

    public const int ErrorAccessDenied = 5;
    public const int ErrorNotSupported = 50;
    public const int ErrorWmiGuidNotFound = 4200;
    public const int ErrorWmiInstanceNotFound = 4201;

    public bool IsAccessDenied => ErrorCode == ErrorAccessDenied;
}
