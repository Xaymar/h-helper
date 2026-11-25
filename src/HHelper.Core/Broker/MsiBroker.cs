using System.Buffers.Binary;
using System.Text;
using HHelper.Core.Profiles;
using HHelper.Core.Settings;
using HHelper.Core.Wmi;

namespace HHelper.Core.Broker;

/// <summary>
/// The privileged component (spec 1.3 and 5.1). It holds the allowlist and is the only code that
/// touches the WMI transport. The UI may only use the typed requests below; there is deliberately
/// no "read or write any instance" method.
/// </summary>
public sealed class MsiBroker : IDisposable
{
    private readonly object _gate = new();
    private readonly CompiledProfile _profile;
    private readonly IWmiBlockTransport _transport;
    private readonly IWmiEventSource? _events;
    private readonly Func<AppSettings> _settings;
    private readonly ISnapshotStore _snapshots;
    private readonly Func<byte[]?> _readGraphicsVariable;
    private string? _ecFirmware;
    private string _graphicsMode = "unknown";
    private bool _started;

    public MsiBroker(
        CompiledProfile profile,
        string boardId,
        IWmiBlockTransport transport,
        IWmiEventSource? events,
        Func<AppSettings> settings,
        ISnapshotStore snapshots,
        Func<byte[]?>? readGraphicsVariable = null)
    {
        _profile = profile;
        BoardId = boardId;
        _transport = transport;
        _events = events;
        _settings = settings;
        _snapshots = snapshots;
        _readGraphicsVariable = readGraphicsVariable ?? (() => null);
    }

    public string BoardId { get; }
    public CompiledProfile Profile => _profile;

    /// <summary>Raised on the WMI notification thread for every MSI event (spec 3.8).</summary>
    public event Action<MsiEvent>? EventReceived;

    /// <summary>Messages about restores and refused or failed writes, for the UI log.</summary>
    public event Action<string>? Log;

    public IReadOnlyCollection<string> Fans => (IReadOnlyCollection<string>?)_profile.Source.FanCurves?.Fans.Keys ?? [];

    public bool FanCurveWritesEnabledByProfile => _profile.Source.FanCurves?.WriteEnabled == true;

    public string? FanCurveWriteBlockedReason => _profile.Source.FanCurves?.WriteBlockedReason;

    public bool ExperimentalEnabled => _settings().ExperimentalEnabledFor(BoardId);

