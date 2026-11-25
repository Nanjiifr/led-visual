import CoreGraphics
import Foundation

public enum HotkeyKind: String, Codable, Equatable, Hashable, Sendable {
    case keyCombo
    case modifierOnly
}

public struct HotkeyModifiers: OptionSet, Codable, Equatable, Hashable, Sendable {
    public let rawValue: UInt32

    public init(rawValue: UInt32) {
        self.rawValue = rawValue
    }

    public static let control = HotkeyModifiers(rawValue: 1 << 0)
    public static let option  = HotkeyModifiers(rawValue: 1 << 1)
    public static let shift   = HotkeyModifiers(rawValue: 1 << 2)
    public static let command = HotkeyModifiers(rawValue: 1 << 3)

    public init(cgFlags: CGEventFlags) {
        var result: HotkeyModifiers = []
        if cgFlags.contains(.maskControl) { result.insert(.control) }
        if cgFlags.contains(.maskAlternate) { result.insert(.option) }
        if cgFlags.contains(.maskShift) { result.insert(.shift) }
        if cgFlags.contains(.maskCommand) { result.insert(.command) }
        self = result
    }

    public var symbolString: String {
        var s = ""
        if contains(.control) { s += "⌃" }
        if contains(.option) { s += "⌥" }
        if contains(.shift) { s += "⇧" }
        if contains(.command) { s += "⌘" }
        return s
    }
}

public struct Hotkey: Codable, Equatable, Hashable, Sendable {
    public var keyCode: UInt16
    public var modifiers: HotkeyModifiers
    public var kind: HotkeyKind

    public init(keyCode: UInt16, modifiers: HotkeyModifiers = [], kind: HotkeyKind = .keyCombo) {
        self.keyCode = keyCode
        self.modifiers = modifiers
        self.kind = kind
    }

    /// Default hotkey: Option + Space (⌥Space)
    public static let defaultHotkey = Hotkey(keyCode: 49, modifiers: [.option], kind: .keyCombo)

    /// Virtual key code for Caps Lock on macOS. Disallowed as a hotkey since Caps Lock is the LED output.
    public static let capsLockKeyCode: UInt16 = 57

    /// Virtual key codes for standalone modifier keys.
    public static let modifierKeyCodes: Set<UInt16> = [
        54, // Right Command
        55, // Left Command
        56, // Left Shift
        58, // Left Option
        59, // Left Control
        60, // Right Shift
        61, // Right Option
        62, // Right Control
        63  // Fn / Globe
    ]

    public static func isModifierKeyCode(_ keyCode: UInt16) -> Bool {
        modifierKeyCodes.contains(keyCode)
    }

    public static func cgFlag(forModifierKeyCode keyCode: UInt16) -> CGEventFlags? {
        switch keyCode {
        case 54, 55: return .maskCommand
        case 56, 60: return .maskShift
        case 58, 61: return .maskAlternate
        case 59, 62: return .maskControl
        case 63:     return .maskSecondaryFn
        default:     return nil
        }
    }

    /// Whether a CGEvent keyDown matches this hotkey (for `.keyCombo` hotkeys).
    public func matchesKeyDown(keyCode: UInt16, cgFlags: CGEventFlags) -> Bool {
        guard kind == .keyCombo else { return false }
        let relevantModifiers = HotkeyModifiers(cgFlags: cgFlags)
        return self.keyCode == keyCode && self.modifiers == relevantModifiers
    }

    /// Human-readable representation such as `⌥Space`, `F8`, or `Right ⌥`.
    public var displayString: String {
        switch kind {
        case .modifierOnly:
            return Self.keyName(for: keyCode)
        case .keyCombo:
            return modifiers.symbolString + Self.keyName(for: keyCode)
        }
    }

    public static func keyName(for keyCode: UInt16) -> String {
        switch keyCode {
        // Letters (ANSI layout standard virtual key codes)
        case 0: return "A"
        case 1: return "S"
        case 2: return "D"
        case 3: return "F"
        case 4: return "H"
        case 5: return "G"
        case 6: return "Z"
        case 7: return "X"
        case 8: return "C"
        case 9: return "V"
        case 10: return "§"
        case 11: return "B"
        case 12: return "Q"
        case 13: return "W"
        case 14: return "E"
        case 15: return "R"
        case 16: return "Y"
        case 17: return "T"
        case 18: return "1"
        case 19: return "2"
        case 20: return "3"
        case 21: return "4"
        case 22: return "6"
        case 23: return "5"
        case 24: return "="
        case 25: return "9"
        case 26: return "7"
        case 27: return "-"
        case 28: return "8"
        case 29: return "0"
        case 30: return "]"
        case 31: return "O"
        case 32: return "U"
        case 33: return "["
        case 34: return "I"
        case 35: return "P"
        case 36: return "↩"
        case 37: return "L"
        case 38: return "J"
        case 39: return "'"
        case 40: return "K"
        case 41: return ";"
        case 42: return "\\"
        case 43: return ","
        case 44: return "/"
        case 45: return "N"
        case 46: return "M"
        case 47: return "."
        case 48: return "⇥"
        case 49: return "Space"
        case 50: return "`"
        case 51: return "⌫"
        case 53: return "⎋"
        // Modifiers
        case 54: return "Right ⌘"
        case 55: return "Left ⌘"
        case 56: return "Left ⇧"
        case 57: return "⇪"
        case 58: return "Left ⌥"
        case 59: return "Left ⌃"
        case 60: return "Right ⇧"
        case 61: return "Right ⌥"
        case 62: return "Right ⌃"
        case 63: return "Fn"
        // Keypad
        case 65: return "Num ."
        case 67: return "Num *"
        case 69: return "Num +"
        case 71: return "Clear"
        case 75: return "Num /"
        case 76: return "Num ↩"
        case 78: return "Num -"
        case 81: return "Num ="
        case 82: return "Num 0"
        case 83: return "Num 1"
        case 84: return "Num 2"
        case 85: return "Num 3"
        case 86: return "Num 4"
        case 87: return "Num 5"
        case 88: return "Num 6"
        case 89: return "Num 7"
        case 91: return "Num 8"
        case 92: return "Num 9"
        // Function keys
        case 122: return "F1"
        case 120: return "F2"
        case 99:  return "F3"
        case 118: return "F4"
        case 96:  return "F5"
        case 97:  return "F6"
        case 98:  return "F7"
        case 100: return "F8"
        case 101: return "F9"
        case 109: return "F10"
        case 103: return "F11"
        case 111: return "F12"
        case 105: return "F13"
        case 107: return "F14"
        case 113: return "F15"
        case 106: return "F16"
        case 64:  return "F17"
        case 79:  return "F18"
        case 80:  return "F19"
        case 90:  return "F20"
        // Navigation
        case 115: return "↖"
        case 116: return "⇞"
        case 117: return "⌦"
        case 119: return "↘"
        case 121: return "⇟"
        case 123: return "←"
        case 124: return "→"
        case 125: return "↓"
        case 126: return "↑"
        default: return "Key(\(keyCode))"
        }
    }
}
