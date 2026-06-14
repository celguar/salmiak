using System.IO;
using System.Text.Json;

namespace Salmiak.Properties;

internal sealed class Settings
{
    private static readonly string FilePath =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Salmiak", "settings.json");

    public static Settings Default { get; } = Load();

    public string WoWDataPath { get; set; } = string.Empty;

    public System.Collections.Generic.List<string> FavoriteDoodads { get; set; } = new();
    public System.Collections.Generic.List<string> FavoriteWmos { get; set; } = new();
    public System.Collections.Generic.List<string> FavoriteTextures { get; set; } = new();

    public string DbHost { get; set; } = "127.0.0.1";
    public int DbPort { get; set; } = 3306;
    public string DbUser { get; set; } = "mangos";
    public string DbPassword { get; set; } = "mangos";
    public string DbWorldName { get; set; } = "classicmangos";
    public string DbCharactersName { get; set; } = "classiccharacters";

    public string DeployClientPatch { get; set; } = string.Empty;
    public string DeployServerDir { get; set; } = string.Empty;
    public bool DeployRestartMangosd { get; set; } = false;
    public bool DeployApplySql { get; set; } = false;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    private static readonly string LegacyFilePath =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "WoWMapEditor", "settings.json");

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            if (File.Exists(LegacyFilePath))
            {
                var migrated = JsonSerializer.Deserialize<Settings>(File.ReadAllText(LegacyFilePath)) ?? new Settings();
                migrated.Save();
                return migrated;
            }
        }
        catch { }
        return new Settings();
    }
}
