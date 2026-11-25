import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

public struct EncodedFrame: Equatable, Sendable {
    public let jpegData: Data
    public let fingerprint: [UInt8]

    public init(jpegData: Data, fingerprint: [UInt8]) {
        self.jpegData = jpegData
        self.fingerprint = fingerprint
    }
}

public enum FrameImageEncoder {
    /// Scales a CGImage down proportionally if either dimension exceeds `maxLongEdge`,
    /// then encodes it as JPEG data for fast upload with crisp text readability.
    public static func encodeJPEG(
        from image: CGImage,
        maxLongEdge: Int = 1600,
        quality: Double = 0.78
    ) -> Data? {
        let targetImage = downscaleIfNeeded(image, maxLongEdge: maxLongEdge) ?? image
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data,
            UTType.jpeg.identifier as CFString,
            1,
            nil
        ) else {
            return nil
        }
        let options: [CFString: Any] = [
            kCGImageDestinationLossyCompressionQuality: quality
        ]
        CGImageDestinationAddImage(destination, targetImage, options as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            return nil
        }
        return data as Data
    }

    /// Computes a lightweight 16x16 grayscale fingerprint (256 bytes) for visual change detection.
    public static func computeFingerprint(from image: CGImage, gridSize: Int = 16) -> [UInt8] {
        var pixels = [UInt8](repeating: 0, count: gridSize * gridSize)
        let colorSpace = CGColorSpaceCreateDeviceGray()
        guard let context = CGContext(
            data: &pixels,
            width: gridSize,
            height: gridSize,
            bitsPerComponent: 8,
            bytesPerRow: gridSize,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            return pixels
        }
        context.interpolationQuality = .low
        context.draw(image, in: CGRect(x: 0, y: 0, width: gridSize, height: gridSize))
        return pixels
    }

    /// Mean absolute difference (0.0 ... 255.0) between two equal-sized fingerprints.
    public static func meanAbsoluteDifference(_ a: [UInt8], _ b: [UInt8]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return 255.0 }
        var total = 0
        for i in 0..<a.count {
            total += abs(Int(a[i]) - Int(b[i]))
        }
        return Double(total) / Double(a.count)
    }

    private static func downscaleIfNeeded(_ image: CGImage, maxLongEdge: Int) -> CGImage? {
        let width = image.width
        let height = image.height
        let longest = max(width, height)
        guard longest > maxLongEdge, maxLongEdge > 0 else { return nil }

        let scale = Double(maxLongEdge) / Double(longest)
        let newWidth = max(1, Int((Double(width) * scale).rounded()))
        let newHeight = max(1, Int((Double(height) * scale).rounded()))

        let colorSpace = image.colorSpace ?? CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: nil,
            width: newWidth,
            height: newHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return nil
        }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: newWidth, height: newHeight))
        return context.makeImage()
    }
}

/// Deduplicates and caps chronological screen-recording frames so scrolling through a long MCQ
/// sends only distinct, readable keyframes within the configured frame budget.
public struct FrameSampler: Sendable {
    public let maxFrames: Int
    public let diffThreshold: Double
    private var frames: [EncodedFrame] = []

    public init(maxFrames: Int = 24, diffThreshold: Double = 3.5) {
        self.maxFrames = max(1, maxFrames)
        self.diffThreshold = max(0.0, diffThreshold)
    }

    public var count: Int { frames.count }

    /// Appends a frame if it is the first frame or differs visually from the previous kept frame.
    @discardableResult
    public mutating func appendIfDistinct(_ frame: EncodedFrame) -> Bool {
        if let last = frames.last {
            let diff = FrameImageEncoder.meanAbsoluteDifference(last.fingerprint, frame.fingerprint)
            if diff < diffThreshold {
                return false
            }
        }
        frames.append(frame)
        return true
    }

    /// Returns up to `maxFrames` JPEG payloads in chronological order, always keeping the first and last frames.
    public func finalizeJPEGFrames() -> [Data] {
        guard !frames.isEmpty else { return [] }
        if frames.count <= maxFrames {
            return frames.map(\.jpegData)
        }
        if maxFrames == 1 {
            return [frames[0].jpegData]
        }

        var selected: [Data] = []
        selected.reserveCapacity(maxFrames)
        let lastIndex = frames.count - 1
        for i in 0..<maxFrames {
            let position = Double(i) * Double(lastIndex) / Double(maxFrames - 1)
            let idx = min(lastIndex, Int(position.rounded()))
            selected.append(frames[idx].jpegData)
        }
        return selected
    }
}
