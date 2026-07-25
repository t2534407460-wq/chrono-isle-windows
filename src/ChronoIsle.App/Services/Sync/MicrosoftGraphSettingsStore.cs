using System.Text.Json;

namespace ChronoIsle.App.Services.Sync;

public sealed record MicrosoftGraphLocalSettings(string ClientId, string TenantId, string? AccountId = null, string? Username = null);

/// <summary>Stores public app-registration metadata only. Tokens remain in MSAL/WAM and are never copied into backups.</summary>
public sealed class MicrosoftGraphSettingsStore
{
    readonly string path;

    public MicrosoftGraphSettingsStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChronoIsle", "microsoft-graph-settings.json");

    public MicrosoftGraphLocalSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MicrosoftGraphLocalSettings>(File.ReadAllText(path)) ?? new("", "common")
                : new("", "common");
        }
        catch (JsonException) { return new("", "common"); }
    }

    public void Save(MicrosoftGraphLocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Graph settings path has no directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
    }
}
