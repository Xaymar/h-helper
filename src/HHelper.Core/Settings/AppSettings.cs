using System.Text.Json;

namespace HHelper.Core.Settings;

/// <summary>User settings. Experimental controls are off by default (spec 2, rule 8).</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Board ID the user typed to turn experimental controls on. Writes are only allowed when this
    /// equals the running machine's board ID, so the switch "names the model" and does not carry
    /// over to another laptop with a copied settings file.
    /// </summary>
    public string? ExperimentalControlsForBoard { get; set; }

    /// <summary>Keep a custom fan curve on exit instead of restoring the factory table (spec 3.6).</summary>
    public bool KeepFanCurveOnExit { get; set; }

    /// <summary>Optional override of the WMI instance name base (the PNP0C14 device instance path).</summary>
    public string? WmiInstanceBaseOverride { get; set; }

    public bool ExperimentalEnabledFor(string? boardId)
        => !string.IsNullOrWhiteSpace(boardId)
           && string.Equals(ExperimentalControlsForBoard?.Trim(), boardId.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; } = path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H-Helper", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file falls back to safe defaults (experimental off).
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, overwrite: true);
    }
}
