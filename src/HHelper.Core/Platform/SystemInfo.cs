using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HHelper.Core.Platform;

/// <summary>SMBIOS identity of the machine, used for the model gate (spec 2, rule 10).</summary>
public sealed record SystemIdentity(
    string? BaseBoardProduct,
    string? BaseBoardManufacturer,
    string? SystemProductName,
    string? SystemSku,
    string? BiosVersion,
    string? BiosReleaseDate);

[SupportedOSPlatform("windows")]
public static class SystemInfo
{
    /// <summary>Reads the SMBIOS strings Windows mirrors into the registry. No WMI, no firmware calls.</summary>
    public static SystemIdentity Read()
    {
        const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        static string? Get(string name)
            => (Microsoft.Win32.Registry.GetValue(key, name, null) as string)?.Trim();

        return new SystemIdentity(
            Get("BaseBoardProduct"),
            Get("BaseBoardManufacturer"),
            Get("SystemProductName"),
            Get("SystemSKU"),
            Get("BIOSVersion"),
            Get("BIOSReleaseDate"));
    }
}

/// <summary>Read-only access to UEFI variables (spec 4.2: never written).</summary>
[SupportedOSPlatform("windows")]
public static class FirmwareVariables
{
    /// <summary>Reads a UEFI variable, or returns null if it is missing or cannot be read.</summary>
    public static byte[]? TryRead(string name, Guid vendor)
    {
        if (!Privileges.TryEnable("SeSystemEnvironmentPrivilege")) return null;
        var buffer = new byte[4096];
        var length = GetFirmwareEnvironmentVariableExW(name, vendor.ToString("B"), buffer, (uint)buffer.Length, out _);
        return length == 0 ? null : buffer.AsSpan(0, (int)length).ToArray();
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string name, string guid, [Out] byte[] buffer, uint size, out uint attributes);
}

[SupportedOSPlatform("windows")]
internal static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    public static bool TryEnable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, privilege, out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED (1300) means not held
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
}
