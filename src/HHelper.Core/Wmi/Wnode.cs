using System.Buffers.Binary;

namespace HHelper.Core.Wmi;

/// <summary>
/// Parses the WNODE buffers returned by the Win32 WMI data-block API (wmistr.h layout).
/// Kept free of P/Invoke so it can be unit tested anywhere.
/// </summary>
public static class Wnode
{
    // WNODE_HEADER: BufferSize(4) ProviderId(4) Version/Linkage(8) TimeStamp(8) Guid(16) ClientContext(4) Flags(4)
    public const int HeaderSize = 48;
    private const int FlagsOffset = 44;

    public const uint FlagAllData = 0x00000001;
    public const uint FlagSingleInstance = 0x00000002;
    public const uint FlagSingleItem = 0x00000004;
    public const uint FlagEventItem = 0x00000008;
    public const uint FlagFixedInstanceSize = 0x00000010;
    public const uint FlagEventReference = 0x00002000;

    public static uint Flags(ReadOnlySpan<byte> wnode) => U32(wnode, FlagsOffset);

    public static Guid Guid(ReadOnlySpan<byte> wnode) => new(wnode.Slice(24, 16));

    /// <summary>
    /// Returns the data of the first (or only) instance in a WNODE_SINGLE_INSTANCE or WNODE_ALL_DATA.
    /// </summary>
    public static byte[] FirstInstanceData(ReadOnlySpan<byte> wnode)
    {
        if (wnode.Length < HeaderSize) throw new FormatException("WNODE shorter than its header.");
        var size = U32(wnode, 0);
        if (size < HeaderSize || size > wnode.Length) throw new FormatException("WNODE size field is out of range.");
        wnode = wnode[..(int)size];
        var flags = Flags(wnode);

        if ((flags & FlagEventReference) != 0)
            throw new FormatException("WNODE is an event reference; the payload was too large to deliver.");

        if ((flags & FlagSingleInstance) != 0)
        {
            // WNODE_SINGLE_INSTANCE: OffsetInstanceName(4) InstanceIndex(4) DataBlockOffset(4) SizeDataBlock(4)
            var offset = U32(wnode, HeaderSize + 8);
            var length = U32(wnode, HeaderSize + 12);
            return Slice(wnode, offset, length);
        }

        if ((flags & FlagAllData) != 0)
        {
            // WNODE_ALL_DATA: DataBlockOffset(4) InstanceCount(4) OffsetInstanceNameOffsets(4) then
            // FixedInstanceSize(4) or OffsetInstanceDataAndLength[InstanceCount] {Offset(4), Length(4)}
            var dataBlockOffset = U32(wnode, HeaderSize);
            var count = U32(wnode, HeaderSize + 4);
            if (count == 0) throw new FormatException("WNODE_ALL_DATA has no instances.");
            if ((flags & FlagFixedInstanceSize) != 0)
                return Slice(wnode, dataBlockOffset, U32(wnode, HeaderSize + 12));
            return Slice(wnode, U32(wnode, HeaderSize + 12), U32(wnode, HeaderSize + 16));
        }

        throw new FormatException($"Unsupported WNODE flags 0x{flags:X8}.");
    }

    private static uint U32(ReadOnlySpan<byte> b, int offset)
    {
        if (offset < 0 || offset + 4 > b.Length) throw new FormatException("WNODE field outside the buffer.");
        return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4));
    }

    private static byte[] Slice(ReadOnlySpan<byte> b, uint offset, uint length)
    {
        if (offset > b.Length || length > b.Length - offset) throw new FormatException("WNODE data outside the buffer.");
        return b.Slice((int)offset, (int)length).ToArray();
    }
}
