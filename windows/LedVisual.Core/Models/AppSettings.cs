using System.Text.Json;
using System.Text.Json.Serialization;

namespace LedVisual.Core.Models;

[JsonConverter(typeof(AIModelJsonConverter))]
public enum AIModel
{
    GeminiDecision1,
    Gemini35FlashLite,
    Gemini35Flash,
    Gemma4_26B_A4B,
    Gemma4_31B
}

public static class AIModelExtensions
{
    public static readonly IReadOnlyList<AIModel> AllModels =
    [
        AIModel.GeminiDecision1,
        AIModel.Gemini35FlashLite,
        AIModel.Gemini35Flash,
        AIModel.Gemma4_26B_A4B,
        AIModel.Gemma4_31B
    ];

    public static string GetApiId(this AIModel model) => model switch
    {
        AIModel.GeminiDecision1 => "gemini-decision-1",
        AIModel.Gemini35FlashLite => "gemini-3.5-flash-lite",
        AIModel.Gemini35Flash => "gemini-3.5-flash",
        AIModel.Gemma4_26B_A4B => "gemma-4-26b-a4b-it",
        AIModel.Gemma4_31B => "gemma-4-31b-it",
        _ => "gemini-decision-1"
    };

    public static string GetDisplayName(this AIModel model) => model switch
    {
        AIModel.GeminiDecision1 => "Gemini Decision 1 (Ultra-Fast)",
        AIModel.Gemini35FlashLite => "Gemini 3.5 Flash Lite (Reasoning)",
        AIModel.Gemini35Flash => "Gemini 3.5 Flash",
        AIModel.Gemma4_26B_A4B => "Gemma 4 26B A4B",
        AIModel.Gemma4_31B => "Gemma 4 31B",
        _ => model.ToString()
    };

    public static bool UsesDecisionsApi(this AIModel model) => model == AIModel.GeminiDecision1;

    public static AIModel GetFallbackModel(this AIModel model) => model switch
    {
        AIModel.GeminiDecision1 => AIModel.Gemini35FlashLite,
        AIModel.Gemini35FlashLite => AIModel.Gemini35Flash,
        AIModel.Gemini35Flash => AIModel.Gemini35FlashLite,
        AIModel.Gemma4_26B_A4B => AIModel.Gemini35FlashLite,
        AIModel.Gemma4_31B => AIModel.Gemini35FlashLite,
        _ => AIModel.Gemini35FlashLite
    };

    public static bool TryParseApiId(string? value, out AIModel model)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (var candidate in AllModels)
            {
                if (string.Equals(candidate.GetApiId(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    model = candidate;
                    return true;
                }
            }

            if (Enum.TryParse(value.Trim(), ignoreCase: true, out model))
            {
                return true;
            }
        }

        model = AIModel.GeminiDecision1;
        return false;
    }
}

public sealed class AIModelJsonConverter : JsonConverter<AIModel>
{
    public override AIModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (AIModelExtensions.TryParseApiId(raw, out var parsed))
            {
                return parsed;
            }
        }

        return AIModel.GeminiDecision1;
    }

    public override void Write(Utf8JsonWriter writer, AIModel value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.GetApiId());
    }
}

public sealed record BlinkTimings
{
    public int ShortOnMs { get; init; }
    public int ShortOffMs { get; init; }
    public int InterAnswerPauseMs { get; init; }
    public int ErrorLongOnMs { get; init; }
    public int PreBlinkDarkMs { get; init; }

    [JsonConstructor]
    public BlinkTimings(
        int shortOnMs = 160,
        int shortOffMs = 180,
        int interAnswerPauseMs = 850,
        int errorLongOnMs = 1400,
        int preBlinkDarkMs = 250)
    {
        ShortOnMs = Math.Max(50, shortOnMs);
        ShortOffMs = Math.Max(50, shortOffMs);
        InterAnswerPauseMs = Math.Max(250, interAnswerPauseMs);
        ErrorLongOnMs = Math.Max(600, errorLongOnMs);
        PreBlinkDarkMs = Math.Max(100, preBlinkDarkMs);
    }

    public static BlinkTimings Default => new();
}

public sealed record AppSettings
{
    public Hotkey Hotkey { get; init; } = Hotkey.Default;
    public AIModel Model { get; init; } = AIModel.GeminiDecision1;
    public bool AutoFallbackOnServerError { get; init; } = true;
    public int HoldThresholdMs { get; init; } = 350;
    public int RecordingSampleIntervalMs { get; init; } = 550;
    public int MaxRecordingFrames { get; init; } = 24;
    public int MaxRecordingSeconds { get; init; } = 30;
    public BlinkTimings BlinkTimings { get; init; } = BlinkTimings.Default;
    public bool SwallowHotkeyEvents { get; init; } = true;

    public static AppSettings Default => new();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? GetDefaultFilePath();
    }

    public static string GetDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LedVisual", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(_filePath);
            var decoded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (decoded is null)
            {
                return AppSettings.Default;
            }

            // Guard against invalid/CapsLock hotkeys or null nested records from hand-edited JSON.
            var hotkey = decoded.Hotkey is null || Hotkey.IsCapsLock(decoded.Hotkey.VirtualKeyCode)
                ? Hotkey.Default
                : decoded.Hotkey;
            var timings = decoded.BlinkTimings ?? BlinkTimings.Default;

            return decoded with
            {
                Hotkey = hotkey,
                BlinkTimings = timings,
                HoldThresholdMs = Math.Clamp(decoded.HoldThresholdMs, 150, 2000),
                RecordingSampleIntervalMs = Math.Clamp(decoded.RecordingSampleIntervalMs, 200, 3000),
                MaxRecordingFrames = Math.Clamp(decoded.MaxRecordingFrames, 1, 60),
                MaxRecordingSeconds = Math.Clamp(decoded.MaxRecordingSeconds, 5, 120)
            };
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(_filePath, json);
    }
}
