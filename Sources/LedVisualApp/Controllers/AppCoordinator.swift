import AppKit
import Foundation
import LedVisualCore
import Observation

public enum AppStatus: Equatable {
    case idle
    case recording(frameCount: Int)
    case thinking
    case blinking

    public var label: String {
        switch self {
        case .idle:
            return "Ready"
        case .recording(let count):
            return count > 0 ? "Recording (\(count) frames)…" : "Recording…"
        case .thinking:
            return "Analyzing…"
        case .blinking:
            return "Blinking LED…"
        }
    }
}

@MainActor
@Observable
public final class AppCoordinator {
    public var settings: AppSettings {
        didSet {
            settingsStore.save(settings)
            hotkeyMonitor.updateConfiguration(
                hotkey: settings.hotkey,
                holdThresholdMs: settings.holdThresholdMs,
                swallowEvents: settings.swallowHotkeyEvents
            )
        }
    }

    public var status: AppStatus = .idle
    public var lastSummary: String = "No captures yet"
    public var lastError: String?
    public var hasAccessibilityPermission = false
    public var hasScreenRecordingPermission = false
    public var hasAPIKey = false
    public var isHotkeyMonitorActive = false

    public var isEditingHotkey = false {
        didSet {
            hotkeyMonitor.isSuspended = isEditingHotkey
        }
    }

    private let settingsStore: SettingsStore
    private let keychain: any APIKeyStoring
    private let geminiClient: GeminiClient
    private let blinkEngine: BlinkEngine
    private let screenCapture: ScreenCaptureService
    private let hotkeyMonitor: GlobalHotkeyMonitor

    private var activeWorkflowTask: Task<Void, Never>?
    private var permissionPollTimer: Timer?

    public init(
        settingsStore: SettingsStore = SettingsStore(),
        keychain: any APIKeyStoring = KeychainService(),
        geminiClient: GeminiClient = GeminiClient(),
        blinkEngine: BlinkEngine = BlinkEngine(),
        screenCapture: ScreenCaptureService = ScreenCaptureService()
    ) {
        self.settingsStore = settingsStore
        self.keychain = keychain
        self.geminiClient = geminiClient
        self.blinkEngine = blinkEngine
        self.screenCapture = screenCapture

        let loadedSettings = settingsStore.load()
        self.settings = loadedSettings
        self.hotkeyMonitor = GlobalHotkeyMonitor(
            hotkey: loadedSettings.hotkey,
            holdThresholdMs: loadedSettings.holdThresholdMs,
            swallowEvents: loadedSettings.swallowHotkeyEvents
        )

        bindHotkeyCallbacks()
        refreshStatusFlags()
        startHotkeyMonitorIfPermitted()
        startPermissionWatcher()
    }

    public func shutdown() {
        permissionPollTimer?.invalidate()
        permissionPollTimer = nil
        activeWorkflowTask?.cancel()
        hotkeyMonitor.stop()
        // Synchronously cancel any active LED blink and restore original Caps Lock state
        let engine = blinkEngine
        Task.detached {
            await engine.cancel()
        }
    }

    // MARK: - Permissions & API Key

    public func refreshStatusFlags() {
        hasAccessibilityPermission = GlobalHotkeyMonitor.isAccessibilityTrusted(prompt: false)
        hasScreenRecordingPermission = ScreenCaptureService.hasScreenRecordingPermission()
        hasAPIKey = (try? keychain.loadAPIKey()) != nil

        if hasAccessibilityPermission && !hotkeyMonitor.isRunning {
            isHotkeyMonitorActive = hotkeyMonitor.start()
        } else {
            isHotkeyMonitorActive = hotkeyMonitor.isRunning
        }
    }

    public func requestAccessibilityPermission() {
        _ = GlobalHotkeyMonitor.isAccessibilityTrusted(prompt: true)
        openSystemSettings(pane: "Privacy_Accessibility")
        refreshStatusFlags()
    }

    public func requestScreenRecordingPermission() {
        ScreenCaptureService.requestScreenRecordingPermission()
        openSystemSettings(pane: "Privacy_ScreenCapture")
        refreshStatusFlags()
    }

    public func loadAPIKey() -> String {
        (try? keychain.loadAPIKey()) ?? ""
    }

