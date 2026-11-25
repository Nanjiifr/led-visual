import AppKit
import LedVisualCore
import SwiftUI

struct HotkeyRecorderView: View {
    @Binding var hotkey: Hotkey
    @Binding var isRecording: Bool
    @State private var warningText: String?
    @State private var pendingModifierKeyCode: UInt16?
    @State private var localMonitor: Any?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                Button {
                    if isRecording {
                        stopRecording()
                    } else {
                        startRecording()
                    }
                } label: {
                    HStack(spacing: 6) {
                        if isRecording {
                            Circle()
                                .fill(Color.red)
                                .frame(width: 8, height: 8)
                            Text("Press any key or combo…")
                                .foregroundStyle(.primary)
                        } else {
                            Text(hotkey.displayString)
                                .font(.system(.body, design: .monospaced).weight(.semibold))
                                .foregroundStyle(.primary)
                        }
                    }
                    .frame(minWidth: 180)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 6)
                    .background(
                        RoundedRectangle(cornerRadius: 7)
                            .fill(isRecording ? Color.accentColor.opacity(0.14) : Color.secondary.opacity(0.12))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 7)
                            .stroke(isRecording ? Color.accentColor : Color.secondary.opacity(0.3), lineWidth: 1)
                    )
                }
                .buttonStyle(.plain)

                if isRecording {
                    Button("Cancel") {
                        stopRecording()
                    }
                } else {
                    Button("Reset") {
                        hotkey = .defaultHotkey
                        warningText = nil
                    }
                    .disabled(hotkey == .defaultHotkey)
                }
            }

            if let warningText {
                Text(warningText)
                    .font(.caption)
                    .foregroundStyle(.orange)
            } else {
                Text("Supports modifier combos (e.g. ⌥Space), single keys (e.g. F8), or standalone modifiers (e.g. Right ⌥). Tap = screenshot, Hold = record screen.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .onDisappear {
            stopRecording()
        }
    }

    private func startRecording() {
        warningText = nil
        pendingModifierKeyCode = nil
        isRecording = true

        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { event in
            handleRecordedEvent(event)
        }
    }

    private func stopRecording() {
        if let monitor = localMonitor {
            NSEvent.removeMonitor(monitor)
            localMonitor = nil
        }
        pendingModifierKeyCode = nil
        isRecording = false
    }

    private func handleRecordedEvent(_ event: NSEvent) -> NSEvent? {
        let keyCode = event.keyCode

        if event.type == .keyDown {
            // Escape cancels recording unless modifiers are held
            let mods = modifiersFromNSEvent(event.modifierFlags)
            if keyCode == 53 && mods.isEmpty {
                stopRecording()
                return nil
            }
            if keyCode == Hotkey.capsLockKeyCode {
                warningText = "Caps Lock cannot be bound because it is used for the LED output."
                return nil
            }

            pendingModifierKeyCode = nil
            hotkey = Hotkey(keyCode: keyCode, modifiers: mods, kind: .keyCombo)
            warningText = nil
            stopRecording()
            return nil
        }

        if event.type == .flagsChanged {
            if keyCode == Hotkey.capsLockKeyCode {
                warningText = "Caps Lock cannot be bound because it is used for the LED output."
                return nil
            }
            guard Hotkey.isModifierKeyCode(keyCode) else { return nil }

            let isPressed = isModifierKeyPressed(keyCode: keyCode, flags: event.modifierFlags)
            if isPressed {
                // Remember this modifier in case user releases it without pressing another key
                let mods = modifiersFromNSEvent(event.modifierFlags)
                if mods.rawValue.nonzeroBitCount <= 1 || keyCode == 63 {
                    pendingModifierKeyCode = keyCode
                } else {
                    pendingModifierKeyCode = nil
                }
            } else if pendingModifierKeyCode == keyCode {
                // User pressed & released a single standalone modifier key -> bind it!
                hotkey = Hotkey(keyCode: keyCode, modifiers: [], kind: .modifierOnly)
                warningText = nil
                stopRecording()
            }
            return nil
        }

        return event
    }

    private func modifiersFromNSEvent(_ flags: NSEvent.ModifierFlags) -> HotkeyModifiers {
        var result: HotkeyModifiers = []
        if flags.contains(.control) { result.insert(.control) }
        if flags.contains(.option)  { result.insert(.option) }
        if flags.contains(.shift)   { result.insert(.shift) }
        if flags.contains(.command) { result.insert(.command) }
        return result
    }

    private func isModifierKeyPressed(keyCode: UInt16, flags: NSEvent.ModifierFlags) -> Bool {
        switch keyCode {
        case 54, 55: return flags.contains(.command)
        case 56, 60: return flags.contains(.shift)
        case 58, 61: return flags.contains(.option)
        case 59, 62: return flags.contains(.control)
        case 63:     return flags.contains(.function)
        default:     return false
        }
    }
}
