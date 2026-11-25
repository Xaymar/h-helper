using HHelper.Core.Broker;
using HHelper.Core.Profiles;

namespace HHelper.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void BuiltInProfileLoadsAndMatchesOnlyItsBoard()
    {
        var profiles = ProfileLoader.LoadBuiltIn();
        Assert.Contains(profiles, p => p.Id == "msi-ms-158l");
        Assert.Equal("msi-ms-158l", ProfileLoader.FindMatch(profiles, "MS-158L")?.Id);
        Assert.Equal("msi-ms-158l", ProfileLoader.FindMatch(profiles, " ms-158l ")?.Id);
        Assert.Null(ProfileLoader.FindMatch(profiles, "MS-17L1"));
        Assert.Null(ProfileLoader.FindMatch(profiles, null));
    }

    [Fact]
    public void ShippedProfileNeverListsForbiddenInstances()
    {
        var p = Shibela.Profile();
        Assert.Null(p.Find(SafetyRules.MsiAp, 1));
        Assert.Null(p.Find(SafetyRules.MsiSoftware, 2));
        Assert.All(p.Entries, e => Assert.False(SafetyRules.IsForbiddenInstance(e.BlockGuid, e.Instance)));
    }

    [Fact]
    public void ShippedProfileOnlyMarksFanTablesWritable()
    {
        var writable = Shibela.Profile().Entries.Where(e => e.CanWriteExperimental).ToList();
        Assert.Equal(26, writable.Count); // 2 fans x (6 thresholds + 7 speeds)
        Assert.All(writable, e => Assert.True(SafetyRules.IsWritableCeiling(e.BlockGuid, e.Instance)));
        Assert.False(Shibela.Profile().Source.FanCurves!.WriteEnabled); // spec 3.6: disabled until Q1/Q3
    }

    [Fact]
    public void ShippedProfileMatchesSpecFeatureTable()
    {
        var p = Shibela.Profile();
        Assert.Equal(1, p.Feature("cpu.temperature").Single().Instance);
        Assert.Equal(SafetyRules.MsiVga, p.Feature("gpu.temperature").Single().BlockGuid);
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, p.Feature("cpu.curve.threshold").Select(e => e.Instance));
        Assert.Equal(new[] { 11, 12, 13, 14, 15, 16, 17 }, p.Feature("gpu.curve.speed").Select(e => e.Instance));
        Assert.Equal(28, p.Feature("ec.info").Count);
        Assert.Equal(7, p.Feature("fan.cpu.tach.high").Single().Instance);
        Assert.Equal(4, p.Feature("fan.gpu.tach.low").Single().Instance);
        Assert.Equal(WmiValueKind.S16, p.Feature("battery.main").First().Kind);
        Assert.Equal("brightness", p.EventGroupByCode[0x62]);
        Assert.Equal("modeSelectors", p.EventGroupByCode[0x60]);
    }

    private static string Mutate(string from, string to)
    {
        var json = Shibela.ProfileJson();
        Assert.Contains(from, json);
        return json.Replace(from, to);
    }

    [Fact]
    public void RejectsProfileThatReadsMsiApInstance1()
    {
        var json = Mutate("\"instance\": 7,", "\"instance\": 1,");
        Assert.Throws<ProfileException>(() => ProfileLoader.Compile(json));
    }

    [Fact]
    public void RejectsProfileThatReadsEventCodeRegister()
    {
        var json = Mutate("\"range\": [ 6, 33 ]", "\"range\": [ 2, 33 ]");
        Assert.Throws<ProfileException>(() => ProfileLoader.Compile(json));
    }

    [Fact]
    public void RejectsProfileThatMakesFlagByteWritable()
    {
        var json = Mutate(
            "\"block\": \"MSI_Power\",          \"instance\": 0,",
            "\"block\": \"MSI_Power\",          \"instance\": 0, \"writeExperimental\": true,");
        Assert.Throws<ProfileException>(() => ProfileLoader.Compile(json));
    }

    [Fact]
    public void RejectsForbiddenGuidBlocks()
    {
        var json = Mutate("\"guid\": \"2D3CBA6C-1C9C-7F41-B54C-F5D5D580D482\"", "\"guid\": \"ABBC0F6E-0000-0000-0000-000000000000\"");
        Assert.Throws<ProfileException>(() => ProfileLoader.Compile(json));
        Assert.True(SafetyRules.IsForbiddenGuid(new Guid("ABBC0F6A-1111-2222-3333-444444444444")));
        Assert.False(SafetyRules.IsForbiddenGuid(SafetyRules.MsiCpu));
    }

    [Fact]
    public void RejectsInstanceOutsideBlock()
    {
        var json = Mutate("\"range\": [ 0, 13 ]", "\"range\": [ 0, 14 ]");
        Assert.Throws<ProfileException>(() => ProfileLoader.Compile(json));
    }
}
