namespace HHelper.Core.Broker;

/// <summary>
/// Hard safety rules that hold no matter what a profile says (spec section 2).
/// Profiles are data and can be edited by anyone; these checks are code and are the last line.
/// </summary>
public static class SafetyRules
{
    /// <summary>MSI_AP: reading instance 1 makes the firmware write EC state (spec 2, rule 2).</summary>
    public static readonly Guid MsiAp = new("A1753D7C-B621-DE4A-B41A-55716A0ECE7A");

    /// <summary>MSI_Software: instance 2 is the event code register (spec 2, rule 3).</summary>
    public static readonly Guid MsiSoftware = new("24418D6A-0A79-524C-9AB1-18B78CA68CE7");

    /// <summary>MSI_Power: instance 0 is a whole byte of flags (spec 2, rule 5).</summary>
    public static readonly Guid MsiPower = new("2D3CBA6C-1C9C-7F41-B54C-F5D5D580D482");

    /// <summary>MSI_Device: instance 0 is a whole byte of flags (spec 2, rule 5).</summary>
    public static readonly Guid MsiDevice = new("4AFBD56B-9F91-8F49-81F5-995BA73822AF");

    /// <summary>MSI_CPU: the only block with experimentally writable instances (fan table).</summary>
    public static readonly Guid MsiCpu = new("BD2A216F-2FB9-A640-B807-DDDBAD656891");

    /// <summary>MSI_VGA: the only other block with experimentally writable instances (fan table).</summary>
    public static readonly Guid MsiVga = new("1EC3EC7A-1E9B-E74A-9026-CF122B0BBD21");

    /// <summary>
    /// First 32 bits of GUIDs H-Helper must never open: the MSI_ACPI method class (not backed
    /// by firmware on MS-158L, spec 1.2) and AMD's tuning interface (spec 1.2).
    /// The spec gives only these prefixes, so the check is on Data1.
    /// </summary>
    private static readonly uint[] ForbiddenGuidPrefixes = [0xABBC0F6E, 0xABBC0F6A];

    public static bool IsForbiddenGuid(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        var data1 = BitConverter.ToUInt32(bytes[..4]);
        return ForbiddenGuidPrefixes.Contains(data1);
    }

    /// <summary>Instances that may never be read or written.</summary>
    public static bool IsForbiddenInstance(Guid guid, int instance)
        => IsForbiddenGuid(guid)
           || (guid == MsiAp && instance == 1)
           || (guid == MsiSoftware && instance == 2);

    /// <summary>
    /// The write ceiling: the only instances code will ever write, whatever a profile claims.
    /// Fan table instances 5..17 of MSI_CPU and MSI_VGA (spec 3.4 to 3.6).
    /// </summary>
    public static bool IsWritableCeiling(Guid guid, int instance)
        => (guid == MsiCpu || guid == MsiVga) && instance is >= 5 and <= 17;
}
