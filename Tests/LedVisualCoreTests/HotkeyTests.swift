import CoreGraphics
import XCTest
@testable import LedVisualCore

final class HotkeyTests: XCTestCase {
    func testDefaultHotkeyMatchesOptionSpaceOnly() {
        let hotkey = Hotkey.defaultHotkey
        XCTAssertEqual(hotkey.displayString, "⌥Space")
        XCTAssertTrue(hotkey.matchesKeyDown(keyCode: 49, cgFlags: .maskAlternate))
        XCTAssertFalse(hotkey.matchesKeyDown(keyCode: 49, cgFlags: []))
        XCTAssertFalse(hotkey.matchesKeyDown(keyCode: 49, cgFlags: [.maskAlternate, .maskCommand]))
        XCTAssertFalse(hotkey.matchesKeyDown(keyCode: 0, cgFlags: .maskAlternate))
    }

    func testSingleFunctionKeyMatchesWithoutModifiers() {
        let f8 = Hotkey(keyCode: 100, modifiers: [], kind: .keyCombo)
        XCTAssertEqual(f8.displayString, "F8")
        XCTAssertTrue(f8.matchesKeyDown(keyCode: 100, cgFlags: []))
        XCTAssertFalse(f8.matchesKeyDown(keyCode: 100, cgFlags: .maskCommand))
    }

    func testStandaloneModifierHotkey() throws {
        let rightOpt = Hotkey(keyCode: 61, modifiers: [], kind: .modifierOnly)
        XCTAssertEqual(rightOpt.displayString, "Right ⌥")
        XCTAssertTrue(Hotkey.isModifierKeyCode(61))
        XCTAssertEqual(Hotkey.cgFlag(forModifierKeyCode: 61), .maskAlternate)

        let encoded = try JSONEncoder().encode(rightOpt)
        let decoded = try JSONDecoder().decode(Hotkey.self, from: encoded)
        XCTAssertEqual(decoded, rightOpt)
    }
}
