using System.Text.Json;

namespace UnityBrowser;

/// <summary>User preferences, stored as JSON in %AppData%\UnityBrowser\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityBrowser", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Default folder that exports are written to. Empty means not set.</summary>
    public string ExportFolder { get; set; } = "";

    // "Export All Known Formats" options.

    /// <summary>"Dae" or "Obj".</summary>
    public string MeshFormat { get; set; } = "Dae";

    /// <summary>"Png" or "Dds".</summary>
    public string ImageFormat { get; set; } = "Png";

    public bool ExportAnimations { get; set; } = true;

    public bool ExportSound { get; set; } = false;

    /// <summary>Last folder searched by File > Find Dependencies.</summary>
    public string DependencyFolder { get; set; } = "";

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Failing to save preferences should never stop the app from closing.
        }
    }
}
