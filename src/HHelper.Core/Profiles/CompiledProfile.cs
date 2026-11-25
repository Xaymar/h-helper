using HHelper.Core.Broker;

namespace HHelper.Core.Profiles;

/// <summary>One allowlisted WMI instance.</summary>
public sealed record AllowlistEntry(
    string Feature,
    string BlockName,
    Guid BlockGuid,
    int Instance,
    WmiValueKind Kind,
    bool CanWriteExperimental,
    string? Spec,
    string? Note);

/// <summary>A validated profile, with the allowlist expanded into a lookup table.</summary>
public sealed class CompiledProfile
{
    private readonly Dictionary<(Guid, int), AllowlistEntry> _byInstance = new();
    private readonly Dictionary<string, List<AllowlistEntry>> _byFeature = new(StringComparer.Ordinal);

    private CompiledProfile(ModelProfile source) => Source = source;

    public ModelProfile Source { get; }
    public string Id => Source.Id;
    public string DisplayName => Source.DisplayName;
    public IReadOnlyCollection<AllowlistEntry> Entries => _byInstance.Values;
    public Guid? EventGuid { get; private set; }
    public IReadOnlyDictionary<byte, string> EventGroupByCode { get; private set; } = new Dictionary<byte, string>();
    public AllowlistEntry? InstanceProbe { get; private set; }

    public AllowlistEntry? Find(Guid guid, int instance)
        => _byInstance.GetValueOrDefault((guid, instance));

    /// <summary>All entries of a feature in instance order (one entry, or a range).</summary>
    public IReadOnlyList<AllowlistEntry> Feature(string feature)
        => _byFeature.TryGetValue(feature, out var list) ? list : [];

    public bool HasFeature(string feature) => _byFeature.ContainsKey(feature);

    public static CompiledProfile From(ModelProfile p)
    {
        if (p.Schema != 1) throw new ProfileException($"Profile '{p.Id}': unsupported schema {p.Schema}.");
        if (string.IsNullOrWhiteSpace(p.Id)) throw new ProfileException("Profile has no id.");
        if (p.Match.BaseBoardProduct.Count == 0) throw new ProfileException($"Profile '{p.Id}': no match rule; a profile must name the boards it was specified for.");

        var blocks = new Dictionary<string, (Guid Guid, int Count, WmiValueKind Kind)>(StringComparer.Ordinal);
        foreach (var (name, def) in p.Blocks)
        {
            if (!Guid.TryParse(def.Guid, out var guid)) throw new ProfileException($"Profile '{p.Id}': block {name} has an invalid GUID.");
            if (SafetyRules.IsForbiddenGuid(guid)) throw new ProfileException($"Profile '{p.Id}': block {name} uses a GUID H-Helper must never open.");
            var kind = def.ValueType.ToLowerInvariant() switch
            {
                "u8" => WmiValueKind.U8,
                "s16" => WmiValueKind.S16,
                _ => throw new ProfileException($"Profile '{p.Id}': block {name} has unknown value type '{def.ValueType}'."),
            };
            if (def.Instances <= 0) throw new ProfileException($"Profile '{p.Id}': block {name} has no instances.");
            blocks[name] = (guid, def.Instances, kind);
        }

        var compiled = new CompiledProfile(p);
        foreach (var a in p.Allowlist)
        {
            if (!blocks.TryGetValue(a.Block, out var block))
                throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} names unknown block {a.Block}.");

            int first, last;
            if (a.Instance is int single && a.Range is null) { first = last = single; }
            else if (a.Instance is null && a.Range is { Length: 2 } r) { first = r[0]; last = r[1]; }
            else throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} needs exactly one of 'instance' or a two-element 'range'.");

            if (first < 0 || last < first || last >= block.Count)
                throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} instance range {first}..{last} is outside block {a.Block} (0..{block.Count - 1}).");

            var list = new List<AllowlistEntry>();
            for (var n = first; n <= last; n++)
            {
                if (SafetyRules.IsForbiddenInstance(block.Guid, n))
                    throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} includes {a.Block} instance {n}, which must never be accessed.");
                if (a.WriteExperimental && !SafetyRules.IsWritableCeiling(block.Guid, n))
                    throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} marks {a.Block} instance {n} writable, which is outside the write ceiling.");
                var entry = new AllowlistEntry(a.Feature, a.Block, block.Guid, n, block.Kind, a.WriteExperimental, a.Spec, a.Note);
                if (!compiled._byInstance.TryAdd((block.Guid, n), entry))
                    throw new ProfileException($"Profile '{p.Id}': {a.Block} instance {n} is listed twice.");
                list.Add(entry);
            }
            if (!compiled._byFeature.TryAdd(a.Feature, list))
                throw new ProfileException($"Profile '{p.Id}': feature {a.Feature} is listed twice.");
        }

        if (p.InstanceProbe is { } probe)
        {
            if (!blocks.TryGetValue(probe.Block, out var pb)) throw new ProfileException($"Profile '{p.Id}': instance probe names unknown block.");
            compiled.InstanceProbe = compiled.Find(pb.Guid, probe.Instance)
                ?? throw new ProfileException($"Profile '{p.Id}': instance probe must itself be allowlisted.");
        }

        if (p.Events is { } ev)
        {
            if (!Guid.TryParse(ev.Guid, out var evGuid)) throw new ProfileException($"Profile '{p.Id}': event GUID is invalid.");
            compiled.EventGuid = evGuid;
            var map = new Dictionary<byte, string>();
            foreach (var (group, def) in ev.Groups)
            {
                foreach (var feature in def.Refresh)
                    if (!compiled.HasFeature(feature)) throw new ProfileException($"Profile '{p.Id}': event group {group} refreshes unknown feature {feature}.");
                foreach (var code in def.Codes)
                    if (!map.TryAdd(ProfileLoader.ParseByte(code), group)) throw new ProfileException($"Profile '{p.Id}': event code {code} is in two groups.");
            }
            compiled.EventGroupByCode = map;
        }

        if (p.FanCurves is { } fc)
        {
            foreach (var (fan, def) in fc.Fans)
            {
                var t = compiled.Feature(def.Thresholds);
                var s = compiled.Feature(def.Speeds);
                if (t.Count == 0 || s.Count != t.Count + 1)
                    throw new ProfileException($"Profile '{p.Id}': fan {fan} needs N thresholds and N+1 speed points.");
                if (t.Concat(s).Any(e => !e.CanWriteExperimental || e.Kind != WmiValueKind.U8))
                    throw new ProfileException($"Profile '{p.Id}': fan {fan} curve entries must be experimental-writable u8 instances.");
            }
        }

        return compiled;
    }
}
