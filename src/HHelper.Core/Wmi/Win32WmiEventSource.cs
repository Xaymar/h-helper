using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HHelper.Core.Wmi;

/// <summary>
/// Receives WMI event notifications through WmiNotificationRegistration (advapi32).
/// The payload arrives inside the notification, so each event is read exactly once and the
/// event data block is never queried by H-Helper itself (spec 2, rule 3).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WmiEventSource : IWmiEventSource
{
    private const uint NotificationCallbackDirect = 0x00000004;

    private readonly List<(Guid Guid, Callback Keepalive)> _registrations = new();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Callback(IntPtr wnode, UIntPtr context);

    public void Subscribe(Guid eventGuid, Action<byte[]> onPayload)
    {
        Callback callback = (wnode, _) =>
        {
            try
            {
                var size = Marshal.ReadInt32(wnode);
                if (size < Wnode.HeaderSize || size > 64 * 1024) return;
                var copy = new byte[size];
                Marshal.Copy(wnode, copy, 0, size);
                onPayload(Wnode.FirstInstanceData(copy));
            }
            catch (Exception)
            {
                // Never let an exception unwind into the WMI notification thread.
            }
        };

        var guid = eventGuid;
        var result = WmiNotificationRegistrationW(ref guid, true, Marshal.GetFunctionPointerForDelegate(callback), UIntPtr.Zero, NotificationCallbackDirect);
        if (result != 0)
            throw new WmiException($"Registering for WMI event {eventGuid} failed with error {result}.", (int)result);
        _registrations.Add((eventGuid, callback));
    }

    public void Dispose()
    {
        foreach (var (guid, callback) in _registrations)
        {
            var g = guid;
            WmiNotificationRegistrationW(ref g, false, Marshal.GetFunctionPointerForDelegate(callback), UIntPtr.Zero, NotificationCallbackDirect);
        }
        _registrations.Clear();
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WmiNotificationRegistrationW(
        ref Guid guid,
        [MarshalAs(UnmanagedType.U1)] bool enable,
        IntPtr deliveryInfo,
        UIntPtr deliveryContext,
        uint flags);
}
