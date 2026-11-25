import AppKit
import CoreGraphics
import XCTest
@testable import LedVisualCore

final class LiveIntegrationTests: XCTestCase {
    private func renderTextImage(_ text: String, width: Int = 900, height: Int = 500) throws -> CGImage {
        let size = NSSize(width: width, height: height)
        let image = NSImage(size: size)
        image.lockFocus()
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()

        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 30, weight: .medium),
            .foregroundColor: NSColor.black
        ]
        (text as NSString).draw(
            in: NSRect(x: 40, y: 30, width: CGFloat(width - 80), height: CGFloat(height - 60)),
            withAttributes: attrs
        )
        image.unlockFocus()

        let tiff = try XCTUnwrap(image.tiffRepresentation)
        let rep = try XCTUnwrap(NSBitmapImageRep(data: tiff))
        return try XCTUnwrap(rep.cgImage)
    }

    func testLiveGemmaSingleAndMultiSelectMCQ() async throws {
        guard ProcessInfo.processInfo.environment["LEDVISUAL_LIVE_TEST"] == "1" else {
            throw XCTSkip("Set LEDVISUAL_LIVE_TEST=1 to run live Google AI Studio tests.")
        }

        let apiKey: String
        if let envKey = ProcessInfo.processInfo.environment["LEDVISUAL_TEST_API_KEY"], !envKey.isEmpty {
            apiKey = envKey
        } else if let stored = try KeychainService().loadAPIKey(), !stored.isEmpty {
            apiKey = stored
        } else {
            XCTFail("No API key found in Keychain or environment.")
            return
        }

        // Verify read-only Caps Lock HID access on this Mac
        let hidController = IOKitCapsLockLEDController()
        _ = try hidController.getState()

        let client = GeminiClient(maxRetriesPerModel: 3)

        // 1. Single-choice screenshot test
        let singleCG = try renderTextImage("""
        Question: Which planet is known as the Red Planet?

        1. Venus
        2. Jupiter
        3. Mars
        4. Saturn
        """)
        let singleJPEG = try XCTUnwrap(FrameImageEncoder.encodeJPEG(from: singleCG))
        let singleResult = try await client.solveMCQ(
            jpegFrames: [singleJPEG],
            apiKey: apiKey,
            model: .geminiDecision1,
            autoFallback: true
        )
        print("Live Single MCQ -> answers=\(singleResult.answers) raw=\(singleResult.rawText) model=\(singleResult.modelUsed.rawValue)")
        XCTAssertEqual(singleResult.answers, [3])

        // 2. Multi-frame scrolled multi-select test (simulates press-and-hold screen recording)
        let frame1CG = try renderTextImage("""
        Question (Select ALL that apply):
        Which of the following are European capitals?

        [ ] 1. Paris
        [ ] 2. Tokyo
        (scrolling down for more options...)
        """)
        let frame2CG = try renderTextImage("""
        (continued from above)
        [ ] 3. Rome
        [ ] 4. Cairo
        """)

        var sampler = FrameSampler(maxFrames: 10, diffThreshold: 2.0)
        for cg in [frame1CG, frame1CG, frame2CG] {
            let jpeg = try XCTUnwrap(FrameImageEncoder.encodeJPEG(from: cg))
            let fp = FrameImageEncoder.computeFingerprint(from: cg)
            sampler.appendIfDistinct(EncodedFrame(jpegData: jpeg, fingerprint: fp))
        }
        let sampledFrames = sampler.finalizeJPEGFrames()
        XCTAssertEqual(sampledFrames.count, 2)

        let multiResult = try await client.solveMCQ(
            jpegFrames: sampledFrames,
            apiKey: apiKey,
            model: .geminiDecision1,
            autoFallback: true
        )
        print("Live Multi-Select MCQ -> answers=\(multiResult.answers) raw=\(multiResult.rawText) model=\(multiResult.modelUsed.rawValue)")
        XCTAssertEqual(multiResult.answers, [1, 3])

        // 3. Non-MCQ image should cleanly fail so the app triggers the long error blink
        let notesCG = try renderTextImage("""
        Shopping List
        - Milk
        - Bread
        - Coffee
        """)
        let notesJPEG = try XCTUnwrap(FrameImageEncoder.encodeJPEG(from: notesCG))
        do {
            _ = try await client.solveMCQ(
                jpegFrames: [notesJPEG],
                apiKey: apiKey,
                model: .geminiDecision1,
                autoFallback: false
            )
            XCTFail("Expected non-MCQ image to throw unparseableAnswer")
        } catch GeminiClientError.unparseableAnswer {
            // Expected
        }
    }
}
