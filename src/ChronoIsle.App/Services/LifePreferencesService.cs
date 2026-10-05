using System.IO;
using System.Text.Json;

namespace ChronoIsle.App.Services;

public sealed class LifePreferencesService
{
    readonly string path;

    public event Action? Changed;

    public LifePreferencesService(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChronoIsle");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "life-preferences.json");
    }

    public LifePreferences Load()
    {
        try { return JsonSerializer.Deserialize<LifePreferences>(File.ReadAllText(path)) ?? LifePreferences.Default; }
        catch { return LifePreferences.Default; }
    }

    public void Save(LifePreferences preferences)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
        File.Move(temporary, path, true);
        Changed?.Invoke();
    }
}
