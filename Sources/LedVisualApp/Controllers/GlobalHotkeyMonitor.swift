import ApplicationServices
import Carbon
import CoreGraphics
import Foundation
import LedVisualCore

/// Global keyboard monitor using a CGEventTap that supports:
/// - Modifier + key combos (e.g. ⌥Space, ⌘⇧A)
/// - Single keys without modifiers (e.g. F8)
/// - Standalone modifier keys (e.g. Right ⌥, Right ⌘, Fn)
/// Distinguishes short tap (screenshot) vs press-and-hold (screen recording).
@MainActor
public final class GlobalHotkeyMonitor {
    public var onTap: (() -> Void)?
    public var onHoldStarted: (() -> Void)?
    public var onHoldEnded: (() -> Void)?

    public private(set) var isRunning = false
    public var isSuspended = false {
        didSet {
            if isSuspended {
                cancelActivePress()
            }
        }
    }

    private var hotkey: Hotkey
    private var holdThresholdMs: Int
    private var swallowEvents: Bool

    private nonisolated(unsafe) var eventTap: CFMachPort?
    private nonisolated(unsafe) var runLoopSource: CFRunLoopSource?

    // Press tracking state
    private var isKeyCurrentlyDown = false
    private var isHoldActive = false
    private var holdTimer: DispatchWorkItem?
    private var modifierInterruptedByOtherKey = false

    public init(
        hotkey: Hotkey = .defaultHotkey,
        holdThresholdMs: Int = 350,
        swallowEvents: Bool = true
    ) {
        self.hotkey = hotkey
        self.holdThresholdMs = max(150, holdThresholdMs)
        self.swallowEvents = swallowEvents
    }

    deinit {
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
        }
    }

    public static func isAccessibilityTrusted(prompt: Bool = false) -> Bool {
        let key = "AXTrustedCheckOptionPrompt" as CFString
        let options = [key: prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    public func updateConfiguration(
        hotkey: Hotkey,
        holdThresholdMs: Int,
        swallowEvents: Bool
    ) {
        cancelActivePress()
        self.hotkey = hotkey
        self.holdThresholdMs = max(150, holdThresholdMs)
        self.swallowEvents = swallowEvents
    }

    @discardableResult
    public func start() -> Bool {
        if isRunning, eventTap != nil {
            return true
        }

        let mask: CGEventMask =
            (1 << CGEventType.keyDown.rawValue) |
            (1 << CGEventType.keyUp.rawValue) |
            (1 << CGEventType.flagsChanged.rawValue)

        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, userInfo in
                guard let userInfo else {
                    return Unmanaged.passUnretained(event)
                }
                let monitor = Unmanaged<GlobalHotkeyMonitor>.fromOpaque(userInfo).takeUnretainedValue()
                let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
                let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0
                let flagsRaw = event.flags.rawValue
                let shouldSwallow = MainActor.assumeIsolated {
                    monitor.handleEvent(
                        type: type,
                        keyCode: keyCode,
                        flags: CGEventFlags(rawValue: flagsRaw),
                        isRepeat: isRepeat
                    )
                }
                return shouldSwallow ? nil : Unmanaged.passUnretained(event)
            },
            userInfo: selfPtr
        ) else {
            isRunning = false
            return false
        }

        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)

        self.eventTap = tap
        self.runLoopSource = source
        self.isRunning = true
        return true
    }

    public func stop() {
        cancelActivePress()
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
            runLoopSource = nil
        }
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
            eventTap = nil
        }
        isRunning = false
    }

    // MARK: - Event Handling

    /// Returns `true` if the event should be swallowed, `false` to pass it through.
    private func handleEvent(
        type: CGEventType,
        keyCode: UInt16,
        flags: CGEventFlags,
        isRepeat: Bool
    ) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = eventTap {
                CGEvent.tapEnable(tap: tap, enable: true)
            }
            return false
        }

        if isSuspended {
            return false
        }

        switch hotkey.kind {
        case .keyCombo:
            return handleKeyComboEvent(type: type, keyCode: keyCode, flags: flags, isRepeat: isRepeat)
        case .modifierOnly:
            return handleModifierOnlyEvent(type: type, keyCode: keyCode, flags: flags)
        }
    }

    private func handleKeyComboEvent(
        type: CGEventType,
        keyCode: UInt16,
        flags: CGEventFlags,
        isRepeat: Bool
    ) -> Bool {
        switch type {
        case .keyDown:
            if isKeyCurrentlyDown && keyCode == hotkey.keyCode {
                // Swallow repeats while our hotkey is held down
                return swallowEvents
            }
            guard !isRepeat, hotkey.matchesKeyDown(keyCode: keyCode, cgFlags: flags) else {
                return false
            }
            beginPress()
            return swallowEvents

        case .keyUp:
            guard isKeyCurrentlyDown, keyCode == hotkey.keyCode else {
                return false
            }
            endPress()
            return swallowEvents

        default:
            return false
        }
    }

    private func handleModifierOnlyEvent(
        type: CGEventType,
        keyCode: UInt16,
        flags: CGEventFlags
    ) -> Bool {
        switch type {
        case .keyDown:
            // If user presses a regular key while holding a standalone modifier (e.g. Right ⌥ + C),
            // treat it as a normal keyboard shortcut instead of firing LedVisual.
            if isKeyCurrentlyDown && !isHoldActive {
                modifierInterruptedByOtherKey = true
                cancelActivePress()
            }
            return false

        case .flagsChanged:
            guard keyCode == hotkey.keyCode,
                  let targetFlag = Hotkey.cgFlag(forModifierKeyCode: keyCode) else {
                return false
            }

            let isFlagDown = flags.contains(targetFlag)
            if isFlagDown && !isKeyCurrentlyDown {
                // Ensure no other standard modifiers are already held down
                let otherFlags = flags.subtracting([targetFlag, .maskNonCoalesced, .maskAlphaShift])
                let extraModifiers = HotkeyModifiers(cgFlags: otherFlags)
                if extraModifiers.isEmpty {
                    modifierInterruptedByOtherKey = false
                    beginPress()
                }
            } else if !isFlagDown && isKeyCurrentlyDown {
                if !modifierInterruptedByOtherKey {
                    endPress()
                } else {
                    cancelActivePress()
                }
            }
            return false

        default:
            return false
        }
    }

    // MARK: - Tap vs Hold State Machine

    private func beginPress() {
        isKeyCurrentlyDown = true
        isHoldActive = false

        holdTimer?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            guard let self, self.isKeyCurrentlyDown else { return }
            self.isHoldActive = true
            self.onHoldStarted?()
        }
        holdTimer = workItem
        DispatchQueue.main.asyncAfter(
            deadline: .now() + .milliseconds(holdThresholdMs),
            execute: workItem
        )
    }

    private func endPress() {
        holdTimer?.cancel()
        holdTimer = nil

        let wasHold = isHoldActive
        isKeyCurrentlyDown = false
        isHoldActive = false

        if wasHold {
            onHoldEnded?()
        } else {
            onTap?()
        }
    }

    private func cancelActivePress() {
        holdTimer?.cancel()
        holdTimer = nil
        let wasHold = isHoldActive
        isKeyCurrentlyDown = false
        isHoldActive = false
        if wasHold {
            onHoldEnded?()
        }
    }
}
