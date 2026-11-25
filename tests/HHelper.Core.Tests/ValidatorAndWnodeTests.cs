using System.Buffers.Binary;
using HHelper.Core.Broker;
using HHelper.Core.Wmi;

namespace HHelper.Core.Tests;

public class FanCurveValidatorTests
{
    private static readonly FanCurve Factory = new([50, 55, 60, 65, 70, 75], [0, 40, 50, 60, 70, 80, 100]);

    [Fact]
    public void FactoryCurveIsValid() => Assert.Null(FanCurveValidator.Validate(Factory, Factory, 5));

    [Theory]
    [InlineData(new byte[] { 50, 55, 55, 65, 70, 75 }, "higher")]
    [InlineData(new byte[] { 44, 55, 60, 65, 70, 75 }, "outside")]
    [InlineData(new byte[] { 50, 55, 60, 65, 70, 81 }, "outside")]
    [InlineData(new byte[] { 50, 55, 60, 65, 70 }, "Expected 6")]
    public void BadThresholds(byte[] thresholds, string reason)
        => Assert.Contains(reason, FanCurveValidator.Validate(new FanCurve(thresholds, Factory.Speeds), Factory, 5));

    [Theory]
    [InlineData(new byte[] { 0, 40, 30, 60, 70, 80, 100 }, "must not be lower")]
    [InlineData(new byte[] { 0, 40, 50, 60, 70, 80, 101 }, "above the factory maximum")]
    [InlineData(new byte[] { 0, 40, 50, 60, 70, 80, 90 }, "last speed point")]
    public void BadSpeeds(byte[] speeds, string reason)
        => Assert.Contains(reason, FanCurveValidator.Validate(new FanCurve(Factory.Thresholds, speeds), Factory, 5));

    [Fact]
    public void SpeedBelowFactoryMinimumIsRefused()
    {
        var factory = new FanCurve(Factory.Thresholds, [20, 40, 50, 60, 70, 80, 100]);
        Assert.Contains("below the factory minimum",
            FanCurveValidator.Validate(new FanCurve(Factory.Thresholds, [10, 40, 50, 60, 70, 80, 100]), factory, 5));
    }
}

public class WnodeTests
{
    private static byte[] Header(int size, uint flags)
    {
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), flags);
        return b;
    }

    [Fact]
    public void ParsesSingleInstance()
    {
        var b = Header(80, Wnode.FlagSingleInstance);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), 72); // DataBlockOffset
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), 4);  // SizeDataBlock
        b[72] = 0x3C;
        Assert.Equal(new byte[] { 0x3C, 0, 0, 0 }, Wnode.FirstInstanceData(b));
    }

    [Fact]
    public void ParsesAllDataFixedSize()
    {
        var b = Header(80, Wnode.FlagAllData | Wnode.FlagFixedInstanceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), 64); // DataBlockOffset
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), 1);  // InstanceCount
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), 4);  // FixedInstanceSize
        b[64] = 0x62; b[65] = 0x01;
        Assert.Equal(new byte[] { 0x62, 0x01, 0, 0 }, Wnode.FirstInstanceData(b));
    }

    [Fact]
    public void ParsesAllDataVariableSize()
    {
        var b = Header(88, Wnode.FlagAllData);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), 1);  // InstanceCount
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), 80); // OffsetInstanceDataAndLength[0].Offset
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), 2);  // .Length
        b[80] = 0x60; b[81] = 0x03;
        Assert.Equal(new byte[] { 0x60, 0x03 }, Wnode.FirstInstanceData(b));
    }

    [Fact]
    public void RejectsOutOfBoundsOffsets()
    {
        var b = Header(64, Wnode.FlagSingleInstance);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), 60);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), 8);
        Assert.Throws<FormatException>(() => Wnode.FirstInstanceData(b));
        Assert.Throws<FormatException>(() => Wnode.FirstInstanceData(new byte[10]));
    }
}
