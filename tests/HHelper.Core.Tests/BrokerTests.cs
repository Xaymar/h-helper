using HHelper.Core.Broker;
using HHelper.Core.Profiles;

namespace HHelper.Core.Tests;

public class BrokerTests
{
    private static void AssertOnlyAllowlisted(FakeTransport t, CompiledProfile p)
    {
        foreach (var (guid, instance) in t.Touched)
        {
            Assert.False(SafetyRules.IsForbiddenInstance(guid, instance), $"touched forbidden {guid} {instance}");
            Assert.NotNull(p.Find(guid, instance));
        }
    }

    [Fact]
    public void ModelGateRefusesOtherBoardsWithoutTouchingHardware()
    {
        var (broker, t, _, _, _) = Shibela.Create(board: "MS-17L1");
        Assert.Throws<InvalidOperationException>(broker.Start);
        Assert.Empty(t.Log);
    }

    [Fact]
    public void ReadRequestsOnlyTouchAllowlistedInstances()
    {
        var (broker, t, _, _, _) = Shibela.Create();
        broker.Start();
        broker.GetTelemetry();
        broker.GetDiagnostics();
        broker.GetBatteryExtras();
        foreach (var fan in broker.Fans) broker.GetFanCurve(fan);
        AssertOnlyAllowlisted(t, broker.Profile);
        Assert.DoesNotContain(t.Log, l => l.Op == "set");
    }

    [Fact]
    public void TelemetryDecodesValues()
    {
        var (broker, t, _, _, _) = Shibela.Create();
        t.Values[(SafetyRules.MsiCpu, 1)] = 61;
        t.Values[(SafetyRules.MsiVga, 1)] = 48;
        t.Values[(SafetyRules.MsiCpu, 2)] = 35;
        t.Values[(SafetyRules.MsiAp, 7)] = 0x0B;
        t.Values[(SafetyRules.MsiAp, 6)] = 0x54;
        t.Values[(SafetyRules.MsiPower, 0)] = 0b101;
        broker.Start();
        var tel = broker.GetTelemetry();
        Assert.Equal(61, tel.CpuTemperatureC);
        Assert.Equal(48, tel.GpuTemperatureC);
        Assert.Equal(35, tel.CpuFanSpeedRaw);
        Assert.Equal(0x0B54, tel.CpuFanTach.Raw);
        Assert.False(tel.CpuFanTach.Unstable);
        Assert.True(tel.OnAc);
    }

    [Fact]
    public void TachReportsUnstableWhenReadsNeverAgree()
    {
        var (broker, t, _, _, _) = Shibela.Create();
        var counter = 0;
        t.QueryOverride = (g, i) => g == SafetyRules.MsiAp && i == 6 ? counter++ : null;
        broker.Start();
        var tel = broker.GetTelemetry();
        Assert.True(tel.CpuFanTach.Unstable);
        Assert.Null(tel.CpuFanTach.Raw);
    }

    [Fact]
    public void TachRetriesUntilTwoReadsMatch()
    {
        var (broker, t, _, _, _) = Shibela.Create();
        var reads = 0;
        // First pair tears (low byte changes between reads), second attempt is stable.
        t.QueryOverride = (g, i) => g == SafetyRules.MsiAp && i == 4 ? (++reads <= 1 ? 0x10 : 0x20) : null;
        broker.Start();
        Assert.Equal(0x20, broker.GetTelemetry().GpuFanTach.Raw);
    }

    [Fact]
    public void EcInfoIsReadOnceAtStart()
    {
        var (broker, t, _, _, _) = Shibela.Create();
        var text = "158LEMS1.105";
        for (var i = 0; i < text.Length; i++) t.Values[(SafetyRules.MsiSoftware, 6 + i)] = text[i];
        broker.Start();
        Assert.Equal(text, broker.GetDiagnostics().EcFirmware);
        var ecReads = t.Log.Count(l => l.Guid == SafetyRules.MsiSoftware);
        broker.GetDiagnostics();
        Assert.Equal(ecReads, t.Log.Count(l => l.Guid == SafetyRules.MsiSoftware));
    }

