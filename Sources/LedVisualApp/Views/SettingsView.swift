import LedVisualCore
import SwiftUI

struct SettingsView: View {
    @Bindable var coordinator: AppCoordinator

    @State private var apiKeyDraft: String = ""
    @State private var keyStatusMessage: String?
    @State private var keyStatusIsError = false
    @State private var isTestingKey = false

    var body: some View {
        Form {
            // MARK: - Hotkey Section
            Section("Hotkey & Capture") {
                LabeledContent("Trigger Hotkey") {
                    HotkeyRecorderView(
                        hotkey: $coordinator.settings.hotkey,
                        isRecording: $coordinator.isEditingHotkey
                    )
                }

                Toggle("Swallow hotkey keystroke", isOn: $coordinator.settings.swallowHotkeyEvents)

                Stepper(
                    "Hold threshold: \(coordinator.settings.holdThresholdMs) ms",
                    value: $coordinator.settings.holdThresholdMs,
                    in: 200...1000,
                    step: 50
                )

                Stepper(
                    "Recording frame interval: \(coordinator.settings.recordingSampleIntervalMs) ms",
                    value: $coordinator.settings.recordingSampleIntervalMs,
                    in: 250...1500,
                    step: 50
                )

                Stepper(
                    "Max recording frames: \(coordinator.settings.maxRecordingFrames)",
                    value: $coordinator.settings.maxRecordingFrames,
                    in: 6...40,
                    step: 2
                )
            }

            // MARK: - Google AI Studio & Model Section
            Section("Google AI Studio") {
                HStack(spacing: 8) {
                    SecureField("API Key (stored in macOS Keychain)", text: $apiKeyDraft)
                        .textFieldStyle(.roundedBorder)

                    Button("Save") {
                        saveKey()
                    }
                    .disabled(apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                    Button(isTestingKey ? "Testing…" : "Test") {
                        testKey()
                    }
                    .disabled(isTestingKey || apiKeyDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }

                if let keyStatusMessage {
                    Text(keyStatusMessage)
                        .font(.caption)
                        .foregroundStyle(keyStatusIsError ? .red : .green)
                }

                Picker("Model", selection: $coordinator.settings.model) {
                    ForEach(AIModel.allCases) { model in
                        Text(model.displayName).tag(model)
                    }
                }

                Toggle(
                    "Fallback to alternate model on 503 overload",
                    isOn: $coordinator.settings.autoFallbackOnServerError
                )
            }

            // MARK: - Caps Lock LED Section
            Section("Caps Lock LED Blinks") {
                Stepper(
                    "Short blink ON: \(coordinator.settings.blinkTimings.shortOnMs) ms",
                    value: $coordinator.settings.blinkTimings.shortOnMs,
                    in: 80...500,
                    step: 20
                )

                Stepper(
                    "Short blink OFF: \(coordinator.settings.blinkTimings.shortOffMs) ms",
                    value: $coordinator.settings.blinkTimings.shortOffMs,
                    in: 80...500,
                    step: 20
                )

                Stepper(
                    "Multi-select pause: \(coordinator.settings.blinkTimings.interAnswerPauseMs) ms",
                    value: $coordinator.settings.blinkTimings.interAnswerPauseMs,
                    in: 400...2000,
                    step: 50
                )

                Stepper(
                    "Failed request long blink: \(coordinator.settings.blinkTimings.errorLongOnMs) ms",
                    value: $coordinator.settings.blinkTimings.errorLongOnMs,
                    in: 700...3000,
                    step: 100
                )

                HStack(spacing: 10) {
                    Button("Test Single (3)") {
                        coordinator.testBlink(answers: [3])
                    }
                    Button("Test Multi (1, 3)") {
                        coordinator.testBlink(answers: [1, 3])
                    }
                    Button("Test Failed (Long)") {
                        coordinator.testErrorBlink()
                    }
                    Spacer()
                    Button("Reset Timings") {
                        coordinator.settings.blinkTimings = .default
                    }
                }
            }

            // MARK: - Permissions Section
            Section("macOS Permissions") {
                HStack {
                    Image(systemName: coordinator.hasAccessibilityPermission ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .foregroundStyle(coordinator.hasAccessibilityPermission ? .green : .orange)
                    Text("Accessibility (Global Hotkey)")
                    Spacer()
                    if !coordinator.hasAccessibilityPermission {
                        Button("Grant Access") {
                            coordinator.requestAccessibilityPermission()
                        }
                    } else {
                        Text("Granted")
                            .foregroundStyle(.secondary)
                    }
                }

                HStack {
                    Image(systemName: coordinator.hasScreenRecordingPermission ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .foregroundStyle(coordinator.hasScreenRecordingPermission ? .green : .orange)
                    Text("Screen Recording (Screenshot & Hold-to-Record)")
                    Spacer()
                    if !coordinator.hasScreenRecordingPermission {
                        Button("Grant Access") {
                            coordinator.requestScreenRecordingPermission()
                        }
                    } else {
                        Text("Granted")
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 610)
        .onAppear {
            apiKeyDraft = coordinator.loadAPIKey()
            coordinator.refreshStatusFlags()
        }
    }

    private func saveKey() {
        do {
            try coordinator.saveAPIKey(apiKeyDraft)
            keyStatusIsError = false
            keyStatusMessage = "Saved securely to macOS Keychain."
        } catch {
            keyStatusIsError = true
            keyStatusMessage = error.localizedDescription
        }
    }

    private func testKey() {
        isTestingKey = true
        keyStatusMessage = nil
        let candidate = apiKeyDraft
        Task {
            let result = await coordinator.verifyAPIKey(candidate)
            await MainActor.run {
                isTestingKey = false
                switch result {
                case .success(let models):
                    try? coordinator.saveAPIKey(candidate)
                    keyStatusIsError = false
                    let list = models.isEmpty ? "connected" : models.joined(separator: ", ")
                    keyStatusMessage = "Verified & saved. Available: \(list)"
                case .failure(let error):
                    keyStatusIsError = true
                    keyStatusMessage = error.localizedDescription
                }
            }
        }
    }
}
