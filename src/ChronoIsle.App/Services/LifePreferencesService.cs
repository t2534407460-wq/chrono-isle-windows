using System.IO;
using System.Text.Json;

namespace ChronoIsle.App.Services;

public sealed class LifePreferencesService
{
    readonly string path;

    public LifePreferencesService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChronoIsle");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "life-preferences.json");
    }

    public LifePreferences Load()
    {
        try { return JsonSerializer.Deserialize<LifePreferences>(File.ReadAllText(path)) ?? LifePreferences.Default; }
        catch { return LifePreferences.Default; }
    }

    public void Save(LifePreferences preferences) => File.WriteAllText(path, JsonSerializer.Serialize(preferences));
}