    [Fact]
    public void EcInfoFallsBackToHex()
    {
        Assert.Equal("ABC", MsiBroker.DecodeEcInfo("ABC\0\0"u8));
        Assert.Equal("4101FF", MsiBroker.DecodeEcInfo(new byte[] { 0x41, 0x01, 0xFF }));
    }

    [Fact]
    public void GraphicsVariableDecodes()
    {
        var def = Shibela.Profile().Source.Graphics;
        var data = new byte[8];
        data[4] = 4; data[6] = 1;
        Assert.Equal("hybrid, primary integrated", MsiBroker.DecodeGraphics(data, def));
        data[4] = 0; data[6] = 2;
        Assert.Equal("disabled, primary external", MsiBroker.DecodeGraphics(data, def));
        Assert.StartsWith("unknown", MsiBroker.DecodeGraphics(null, def));
    }

    [Fact]
    public void EventsDecodeCodeAndArgument()
    {
        var (broker, _, events, _, _) = Shibela.Create();
        var seen = new List<MsiEvent>();
        broker.EventReceived += seen.Add;
        broker.Start();
        Assert.Equal(new Guid("5B3CC38A-40D9-7245-8AE6-1145B751BE3F"), events.Guid);
        events.Handler!([0x62, 0x05, 0x00, 0x00]);
        events.Handler!([0x99, 0x00, 0x00, 0x00]);
        Assert.Equal(0x62, seen[0].Code);
        Assert.Equal(0x05, seen[0].Argument);
        Assert.Equal("brightness", seen[0].Group);
        Assert.Null(seen[1].Group);
    }

    [Fact]
    public void FanCurveWriteRefusedWhileProfileDisablesIt()
    {
        var (broker, t, _, snaps, _) = Shibela.Create(fanWritesEnabled: false, experimental: true);
        broker.Start();
        var curve = broker.GetFanCurve("cpu");
        Assert.Throws<WriteRefusedException>(() => broker.SetFanCurve("cpu", curve));
        Assert.DoesNotContain(t.Log, l => l.Op == "set");
        Assert.Empty(snaps.Items);
    }

    [Fact]
    public void FanCurveWriteRefusedWithoutExperimentalSwitch()
    {
        var (broker, t, _, _, _) = Shibela.Create(fanWritesEnabled: true, experimental: false);
        broker.Start();
        Assert.Throws<WriteRefusedException>(() => broker.SetFanCurve("cpu", broker.GetFanCurve("cpu")));
        Assert.DoesNotContain(t.Log, l => l.Op == "set");
    }

    [Fact]
    public void ExperimentalSwitchMustNameThisBoard()
    {
        var (broker, t, _, _, settings) = Shibela.Create(fanWritesEnabled: true);
        settings.ExperimentalControlsForBoard = "MS-17L1";
        broker.Start();
        Assert.False(broker.ExperimentalEnabled);
        Assert.Throws<WriteRefusedException>(() => broker.SetFanCurve("cpu", broker.GetFanCurve("cpu")));
    }

    [Fact]
    public void FanCurveWriteSnapshotsWritesSpeedsFirstAndVerifies()
    {
        var (broker, t, _, snaps, _) = Shibela.Create(fanWritesEnabled: true, experimental: true);
        broker.Start();
        var factory = broker.GetFanCurve("cpu");
        var curve = new FanCurve([52, 57, 62, 67, 72, 77], [0, 45, 55, 65, 75, 85, 100]);
        broker.SetFanCurve("cpu", curve);

        Assert.Equal(factory, snaps.Items["cpu"].Factory, new CurveComparer());
        var sets = t.Log.Where(l => l.Op == "set").ToList();
        Assert.Equal(13, sets.Count);
        Assert.All(sets.Take(7), s => Assert.InRange(s.Instance, 11, 17));
        Assert.All(sets.Skip(7), s => Assert.InRange(s.Instance, 5, 10));
        Assert.All(sets, s => Assert.Equal(SafetyRules.MsiCpu, s.Guid));
        Assert.Equal(curve, broker.GetFanCurve("cpu"), new CurveComparer());
        AssertOnlyAllowlisted(t, broker.Profile);
    }