    public func saveAPIKey(_ key: String) throws {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            try keychain.deleteAPIKey()
            hasAPIKey = false
        } else {
            try keychain.saveAPIKey(trimmed)
            hasAPIKey = true
        }
    }

    public func verifyAPIKey(_ candidateKey: String? = nil) async -> Result<[String], Error> {
        let keyToTest = (candidateKey ?? loadAPIKey()).trimmingCharacters(in: .whitespacesAndNewlines)
        do {
            let models = try await geminiClient.listAvailableGemmaModels(apiKey: keyToTest)
            return .success(models)
        } catch {
            return .failure(error)
        }
    }

    // MARK: - Manual & Test Actions (from Popover / Settings)

    public func triggerManualScreenshot() {
        handleTapCapture()
    }

    public func testBlink(answers: [Int]) {
        cancelActiveWork()
        let timings = settings.blinkTimings
        activeWorkflowTask = Task { [weak self] in
            guard let self else { return }
            self.status = .blinking
            await self.blinkEngine.blinkAnswers(answers, timings: timings)
            if !Task.isCancelled {
                self.status = .idle
            }
        }
    }

    public func testErrorBlink() {
        cancelActiveWork()
        let timings = settings.blinkTimings
        activeWorkflowTask = Task { [weak self] in
            guard let self else { return }
            self.status = .blinking
            await self.blinkEngine.blinkError(timings: timings)
            if !Task.isCancelled {
                self.status = .idle
            }
        }
    }

    // MARK: - Hotkey Capture Workflow (Completely Discreet)

    private func bindHotkeyCallbacks() {
        hotkeyMonitor.onTap = { [weak self] in
            self?.handleTapCapture()
        }
        hotkeyMonitor.onHoldStarted = { [weak self] in
            self?.handleHoldStarted()
        }
        hotkeyMonitor.onHoldEnded = { [weak self] in
            self?.handleHoldEnded()
        }
    }

    private func startHotkeyMonitorIfPermitted() {
        if hasAccessibilityPermission {
            isHotkeyMonitorActive = hotkeyMonitor.start()
        } else {
            isHotkeyMonitorActive = false
        }
    }

    private func startPermissionWatcher() {
        permissionPollTimer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.refreshStatusFlags()
            }
        }
    }

    private func cancelActiveWork() {
        activeWorkflowTask?.cancel()
        activeWorkflowTask = nil
        let engine = blinkEngine
        let capture = screenCapture
        Task {
            await engine.cancel()
            await capture.cancelRecording()
        }
    }

    private func handleTapCapture() {
        cancelActiveWork()
        let currentSettings = settings
        activeWorkflowTask = Task { [weak self] in
            guard let self else { return }
            self.status = .thinking
            do {
                let frames = try await self.screenCapture.captureScreenshot()
                try Task.checkCancellation()
                await self.resolveAndBlink(frames: frames, captureLabel: "Screenshot", settings: currentSettings)
            } catch is CancellationError {
                return
            } catch {
                await self.handleFailure(error.localizedDescription, timings: currentSettings.blinkTimings)
            }
        }
    }

    private func handleHoldStarted() {
        cancelActiveWork()
        let currentSettings = settings
        status = .recording(frameCount: 0)

        activeWorkflowTask = Task { [weak self] in
            guard let self else { return }
            do {
                try await self.screenCapture.startRecording(
                    sampleIntervalMs: currentSettings.recordingSampleIntervalMs,
                    maxFrames: currentSettings.maxRecordingFrames,
                    maxDurationSeconds: currentSettings.maxRecordingSeconds,
                    onFrameCountChanged: { [weak self] count in
                        Task { @MainActor [weak self] in
                            if case .recording = self?.status {
                                self?.status = .recording(frameCount: count)
                            }
                        }
                    }
                )
            } catch is CancellationError {
                return
            } catch {
                await self.handleFailure(error.localizedDescription, timings: currentSettings.blinkTimings)
            }
        }
    }

    private func handleHoldEnded() {
        let currentSettings = settings
        let startTask = activeWorkflowTask

        activeWorkflowTask = Task { [weak self] in
            guard let self else { return }
            // Ensure recording finished starting before we stop it
            await startTask?.value
            guard !Task.isCancelled else { return }

            self.status = .thinking
            do {
                let frames = try await self.screenCapture.stopRecording()
                try Task.checkCancellation()
                let label = "Recording (\(frames.count) frame\(frames.count == 1 ? "" : "s"))"
                await self.resolveAndBlink(frames: frames, captureLabel: label, settings: currentSettings)
            } catch is CancellationError {
                return
            } catch {
                await self.handleFailure(error.localizedDescription, timings: currentSettings.blinkTimings)
            }
        }
    }

    private func resolveAndBlink(
        frames: [Data],
        captureLabel: String,
        settings: AppSettings
    ) async {
        do {
            guard let apiKey = try keychain.loadAPIKey(), !apiKey.isEmpty else {
                throw GeminiClientError.missingAPIKey
            }

            let resolution = try await geminiClient.solveMCQ(
                jpegFrames: frames,
                apiKey: apiKey,
                model: settings.model,
                autoFallback: settings.autoFallbackOnServerError
            )
            try Task.checkCancellation()

            let answerText = resolution.answers.map(String.init).joined(separator: ", ")
            lastError = nil
            lastSummary = "\(captureLabel) -> Answer: \(answerText) (\(resolution.modelUsed.rawValue))"

            status = .blinking
            await blinkEngine.blinkAnswers(resolution.answers, timings: settings.blinkTimings)
            if !Task.isCancelled {
                status = .idle
            }
        } catch is CancellationError {
            return
        } catch {
            await handleFailure(error.localizedDescription, timings: settings.blinkTimings)
        }
    }

    private func handleFailure(_ message: String, timings: BlinkTimings) async {
        lastError = message
        lastSummary = "Failed"
        status = .blinking
        // Long blink indicates failure only
        await blinkEngine.blinkError(timings: timings)
        if !Task.isCancelled {
            status = .idle
        }
    }

    private func openSystemSettings(pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }
}
