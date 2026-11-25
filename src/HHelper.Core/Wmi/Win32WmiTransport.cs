using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HHelper.Core.Wmi;

/// <summary>
/// WMI data-block access through the documented Win32 WMI API in advapi32
/// (WmiOpenBlock, WmiQuerySingleInstance, WmiSetSingleInstance). Works by GUID, so it does
/// not need the MSI class descriptions (MOF) that MSI Center installs (spec 1.2).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WmiTransport : IWmiBlockTransport
{
    private const uint WmiGuidQuery = 0x0001;
    private const uint WmiGuidSet = 0x0002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private readonly Dictionary<(Guid, uint), IntPtr> _handles = new();
    private readonly string? _instanceBaseOverride;
    private string? _instanceBase;

    /// <param name="instanceBaseOverride">
    /// Optional fixed instance name base (the hosting device's instance path). Instance n is
    /// addressed as "{base}_{n}". Leave null to discover it with <see cref="Resolve"/>.
    /// </param>
    public Win32WmiTransport(string? instanceBaseOverride = null)
        => _instanceBaseOverride = instanceBaseOverride;

    public string? InstanceBase => _instanceBase;

    public bool Resolve(Guid probeGuid, int probeInstance)
    {
        var candidates = _instanceBaseOverride is not null
            ? [_instanceBaseOverride]
            : AcpiWmiDevices.EnumerateInstanceIds();

        foreach (var candidate in candidates)
        {
            var result = TryQuery(probeGuid, $"{candidate}_{probeInstance}", out _);
            if (result == ErrorSuccess)
            {
                _instanceBase = candidate;
                return true;
            }
            if (result == WmiException.ErrorAccessDenied)
                throw new WmiException("Access denied while opening the MSI WMI blocks. H-Helper must run elevated (and SYSTEM may be required, spec Q2).", result);
        }
        return false;
    }

    public byte[] Query(Guid guid, int instance)
    {
        var name = InstanceName(instance);
        var result = TryQuery(guid, name, out var data);
        if (result != ErrorSuccess)
            throw new WmiException($"Query {guid} instance {instance} failed with error {result}.", result);
        return data!;
    }

    public void Set(Guid guid, int instance, ReadOnlySpan<byte> data)
    {
        var handle = Open(guid, WmiGuidQuery | WmiGuidSet);
        var buffer = data.ToArray();
        var result = (int)Native.WmiSetSingleInstanceW(handle, InstanceName(instance), 0, (uint)buffer.Length, buffer);
        if (result != ErrorSuccess)
            throw new WmiException($"Set {guid} instance {instance} failed with error {result}.", result);
    }

    private string InstanceName(int instance)
        => $"{_instanceBase ?? throw new InvalidOperationException("Call Resolve first.")}_{instance}";

    private int TryQuery(Guid guid, string instanceName, out byte[]? data)
    {
        data = null;
        IntPtr handle;
        try { handle = Open(guid, WmiGuidQuery); }
        catch (WmiException e) { return e.ErrorCode; }

        uint size = 256;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            var result = (int)Native.WmiQuerySingleInstanceW(handle, instanceName, ref size, buffer);
            if (result == ErrorInsufficientBuffer) continue;
            if (result != ErrorSuccess) return result;
            data = Wnode.FirstInstanceData(buffer);
            return ErrorSuccess;
        }
        return ErrorInsufficientBuffer;
    }

    /// <summary>Opening a block enables collection for "expensive" blocks (spec 1.1).</summary>
    private IntPtr Open(Guid guid, uint access)
    {
        if (_handles.TryGetValue((guid, access), out var existing)) return existing;
        var g = guid;
        var result = (int)Native.WmiOpenBlock(ref g, access, out var handle);
        if (result != ErrorSuccess)
            throw new WmiException($"Opening WMI block {guid} failed with error {result}.", result);
        _handles[(guid, access)] = handle;
        return handle;
    }

    public void Dispose()
    {
        foreach (var handle in _handles.Values) Native.WmiCloseBlock(handle);
        _handles.Clear();
    }

    private static class Native
    {
        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint WmiOpenBlock(ref Guid guid, uint desiredAccess, out IntPtr dataBlockHandle);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        public static extern uint WmiCloseBlock(IntPtr dataBlockHandle);

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern uint WmiQuerySingleInstanceW(IntPtr dataBlockHandle, string instanceName, ref uint bufferSize, [Out] byte[] buffer);

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern uint WmiSetSingleInstanceW(IntPtr dataBlockHandle, string instanceName, uint reserved, uint valueBufferSize, byte[] valueBuffer);
    }
}

/// <summary>Lists the ACPI WMI mapper devices (PNP0C14) so their instance paths can be tried as instance name bases.</summary>
[SupportedOSPlatform("windows")]
internal static class AcpiWmiDevices
{
    private const uint FilterEnumerator = 0x00000001;
    private const uint FilterPresent = 0x00000100;
    private const string Prefix = @"ACPI\PNP0C14\";

    public static IReadOnlyList<string> EnumerateInstanceIds()
    {
        const uint flags = FilterEnumerator | FilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out var length, "ACPI", flags) != 0 || length == 0) return [];
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW("ACPI", buffer, length, flags) != 0) return [];
        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint bufferLength, uint flags);
}
