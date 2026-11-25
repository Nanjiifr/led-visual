import Foundation

public enum AIModel: String, CaseIterable, Codable, Equatable, Hashable, Identifiable, Sendable {
    case geminiDecision1   = "gemini-decision-1"
    case gemini35FlashLite = "gemini-3.5-flash-lite"
    case gemini35Flash     = "gemini-3.5-flash"
    case gemma4_26b_a4b    = "gemma-4-26b-a4b-it"
    case gemma4_31b        = "gemma-4-31b-it"

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .geminiDecision1:   return "Gemini Decision 1 (Ultra-Fast)"
        case .gemini35FlashLite: return "Gemini 3.5 Flash Lite (Reasoning)"
        case .gemini35Flash:     return "Gemini 3.5 Flash"
        case .gemma4_26b_a4b:    return "Gemma 4 26B A4B"
        case .gemma4_31b:        return "Gemma 4 31B"
        }
    }

    public var usesDecisionsAPI: Bool {
        self == .geminiDecision1
    }

    public var fallbackModel: AIModel {
        switch self {
        case .geminiDecision1:   return .gemini35FlashLite
        case .gemini35FlashLite: return .gemini35Flash
        case .gemini35Flash:     return .gemini35FlashLite
        case .gemma4_26b_a4b:    return .gemini35FlashLite
        case .gemma4_31b:        return .gemini35FlashLite
        }
    }
}

public typealias GemmaModel = AIModel

public struct BlinkTimings: Codable, Equatable, Sendable {
    public var shortOnMs: Int
    public var shortOffMs: Int
    public var interAnswerPauseMs: Int
    public var errorLongOnMs: Int
    public var preBlinkDarkMs: Int

    public init(
        shortOnMs: Int = 160,
        shortOffMs: Int = 180,
        interAnswerPauseMs: Int = 850,
        errorLongOnMs: Int = 1400,
        preBlinkDarkMs: Int = 250
    ) {
        self.shortOnMs = max(50, shortOnMs)
        self.shortOffMs = max(50, shortOffMs)
        self.interAnswerPauseMs = max(250, interAnswerPauseMs)
        self.errorLongOnMs = max(600, errorLongOnMs)
        self.preBlinkDarkMs = max(100, preBlinkDarkMs)
    }

    public static let `default` = BlinkTimings()
}

public struct AppSettings: Codable, Equatable, Sendable {
    public var hotkey: Hotkey
    public var model: AIModel
    public var autoFallbackOnServerError: Bool
    public var holdThresholdMs: Int
    public var recordingSampleIntervalMs: Int
    public var maxRecordingFrames: Int
    public var maxRecordingSeconds: Int
    public var blinkTimings: BlinkTimings
    public var swallowHotkeyEvents: Bool

    public init(
        hotkey: Hotkey = .defaultHotkey,
        model: AIModel = .geminiDecision1,
        autoFallbackOnServerError: Bool = true,
        holdThresholdMs: Int = 350,
        recordingSampleIntervalMs: Int = 550,
        maxRecordingFrames: Int = 24,
        maxRecordingSeconds: Int = 30,
        blinkTimings: BlinkTimings = .default,
        swallowHotkeyEvents: Bool = true
    ) {
        self.hotkey = hotkey
        self.model = model
        self.autoFallbackOnServerError = autoFallbackOnServerError
        self.holdThresholdMs = holdThresholdMs
        self.recordingSampleIntervalMs = recordingSampleIntervalMs
        self.maxRecordingFrames = maxRecordingFrames
        self.maxRecordingSeconds = maxRecordingSeconds
        self.blinkTimings = blinkTimings
        self.swallowHotkeyEvents = swallowHotkeyEvents
    }

    public static let `default` = AppSettings()
}

public final class SettingsStore: @unchecked Sendable {
    private let defaults: UserDefaults
    private let storageKey: String
    private let legacyKeys = [
        "com.led-visual.app.settings.v2",
        "com.led-visual.app.settings.v1"
    ]

    public init(
        defaults: UserDefaults = .standard,
        storageKey: String = "com.led-visual.app.settings.v3"
    ) {
        self.defaults = defaults
        self.storageKey = storageKey
    }

    public func load() -> AppSettings {
        if let data = defaults.data(forKey: storageKey),
           let decoded = try? JSONDecoder().decode(AppSettings.self, from: data) {
            return decoded
        }
        // Migrate earlier settings (preserving hotkey & blink timings, switching default to Gemini Decision 1)
        for key in legacyKeys {
            if let legacyData = defaults.data(forKey: key),
               var migrated = try? JSONDecoder().decode(AppSettings.self, from: legacyData) {
                migrated.model = .geminiDecision1
                save(migrated)
                return migrated
            }
        }
        return .default
    }

    public func save(_ settings: AppSettings) {
        guard let data = try? JSONEncoder().encode(settings) else { return }
        defaults.set(data, forKey: storageKey)
    }
}