    /// <summary>Spec 5.1 "on start": model gate, open blocks, read EC info, crash recovery.</summary>
    public void Start()
    {
        // Model gate (spec 2, rule 10).
        if (!_profile.Source.Match.BaseBoardProduct.Any(b => string.Equals(b, BoardId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Unsupported model: board '{BoardId}' is not covered by profile '{_profile.Id}'.");

        var probe = _profile.InstanceProbe ?? throw new InvalidOperationException("Profile has no instance probe.");
        lock (_gate)
        {
            if (!_transport.Resolve(probe.BlockGuid, probe.Instance))
                throw new InvalidOperationException("No ACPI WMI device answered for this model's data blocks.");
            _started = true;
        }

        _ecFirmware = ReadEcInfo();
        _graphicsMode = DecodeGraphics(_readGraphicsVariable(), _profile.Source.Graphics);

        if (_events is not null && _profile.EventGuid is Guid eventGuid)
            _events.Subscribe(eventGuid, OnEventPayload);

        // Crash recovery (spec 3.6 and 5.4): a snapshot on disk means a custom curve may still be active.
        if (!_settings().KeepFanCurveOnExit)
            foreach (var fan in Fans)
                if (_snapshots.Load(fan) is not null)
                    RestoreFactory(fan);
    }

    // ---- Requests (spec 5.1) ------------------------------------------------------------

    /// <summary>Spec 5.2. Call from one poll thread only.</summary>
    public Telemetry GetTelemetry()
    {
        var power = TryRead("power.flags");
        return new Telemetry(
            DateTimeOffset.Now,
            TryRead("cpu.temperature"),
            TryRead("gpu.temperature"),
            TryRead("cpu.fanSpeed"),
            TryRead("gpu.fanSpeed"),
            ReadPair("fan.cpu.tach.high", "fan.cpu.tach.low"),
            ReadPair("fan.gpu.tach.high", "fan.gpu.tach.low"),
            power is int p ? (p & 0x01) != 0 : null);
    }

    /// <summary>Raw battery instances for diagnostics (spec 3.2, 3.3). Units are unconfirmed (Q5).</summary>
    public (IReadOnlyList<RawValue> Main, IReadOnlyList<RawValue> Second) GetBatteryExtras()
    {
        var main = ReadRaw("battery.main").Concat(ReadRaw("battery.main.extra")).ToList();
        return (main, ReadRaw("battery.second"));
    }

    public Diagnostics GetDiagnostics() => new(
        _profile.Id,
        BoardId,
        _ecFirmware,
        TryRead("mode.a"),
        TryRead("mode.b"),
        TryRead("device.flags"),
        TryRead("display.brightness"),
        TryRead("keys.winFnSwapCandidate"),
        _graphicsMode,
        _transport.InstanceBase);

    public FanCurve GetFanCurve(string fan)
    {
        var def = FanDef(fan);
        return new FanCurve(
            _profile.Feature(def.Thresholds).Select(e => (byte)ReadInstance(e)).ToArray(),
            _profile.Feature(def.Speeds).Select(e => (byte)ReadInstance(e)).ToArray());
    }

    /// <summary>Spec 5.4. Refuses unless the profile and the experimental switch both allow it.</summary>
    public void SetFanCurve(string fan, FanCurve curve)
    {
        if (!FanCurveWritesEnabledByProfile)
            throw new WriteRefusedException(FanCurveWriteBlockedReason ?? "Fan curve writes are disabled for this model.");
        if (!ExperimentalEnabled)
            throw new WriteRefusedException("Experimental controls are off. Turn them on in Settings by typing this laptop's board ID.");

        var def = FanDef(fan);
        var thresholdEntries = _profile.Feature(def.Thresholds);
        var speedEntries = _profile.Feature(def.Speeds);

        var snapshot = _snapshots.Load(fan);
        if (snapshot is null)
        {
            snapshot = new FanSnapshot(_profile.Id, BoardId, fan, GetFanCurve(fan), DateTimeOffset.Now);
            _snapshots.Save(snapshot);
        }

        var reason = FanCurveValidator.Validate(curve, snapshot.Factory, _profile.Source.FanCurves!.ThresholdMarginC);
        if (reason is not null) throw new WriteRefusedException(reason);

        try
        {
            for (var i = 0; i < speedEntries.Count; i++) WriteInstance(speedEntries[i], curve.Speeds[i], restoring: false);
            for (var i = 0; i < thresholdEntries.Count; i++) WriteInstance(thresholdEntries[i], curve.Thresholds[i], restoring: false);
            Log?.Invoke($"Applied {fan} curve {curve}.");
        }
        catch (Exception e)
        {
            Log?.Invoke($"Writing the {fan} curve failed ({e.Message}); restoring factory values.");
            RestoreFactory(fan);
            throw;
        }
    }

    /// <summary>
    /// Writes a fan's factory snapshot back and deletes it if every value verifies. Restoring is
    /// always allowed, even with experimental controls off, because it only returns to factory state.
    /// </summary>
    /// <returns>True if a snapshot existed and was fully restored.</returns>
    public bool RestoreFactory(string fan)
    {
        var snapshot = _snapshots.Load(fan);
        if (snapshot is null) return false;
        if (!string.Equals(snapshot.BoardId, BoardId, StringComparison.OrdinalIgnoreCase) || snapshot.ProfileId != _profile.Id)
        {
            Log?.Invoke($"Ignoring the {fan} snapshot: it was taken on {snapshot.BoardId}/{snapshot.ProfileId}.");
            return false;
        }

        var def = FanDef(fan);
        var speeds = _profile.Feature(def.Speeds);
        var thresholds = _profile.Feature(def.Thresholds);
        var ok = true;
        for (var i = 0; i < speeds.Count; i++) ok &= TryRestore(speeds[i], snapshot.Factory.Speeds[i]);
        for (var i = 0; i < thresholds.Count; i++) ok &= TryRestore(thresholds[i], snapshot.Factory.Thresholds[i]);

        if (ok)
        {
            _snapshots.Delete(fan);
            Log?.Invoke($"Restored factory {fan} curve {snapshot.Factory}.");
        }
        else
        {
            Log?.Invoke($"Restoring the factory {fan} curve did not verify; the snapshot is kept for another try.");
        }
        return ok;
    }

    /// <summary>Restores every fan that has a snapshot (spec 3.6), unless the user chose to keep the curve.</summary>
    public void OnExit()
    {
        if (!_started || _settings().KeepFanCurveOnExit) return;
        foreach (var fan in Fans) RestoreFactory(fan);
    }

    public bool HasSnapshot(string fan) => _snapshots.Load(fan) is not null;

    public void Dispose()
    {
        _events?.Dispose();
        lock (_gate) _transport.Dispose();
    }

    // ---- Internals ----------------------------------------------------------------------

    private FanDefinition FanDef(string fan)
        => _profile.Source.FanCurves?.Fans.GetValueOrDefault(fan)
           ?? throw new ArgumentException($"Unknown fan '{fan}'.", nameof(fan));

    private bool TryRestore(AllowlistEntry entry, byte value)
    {
        try { WriteInstance(entry, value, restoring: true); return true; }
        catch (Exception e) { Log?.Invoke($"Restore of {entry.BlockName} {entry.Instance} failed: {e.Message}"); return false; }
    }

    private int? TryRead(string feature)
    {
        var entries = _profile.Feature(feature);
        if (entries.Count != 1) return null;
        try { return ReadInstance(entries[0]); }
        catch (WmiException) { return null; }
    }

    private IReadOnlyList<RawValue> ReadRaw(string feature)
        => _profile.Feature(feature).Select(e =>
        {
            int? v;
            try { v = ReadInstance(e); } catch (WmiException) { v = null; }
            return new RawValue(e.Feature, e.BlockName, e.Instance, v, e.Spec, e.Note);
        }).ToList();

    /// <summary>Spec 5.2 read_pair: high byte first, read twice, accept only matching reads.</summary>
    private TachReading ReadPair(string highFeature, string lowFeature)
    {
        var high = _profile.Feature(highFeature);
        var low = _profile.Feature(lowFeature);
        if (high.Count != 1 || low.Count != 1) return new TachReading(null, false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var a = (ReadInstance(high[0]) << 8) | ReadInstance(low[0]);
                var b = (ReadInstance(high[0]) << 8) | ReadInstance(low[0]);
                if (a == b) return new TachReading(a, false);
            }
            return new TachReading(null, true);
        }
        catch (WmiException) { return new TachReading(null, false); }
    }

    private string? ReadEcInfo()
    {
        var entries = _profile.Feature("ec.info");
        if (entries.Count == 0) return null;
        try { return DecodeEcInfo(entries.Select(e => (byte)ReadInstance(e)).ToArray()); }
        catch (WmiException) { return null; }
    }

    /// <summary>Spec 3.11: printable ASCII or zero is shown as text, anything else as hex.</summary>
    public static string DecodeEcInfo(ReadOnlySpan<byte> bytes)
    {
        var printable = true;
        foreach (var b in bytes) printable &= b == 0 || (b >= 0x20 && b < 0x7F);
        if (printable)
        {
            var text = new StringBuilder();
            foreach (var b in bytes) text.Append(b == 0 ? ' ' : (char)b);
            return text.ToString().Trim();
        }
        return Convert.ToHexString(bytes);
    }

    /// <summary>Spec 4.2: read-only decode of the AMD setup variable.</summary>
    public static string DecodeGraphics(byte[]? data, GraphicsDefinition? def)
    {
        if (def is null) return "not described for this model";
        if (data is null) return "unknown (variable not readable)";
        string Field(int offset, Dictionary<string, string> values)
            => offset < data.Length
                ? values.GetValueOrDefault(data[offset].ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? $"value {data[offset]}"
                : "out of range";
        return $"{Field(def.ModeOffset, def.ModeValues)}, primary {Field(def.PrimaryOffset, def.PrimaryValues)}";
    }

    private void OnEventPayload(byte[] payload)
    {
        // Spec 3.8 and 5.3: byte 0 is the code, byte 1 the argument. The payload is never fetched any other way.
        var code = payload.Length > 0 ? payload[0] : (byte)0;
        var arg = payload.Length > 1 ? payload[1] : (byte)0;
        var group = _profile.EventGroupByCode.GetValueOrDefault(code);
        EventReceived?.Invoke(new MsiEvent(DateTimeOffset.Now, code, arg, group));
    }

    /// <summary>Spec 5.1 read_instance.</summary>
    private int ReadInstance(AllowlistEntry requested)
    {
        var entry = _profile.Find(requested.BlockGuid, requested.Instance);
        if (entry is null || SafetyRules.IsForbiddenInstance(requested.BlockGuid, requested.Instance))
            throw new InvalidOperationException($"Refused read of {requested.BlockName} instance {requested.Instance}: not allowlisted.");

        byte[] data;
        lock (_gate)
        {
            if (!_started) throw new InvalidOperationException("Broker not started.");
            data = _transport.Query(entry.BlockGuid, entry.Instance);
        }
        return Decode(entry.Kind, data);
    }

    /// <summary>Spec 5.1 write_instance with verify (spec 2, rule 7).</summary>
    private void WriteInstance(AllowlistEntry requested, byte value, bool restoring)
    {
        var entry = _profile.Find(requested.BlockGuid, requested.Instance);
        if (entry is null || !entry.CanWriteExperimental
            || SafetyRules.IsForbiddenInstance(entry.BlockGuid, entry.Instance)
            || !SafetyRules.IsWritableCeiling(entry.BlockGuid, entry.Instance)
            || entry.Kind != WmiValueKind.U8)
            throw new WriteRefusedException($"Refused write of {requested.BlockName} instance {requested.Instance}: not writable.");
        if (!restoring && !ExperimentalEnabled)
            throw new WriteRefusedException("Experimental controls are off.");

        lock (_gate)
        {
            if (!_started) throw new InvalidOperationException("Broker not started.");
            // Write a buffer the same size the firmware returns for this instance (the value in
            // byte 0, little endian, rest zero), so the set matches the query's shape.
            var size = Math.Max(1, _transport.Query(entry.BlockGuid, entry.Instance).Length);
            var buffer = new byte[size];
            buffer[0] = value;
            _transport.Set(entry.BlockGuid, entry.Instance, buffer);
        }
        var readBack = ReadInstance(entry);
        if (readBack != value)
            throw new WriteVerifyFailedException($"{entry.BlockName} instance {entry.Instance}: wrote {value}, read back {readBack}.");
    }

    internal static int Decode(WmiValueKind kind, byte[] data) => kind switch
    {
        WmiValueKind.U8 when data.Length >= 1 => data[0],
        WmiValueKind.S16 when data.Length >= 2 => BinaryPrimitives.ReadInt16LittleEndian(data),
        _ => throw new WmiException($"Instance returned {data.Length} bytes, too short for {kind}.", 0),
    };
}