    [Fact]
    public void InvalidCurveIsRefusedBeforeAnyWrite()
    {
        var (broker, t, _, _, _) = Shibela.Create(fanWritesEnabled: true, experimental: true);
        broker.Start();
        var weaker = new FanCurve([50, 55, 60, 65, 70, 75], [0, 40, 50, 60, 70, 80, 90]);
        var ex = Assert.Throws<WriteRefusedException>(() => broker.SetFanCurve("cpu", weaker));
        Assert.Contains("last speed point", ex.Message);
        Assert.DoesNotContain(t.Log, l => l.Op == "set");
    }

    [Fact]
    public void VerifyFailureRestoresFactoryTable()
    {
        var (broker, t, _, snaps, _) = Shibela.Create(fanWritesEnabled: true, experimental: true);
        broker.Start();
        var factory = broker.GetFanCurve("gpu");
        var fail = true;
        // The third user write does not stick; restores (value equal to factory) do.
        t.OnSetStores = (g, i, v) => (fail && i == 13 && v == 55) ? 0 : v;
        var curve = new FanCurve([50, 55, 60, 65, 70, 75], [0, 45, 55, 65, 75, 85, 100]);
        Assert.Throws<WriteVerifyFailedException>(() => broker.SetFanCurve("gpu", curve));
        fail = false;
        Assert.Equal(factory, broker.GetFanCurve("gpu"), new CurveComparer());
        Assert.Empty(snaps.Items); // fully restored, snapshot removed
    }

    [Fact]
    public void StartRestoresLeftoverSnapshotUnlessKeepCurve()
    {
        var (broker, t, _, snaps, _) = Shibela.Create();
        var factory = new FanCurve([50, 55, 60, 65, 70, 75], [0, 40, 50, 60, 70, 80, 100]);
        snaps.Save(new FanSnapshot("msi-ms-158l", Shibela.Board, "cpu", factory, DateTimeOffset.Now));
        t.Values[(SafetyRules.MsiCpu, 12)] = 47; // a custom value left behind by a crash
        broker.Start();
        Assert.Equal(40, t.Values[(SafetyRules.MsiCpu, 12)]);
        Assert.Empty(snaps.Items);

        var (broker2, t2, _, snaps2, settings2) = Shibela.Create();
        settings2.KeepFanCurveOnExit = true;
        snaps2.Save(new FanSnapshot("msi-ms-158l", Shibela.Board, "cpu", factory, DateTimeOffset.Now));
        broker2.Start();
        Assert.DoesNotContain(t2.Log, l => l.Op == "set");
        Assert.Single(snaps2.Items);
    }

    [Fact]
    public void SnapshotFromAnotherBoardIsNeverWritten()
    {
        var (broker, t, _, snaps, _) = Shibela.Create();
        snaps.Save(new FanSnapshot("msi-ms-158l", "MS-17L1", "cpu",
            new FanCurve([1, 2, 3, 4, 5, 6], [1, 2, 3, 4, 5, 6, 7]), DateTimeOffset.Now));
        broker.Start();
        Assert.DoesNotContain(t.Log, l => l.Op == "set");
    }

    [Fact]
    public void WriteBufferMatchesQueryShape()
    {
        var (broker, t, _, _, _) = Shibela.Create(fanWritesEnabled: true, experimental: true);
        t.QueryLength = 1;
        broker.Start();
        broker.SetFanCurve("cpu", broker.GetFanCurve("cpu"));
        Assert.Contains(t.Log, l => l.Op == "set");
    }

    private sealed class CurveComparer : IEqualityComparer<FanCurve>
    {
        public bool Equals(FanCurve? a, FanCurve? b)
            => a is not null && b is not null && a.Thresholds.SequenceEqual(b.Thresholds) && a.Speeds.SequenceEqual(b.Speeds);
        public int GetHashCode(FanCurve c) => 0;
    }
}
