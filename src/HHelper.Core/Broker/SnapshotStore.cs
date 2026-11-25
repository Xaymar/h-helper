using System.Text.Json;

namespace HHelper.Core.Broker;

/// <summary>Factory values of every register a feature can touch, saved before its first write (spec 2, rule 6).</summary>
public sealed record FanSnapshot(string ProfileId, string BoardId, string Fan, FanCurve Factory, DateTimeOffset TakenAt);

public interface ISnapshotStore
{
    FanSnapshot? Load(string fan);
    void Save(FanSnapshot snapshot);
    void Delete(string fan);
}

/// <summary>Snapshots as JSON files, flushed to disk so they survive a crash.</summary>
public sealed class FileSnapshotStore(string directory) : ISnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "H-Helper", "snapshots");

    private string FileFor(string fan)
    {
        if (fan.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Invalid fan id.", nameof(fan));
        return Path.Combine(directory, $"fan-{fan}.json");
    }

    public FanSnapshot? Load(string fan)
    {
        var file = FileFor(fan);
        if (!File.Exists(file)) return null;
        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), Options);
        return dto is null ? null : new FanSnapshot(dto.ProfileId, dto.BoardId, dto.Fan, new FanCurve(dto.Thresholds, dto.Speeds), dto.TakenAt);
    }

    public void Save(FanSnapshot s)
    {
        Directory.CreateDirectory(directory);
        var file = FileFor(s.Fan);
        var tmp = file + ".tmp";
        var dto = new Dto(s.ProfileId, s.BoardId, s.Fan, s.Factory.Thresholds.ToArray(), s.Factory.Speeds.ToArray(), s.TakenAt);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, dto, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, overwrite: true);
    }

    public void Delete(string fan)
    {
        var file = FileFor(fan);
        if (File.Exists(file)) File.Delete(file);
    }

    private sealed record Dto(string ProfileId, string BoardId, string Fan, byte[] Thresholds, byte[] Speeds, DateTimeOffset TakenAt);
}
