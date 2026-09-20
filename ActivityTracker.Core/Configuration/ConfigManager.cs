using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.Core.Configuration;

public static class ConfigManager
{
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ActivityTracker",
        "config.json");

    public static TrackerConfig Load()
    {
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                var existingJson = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize(existingJson, TrackerConfigJsonContext.Default.TrackerConfig);
                if (config != null)
                {
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[WARN] Failed to deserialize {ConfigFilePath}: {ex.Message}. " +
                    "Renaming to config.json.broken and regenerating defaults.");

                try
                {
                    var brokenPath = ConfigFilePath + ".broken";
                    File.Move(ConfigFilePath, brokenPath, overwrite: true);
                }
                catch
                {
                    // If rename fails, just proceed to overwrite with defaults
                }
            }
        }

        // Either file doesn't exist, deserialization failed, or returned null — generate defaults
        var defaultConfig = new TrackerConfig();
        var directory = Path.GetDirectoryName(ConfigFilePath);
        if (!Directory.Exists(directory) && directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(defaultConfig, TrackerConfigJsonContext.Default.TrackerConfig);
        File.WriteAllText(ConfigFilePath, json);
        return defaultConfig;
    }
}
