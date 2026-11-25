import CoreGraphics
import XCTest
@testable import LedVisualCore

final class FrameSamplerTests: XCTestCase {
    func testDeduplicatesIdenticalFramesAndKeepsDistinctOnes() {
        var sampler = FrameSampler(maxFrames: 10, diffThreshold: 3.5)

        let f1 = EncodedFrame(jpegData: Data([1]), fingerprint: [UInt8](repeating: 10, count: 256))
        let f2 = EncodedFrame(jpegData: Data([2]), fingerprint: [UInt8](repeating: 11, count: 256)) // diff 1.0 -> skip
        let f3 = EncodedFrame(jpegData: Data([3]), fingerprint: [UInt8](repeating: 80, count: 256)) // diff 70 -> keep

        XCTAssertTrue(sampler.appendIfDistinct(f1))
        XCTAssertFalse(sampler.appendIfDistinct(f2))
        XCTAssertTrue(sampler.appendIfDistinct(f3))

        XCTAssertEqual(sampler.finalizeJPEGFrames(), [Data([1]), Data([3])])
    }

    func testSubsamplesWhilePreservingFirstAndLastFrames() {
        var sampler = FrameSampler(maxFrames: 4, diffThreshold: 1.0)
        for i in 0..<10 {
            let value = UInt8(i * 25)
            let frame = EncodedFrame(jpegData: Data([UInt8(i)]), fingerprint: [UInt8](repeating: value, count: 256))
            sampler.appendIfDistinct(frame)
        }

        let result = sampler.finalizeJPEGFrames()
        XCTAssertEqual(result.count, 4)
        XCTAssertEqual(result.first, Data([0]))
        XCTAssertEqual(result.last, Data([9]))
    }

    func testImageEncoderProducesValidJPEGAndDownscales() throws {
        let width = 2000
        let height = 1000
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let ctx = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        ctx.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = try XCTUnwrap(ctx.makeImage())

        let jpeg = try XCTUnwrap(FrameImageEncoder.encodeJPEG(from: image, maxLongEdge: 800, quality: 0.75))
        // JPEG SOI marker: 0xFF 0xD8
        XCTAssertGreaterThan(jpeg.count, 100)
        XCTAssertEqual(jpeg[0], 0xFF)
        XCTAssertEqual(jpeg[1], 0xD8)

        let fp = FrameImageEncoder.computeFingerprint(from: image)
        XCTAssertEqual(fp.count, 256)
        XCTAssertEqual(fp[0], 255)
    }
}
