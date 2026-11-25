using HHelper.Core.Broker;
using HHelper.Core.Profiles;
using HHelper.Core.Settings;
using HHelper.Core.Wmi;

namespace HHelper.Core.Tests;

/// <summary>An in-memory stand-in for the firmware: one value per (GUID, instance), with an access log.</summary>
internal sealed class FakeTransport : IWmiBlockTransport
{
    public readonly Dictionary<(Guid, int), int> Values = new();
    public readonly List<(string Op, Guid Guid, int Instance, int Value)> Log = new();
    public Func<Guid, int, int, int>? OnSetStores; // (guid, instance, value) -> value actually stored
    public Func<Guid, int, int?>? QueryOverride;
    public bool Resolved;
    public int QueryLength = 4;

    public string? InstanceBase => Resolved ? @"ACPI\PNP0C14\FAKE" : null;

    public bool Resolve(Guid probeGuid, int probeInstance)
    {
        Log.Add(("probe", probeGuid, probeInstance, 0));
        Resolved = true;
        return true;
    }

    public byte[] Query(Guid guid, int instance)
    {
        var v = QueryOverride?.Invoke(guid, instance) ?? Values.GetValueOrDefault((guid, instance));
        Log.Add(("query", guid, instance, v));
        var data = new byte[QueryLength];
        BitConverter.GetBytes(v).AsSpan(0, Math.Min(4, QueryLength)).CopyTo(data);
        return data;
    }

    public void Set(Guid guid, int instance, ReadOnlySpan<byte> data)
    {
        var v = data[0];
        Log.Add(("set", guid, instance, v));
        Values[(guid, instance)] = OnSetStores?.Invoke(guid, instance, v) ?? v;
    }

    public IEnumerable<(Guid, int)> Touched => Log.Where(l => l.Op != "probe").Select(l => (l.Guid, l.Instance));

    public void Dispose() { }
}

internal sealed class FakeEvents : IWmiEventSource
{
    public Action<byte[]>? Handler;
    public Guid? Guid;
    public void Subscribe(Guid eventGuid, Action<byte[]> onPayload) { Guid = eventGuid; Handler = onPayload; }
    public void Dispose() { }
}

internal sealed class MemorySnapshots : ISnapshotStore
{
    public readonly Dictionary<string, FanSnapshot> Items = new();
    public FanSnapshot? Load(string fan) => Items.GetValueOrDefault(fan);
    public void Save(FanSnapshot snapshot) => Items[snapshot.Fan] = snapshot;
    public void Delete(string fan) => Items.Remove(fan);
}

internal static class Shibela
{
    public const string Board = "MS-158L";

    public static string ProfileJson()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "profiles", "msi-ms-158l.json")))
            dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir ?? throw new FileNotFoundException("profiles dir"), "profiles", "msi-ms-158l.json"));
    }

    public static CompiledProfile Profile(bool fanWritesEnabled = false)
    {
        var p = ProfileLoader.Parse(ProfileJson());
        p.FanCurves!.WriteEnabled = fanWritesEnabled;
        return CompiledProfile.From(p);
    }

    /// <summary>A plausible factory fan table: thresholds 50..75, speeds 0..100 [values invented for tests].</summary>
    public static void SeedFactoryTable(FakeTransport t)
    {
        byte[] thresholds = [50, 55, 60, 65, 70, 75];
        byte[] speeds = [0, 40, 50, 60, 70, 80, 100];
        foreach (var guid in new[] { SafetyRules.MsiCpu, SafetyRules.MsiVga })
        {
            for (var i = 0; i < 6; i++) t.Values[(guid, 5 + i)] = thresholds[i];
            for (var i = 0; i < 7; i++) t.Values[(guid, 11 + i)] = speeds[i];
        }
    }

    public static (MsiBroker Broker, FakeTransport Transport, FakeEvents Events, MemorySnapshots Snapshots, AppSettings Settings)
        Create(bool fanWritesEnabled = false, bool experimental = false, string board = Board)
    {
        var t = new FakeTransport();
        SeedFactoryTable(t);
        var e = new FakeEvents();
        var s = new MemorySnapshots();
        var settings = new AppSettings { ExperimentalControlsForBoard = experimental ? Board : null };
        var broker = new MsiBroker(Profile(fanWritesEnabled), board, t, e, () => settings, s);
        return (broker, t, e, s, settings);
    }
}
