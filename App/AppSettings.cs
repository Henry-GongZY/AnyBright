using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenBrightnessApp;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal static class AppSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenBrightness", "settings.json");

    public static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), SettingsJsonContext.Default.DictionaryStringString) ?? new();
        }
        catch { /* corrupted file -> defaults */ }
        return new();
    }

    public static void Save(Dictionary<string, string> settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.DictionaryStringString));
        }
        catch { /* best effort */ }
    }
}
