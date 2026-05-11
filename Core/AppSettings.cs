using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickyWindows.Core;

/// <summary>
/// Persisted user settings. Serialised to %APPDATA%\Clicky\settings.json.
/// Mirrors macOS UserDefaults keys used by CompanionManager.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("workerBaseUrl")]
    public string WorkerBaseUrl { get; set; } = AppConstants.WorkerBaseUrl;

    [JsonPropertyName("selectedClaudeModel")]
    public string SelectedClaudeModel { get; set; } = AppConstants.DefaultClaudeModel;

    [JsonPropertyName("isClickyCursorEnabled")]
    public bool IsClickyCursorEnabled { get; set; } = true;

    [JsonPropertyName("hasCompletedOnboarding")]
    public bool HasCompletedOnboarding { get; set; } = false;

    [JsonPropertyName("hasSubmittedEmail")]
    public bool HasSubmittedEmail { get; set; } = false;

    /// <summary>
    /// Facts the user has asked Clicky to remember (e.g. "my name is Alex").
    /// Injected into the system prompt so Claude always has personal context.
    /// Capped at <see cref="AppConstants.MemoryMaxFacts"/> entries; oldest is dropped when full.
    /// </summary>
    [JsonPropertyName("userFacts")]
    public List<string> UserFacts { get; set; } = [];

    // ── Persistence ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (IoFile.Exists(AppConstants.SettingsFilePath))
            {
                var json = IoFile.ReadAllText(AppConstants.SettingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                       ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to load: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            IoDirectory.CreateDirectory(AppConstants.SettingsDirectory);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            IoFile.WriteAllText(AppConstants.SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to save: {ex.Message}");
        }
    }
}

