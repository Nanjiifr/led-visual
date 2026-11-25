import Foundation
import IOKit
import IOKit.hidsystem

public struct BlinkStep: Equatable, Sendable {
    public let ledOn: Bool
    public let durationMs: Int

    public init(ledOn: Bool, durationMs: Int) {
        self.ledOn = ledOn
        self.durationMs = durationMs
    }
}

public enum BlinkPatternBuilder {
    /// Generates LED steps for one or more 1-based answers.
    /// - Single answer `[3]`: 3 short blinks, then stops (no trailing long blink).
    /// - Multi-select `[1, 3]`: 1 short blink, inter-answer pause, then 3 short blinks, then stops.
    public static func stepsForAnswers(
        _ answers: [Int],
        initiallyOn: Bool,
        timings: BlinkTimings = .default
    ) -> [BlinkStep] {
        let validAnswers = answers.filter { $0 > 0 }
        guard !validAnswers.isEmpty else { return [] }

        var steps: [BlinkStep] = []

        if initiallyOn {
            steps.append(BlinkStep(ledOn: false, durationMs: timings.preBlinkDarkMs))
        }

        for (groupIndex, blinkCount) in validAnswers.enumerated() {
            let isLastGroup = (groupIndex == validAnswers.count - 1)
            for blinkIndex in 0..<blinkCount {
                let isLastBlinkInGroup = (blinkIndex == blinkCount - 1)
                steps.append(BlinkStep(ledOn: true, durationMs: timings.shortOnMs))

                if isLastBlinkInGroup && isLastGroup {
                    steps.append(BlinkStep(ledOn: false, durationMs: initiallyOn ? timings.preBlinkDarkMs : 0))
                } else if isLastBlinkInGroup {
                    steps.append(BlinkStep(ledOn: false, durationMs: timings.interAnswerPauseMs))
                } else {
                    steps.append(BlinkStep(ledOn: false, durationMs: timings.shortOffMs))
                }
            }
        }

        return steps
    }

    /// Generates a single long blink indicating a failed request or unparseable answer.
    public static func stepsForError(
        initiallyOn: Bool,
        timings: BlinkTimings = .default
    ) -> [BlinkStep] {
        var steps: [BlinkStep] = []
        if initiallyOn {
            steps.append(BlinkStep(ledOn: false, durationMs: timings.preBlinkDarkMs))
        }
        steps.append(BlinkStep(ledOn: true, durationMs: timings.errorLongOnMs))
        steps.append(BlinkStep(ledOn: false, durationMs: initiallyOn ? timings.preBlinkDarkMs : 0))
        return steps
    }
}

public enum CapsLockLEDError: Error, LocalizedError, Sendable {
    case hidSystemNotFound
    case openFailed(kern_return_t)
    case getStateFailed(kern_return_t)
    case setStateFailed(kern_return_t)

    public var errorDescription: String? {
        switch self {
        case .hidSystemNotFound:
            return "Could not locate macOS IOHIDSystem service."
        case .openFailed(let code):
            return "Failed to open IOHIDSystem connection (kern_return \(code))."
        case .getStateFailed(let code):
            return "Failed to read Caps Lock LED state (kern_return \(code))."
        case .setStateFailed(let code):
            return "Failed to set Caps Lock LED state (kern_return \(code))."
        }
    }
}

public protocol CapsLockLEDControlling: Sendable {
    func getState() throws -> Bool
    func setState(_ enabled: Bool) throws
}

public final class IOKitCapsLockLEDController: CapsLockLEDControlling, Sendable {
    public init() {}

    private func withConnection<T>(_ body: (io_connect_t) throws -> T) throws -> T {
        let service = IOServiceGetMatchingService(
            kIOMainPortDefault,
            IOServiceMatching(kIOHIDSystemClass)
        )
        guard service != MACH_PORT_NULL else {
            throw CapsLockLEDError.hidSystemNotFound
        }
        defer { IOObjectRelease(service) }

        var connect: io_connect_t = 0
        let openResult = IOServiceOpen(
            service,
            mach_task_self_,
            UInt32(kIOHIDParamConnectType),
            &connect
        )
        guard openResult == KERN_SUCCESS else {
            throw CapsLockLEDError.openFailed(openResult)
        }
        defer { IOServiceClose(connect) }

        return try body(connect)
    }

    public func getState() throws -> Bool {
        try withConnection { connect in
            var state = false
            let result = IOHIDGetModifierLockState(connect, Int32(kIOHIDCapsLockState), &state)
            guard result == KERN_SUCCESS else {
                throw CapsLockLEDError.getStateFailed(result)
            }
            return state
        }
    }

    public func setState(_ enabled: Bool) throws {
        try withConnection { connect in
            let result = IOHIDSetModifierLockState(connect, Int32(kIOHIDCapsLockState), enabled)
            guard result == KERN_SUCCESS else {
                throw CapsLockLEDError.setStateFailed(result)
            }
        }
    }
}

public actor BlinkEngine {
    public typealias Sleeper = @Sendable (Int) async throws -> Void

    private let controller: any CapsLockLEDControlling
    private let sleepMs: Sleeper
    private var activeTask: Task<Void, Never>?
    private var savedOriginalState: Bool?

    public init(
        controller: any CapsLockLEDControlling = IOKitCapsLockLEDController(),
        sleepMs: @escaping Sleeper = { ms in
            guard ms > 0 else { return }
            try await Task.sleep(for: .milliseconds(ms))
        }
    ) {
        self.controller = controller
        self.sleepMs = sleepMs
    }

    public func cancel() {
        activeTask?.cancel()
        activeTask = nil
        restoreSavedStateIfNeeded()
    }

    public func blinkAnswers(_ answers: [Int], timings: BlinkTimings = .default) async {
        await runSequence { initiallyOn in
            BlinkPatternBuilder.stepsForAnswers(answers, initiallyOn: initiallyOn, timings: timings)
        }
    }

    public func blinkError(timings: BlinkTimings = .default) async {
        await runSequence { initiallyOn in
            BlinkPatternBuilder.stepsForError(initiallyOn: initiallyOn, timings: timings)
        }
    }

    private func runSequence(
        buildSteps: @escaping @Sendable (Bool) -> [BlinkStep]
    ) async {
        // Cancel any currently blinking sequence and restore baseline first
        activeTask?.cancel()
        activeTask = nil
        restoreSavedStateIfNeeded()

        let originalState: Bool
        do {
            originalState = try controller.getState()
        } catch {
            return
        }
        savedOriginalState = originalState

        let steps = buildSteps(originalState)
        let controller = self.controller
        let sleepMs = self.sleepMs

        let task = Task {
            for step in steps {
                if Task.isCancelled { break }
                try? controller.setState(step.ledOn)
                if step.durationMs > 0 {
                    do {
                        try await sleepMs(step.durationMs)
                    } catch {
                        break
                    }
                }
            }
        }

        activeTask = task
        await task.value

        // Only clear & restore if this task is still the active one
        if activeTask == task {
            activeTask = nil
            restoreSavedStateIfNeeded()
        }
    }

    private func restoreSavedStateIfNeeded() {
        guard let original = savedOriginalState else { return }
        savedOriginalState = nil
        try? controller.setState(original)
    }
}
