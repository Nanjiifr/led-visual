import XCTest
@testable import LedVisualCore

final class MockCapsLockController: CapsLockLEDControlling, @unchecked Sendable {
    private let lock = NSLock()
    private var currentState: Bool
    private(set) var transitions: [Bool] = []

    init(initialState: Bool = false) {
        self.currentState = initialState
    }

    func getState() throws -> Bool {
        lock.withLock { currentState }
    }

    func setState(_ enabled: Bool) throws {
        lock.withLock {
            currentState = enabled
            transitions.append(enabled)
        }
    }

    var finalState: Bool {
        lock.withLock { currentState }
    }

    var recordedTransitions: [Bool] {
        lock.withLock { transitions }
    }
}

final class BlinkEngineTests: XCTestCase {
    func testSingleAnswerPatternHasNoTrailingLongBlink() {
        let timings = BlinkTimings(
            shortOnMs: 150,
            shortOffMs: 170,
            interAnswerPauseMs: 800,
            errorLongOnMs: 1400,
            preBlinkDarkMs: 250
        )

        let steps = BlinkPatternBuilder.stepsForAnswers([3], initiallyOn: false, timings: timings)
        XCTAssertEqual(steps, [
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 170),
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 170),
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 0)
        ])
    }

    func testMultiSelectPatternSeparatesGroupsWithLongPause() {
        let timings = BlinkTimings(
            shortOnMs: 150,
            shortOffMs: 170,
            interAnswerPauseMs: 800,
            errorLongOnMs: 1400,
            preBlinkDarkMs: 250
        )

        let steps = BlinkPatternBuilder.stepsForAnswers([1, 3], initiallyOn: false, timings: timings)
        XCTAssertEqual(steps, [
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 800),
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 170),
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 170),
            BlinkStep(ledOn: true, durationMs: 150),
            BlinkStep(ledOn: false, durationMs: 0)
        ])
    }

    func testErrorPatternIsSingleLongBlink() {
        let timings = BlinkTimings(errorLongOnMs: 1500, preBlinkDarkMs: 250)
        let steps = BlinkPatternBuilder.stepsForError(initiallyOn: false, timings: timings)
        XCTAssertEqual(steps, [
            BlinkStep(ledOn: true, durationMs: 1500),
            BlinkStep(ledOn: false, durationMs: 0)
        ])
    }

    func testEngineRestoresInitialCapsLockOnState() async {
        let mock = MockCapsLockController(initialState: true)
        let engine = BlinkEngine(controller: mock, sleepMs: { _ in })

        await engine.blinkAnswers([2], timings: .default)

        XCTAssertTrue(mock.finalState, "Original Caps Lock ON state must be restored")
        // Initial dark baseline, 2 blinks (ON/OFF x2), final restore ON
        XCTAssertEqual(mock.recordedTransitions, [false, true, false, true, false, true])
    }

    func testEngineRestoresInitialCapsLockOffStateOnError() async {
        let mock = MockCapsLockController(initialState: false)
        let engine = BlinkEngine(controller: mock, sleepMs: { _ in })

        await engine.blinkError(timings: .default)

        XCTAssertFalse(mock.finalState)
        XCTAssertEqual(mock.recordedTransitions, [true, false, false])
    }
}
