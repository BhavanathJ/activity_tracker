using System;
using System.IO;
using System.Text.Json;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.Core.Configuration;

public static class ConfigManager
{
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ActivityTracker",
        "config.json");

    public static TrackerConfig Load()
    {
        if (!File.Exists(ConfigFilePath))
        {
            var defaultConfig = new TrackerConfig();
            var directory = Path.GetDirectoryName(ConfigFilePath);
            if (!Directory.Exists(directory) && directory != null)
            {
                Directory.CreateDirectory(directory);
            }
            
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
            return defaultConfig;
        }

        var existingJson = File.ReadAllText(ConfigFilePath);
        return JsonSerializer.Deserialize<TrackerConfig>(existingJson) ?? new TrackerConfig();
    }
}
