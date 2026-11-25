using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HHelper.Core.Profiles;

/// <summary>Value encoding of one WMI data block instance.</summary>
public enum WmiValueKind
{
    /// <summary>Unsigned 8-bit.</summary>
    U8,

    /// <summary>Signed 16-bit, little endian in the WMI buffer.</summary>
    S16,
}

/// <summary>
/// A model profile as stored in <c>profiles/*.json</c>. Every model difference lives here,
/// never in code. The loader turns it into a <see cref="CompiledProfile"/> and checks it
/// against the hard safety rules in <see cref="Broker.SafetyRules"/>.
/// </summary>
public sealed class ModelProfile
{
    public int Schema { get; set; }
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Spec { get; set; }
    public ProfileMatch Match { get; set; } = new();
    public Dictionary<string, BlockDefinition> Blocks { get; set; } = new();
    public InstanceRef? InstanceProbe { get; set; }
    public List<AllowlistDefinition> Allowlist { get; set; } = new();
    public EventDefinition? Events { get; set; }
    public FanCurveDefinition? FanCurves { get; set; }
    public GraphicsDefinition? Graphics { get; set; }
}

public sealed class ProfileMatch
{
    public List<string> BaseBoardProduct { get; set; } = new();
}

public sealed class BlockDefinition
{
    public string Guid { get; set; } = "";
    public int Instances { get; set; }
    public string ValueType { get; set; } = "u8";
}

public sealed class InstanceRef
{
    public string Block { get; set; } = "";
    public int Instance { get; set; }
}

public sealed class AllowlistDefinition
{
    public string Feature { get; set; } = "";
    public string Block { get; set; } = "";
    public int? Instance { get; set; }
    public int[]? Range { get; set; }
    public string? Spec { get; set; }
    public string? Note { get; set; }
    public bool WriteExperimental { get; set; }
}

public sealed class EventDefinition
{
    public string Guid { get; set; } = "";
    public Dictionary<string, EventGroupDefinition> Groups { get; set; } = new();
}

public sealed class EventGroupDefinition
{
    public List<string> Codes { get; set; } = new();
    public List<string> Refresh { get; set; } = new();
}

public sealed class FanCurveDefinition
{
    public bool WriteEnabled { get; set; }
    public string? WriteBlockedReason { get; set; }
    public int ThresholdMarginC { get; set; } = 5;
    public Dictionary<string, FanDefinition> Fans { get; set; } = new();
}

public sealed class FanDefinition
{
    public string Label { get; set; } = "";
    public string Thresholds { get; set; } = "";
    public string Speeds { get; set; } = "";
}

public sealed class GraphicsDefinition
{
    public string UefiVariable { get; set; } = "";
    public string UefiGuid { get; set; } = "";
    public int ModeOffset { get; set; }
    public Dictionary<string, string> ModeValues { get; set; } = new();
    public int PrimaryOffset { get; set; }
    public Dictionary<string, string> PrimaryValues { get; set; } = new();
}

/// <summary>Thrown when a profile is malformed or breaks a safety rule.</summary>
public sealed class ProfileException(string message) : Exception(message);

public static class ProfileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static ModelProfile Parse(string json)
        => JsonSerializer.Deserialize<ModelProfile>(json, Options)
           ?? throw new ProfileException("Profile is empty.");

    public static CompiledProfile Compile(string json) => CompiledProfile.From(Parse(json));

    /// <summary>All profiles shipped inside the assembly.</summary>
    public static IReadOnlyList<CompiledProfile> LoadBuiltIn()
    {
        var asm = typeof(ProfileLoader).Assembly;
        var list = new List<CompiledProfile>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(Compile(reader.ReadToEnd()));
        }
        return list;
    }

    /// <summary>Finds the profile whose match rules fit this machine, or null (read nothing, write nothing).</summary>
    public static CompiledProfile? FindMatch(IEnumerable<CompiledProfile> profiles, string? baseBoardProduct)
    {
        if (string.IsNullOrWhiteSpace(baseBoardProduct)) return null;
        var board = baseBoardProduct.Trim();
        return profiles.FirstOrDefault(p => p.Source.Match.BaseBoardProduct.Any(b => string.Equals(b, board, StringComparison.OrdinalIgnoreCase)));
    }

    internal static byte ParseByte(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.Parse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return byte.Parse(t, CultureInfo.InvariantCulture);
    }
}
