import AppKit
import CoreGraphics
import CoreImage
import CoreMedia
import CoreVideo
import Foundation
import LedVisualCore
import ScreenCaptureKit

public enum ScreenCaptureError: Error, LocalizedError, Sendable {
    case permissionDenied
    case noDisplayAvailable
    case encodingFailed
    case noFramesCaptured

    public var errorDescription: String? {
        switch self {
        case .permissionDenied:
            return "Screen Recording permission is required in System Settings."
        case .noDisplayAvailable:
            return "No active display was found to capture."
        case .encodingFailed:
            return "Failed to encode captured screen frame."
        case .noFramesCaptured:
            return "No screen frames were captured during recording."
        }
    }
}

/// Captures either a single screenshot or sampled keyframes while the hotkey is held down,
/// using ScreenCaptureKit without showing any on-screen indicators.
public final class ScreenCaptureService: @unchecked Sendable {
    private let stateLock = NSLock()
    private var activeStream: SCStream?
    private var streamOutput: StreamOutputHandler?
    private var recordingQueue: DispatchQueue?
    private var maxDurationWorkItem: DispatchWorkItem?

    public init() {}

    public static func hasScreenRecordingPermission() -> Bool {
        CGPreflightScreenCaptureAccess()
    }

    @discardableResult
    public static func requestScreenRecordingPermission() -> Bool {
        CGRequestScreenCaptureAccess()
    }

    /// Captures a single screenshot of the display currently under the mouse cursor.
    public func captureScreenshot() async throws -> [Data] {
        guard Self.hasScreenRecordingPermission() else {
            throw ScreenCaptureError.permissionDenied
        }

        let display = try await targetDisplay()
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let config = SCStreamConfiguration()
        let dims = scaledCaptureSize(for: display, maxLongEdge: 1600)
        config.width = dims.width
        config.height = dims.height
        config.showsCursor = false
        config.capturesAudio = false

        let cgImage = try await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: config
        )

        guard let jpeg = FrameImageEncoder.encodeJPEG(from: cgImage, maxLongEdge: 1600, quality: 0.78) else {
            throw ScreenCaptureError.encodingFailed
        }
        return [jpeg]
    }

    /// Begins recording keyframes of the display under the cursor while the hotkey is held.
    public func startRecording(
        sampleIntervalMs: Int,
        maxFrames: Int,
        maxDurationSeconds: Int,
        onFrameCountChanged: (@Sendable (Int) -> Void)? = nil
    ) async throws {
        guard Self.hasScreenRecordingPermission() else {
            throw ScreenCaptureError.permissionDenied
        }

        // Clean up any leftover stream first
        _ = await stopRecordingIfActive()

        let display = try await targetDisplay()
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let config = SCStreamConfiguration()
        let dims = scaledCaptureSize(for: display, maxLongEdge: 1600)
        config.width = dims.width
        config.height = dims.height
        config.showsCursor = false
        config.capturesAudio = false
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 3
        // Capture at ~4 fps so fast scrolls are caught; StreamOutputHandler samples & deduplicates
        config.minimumFrameInterval = CMTime(value: 1, timescale: 4)

        let queue = DispatchQueue(label: "com.led-visual.screen-recording", qos: .userInitiated)
        let output = StreamOutputHandler(
            sampleIntervalSec: max(0.2, Double(sampleIntervalMs) / 1000.0),
            maxFrames: maxFrames,
            onFrameCountChanged: onFrameCountChanged
        )

        // Capture an immediate initial frame so even brief holds always have the start state
        if let initialImage = try? await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: config
        ) {
            output.ingest(cgImage: initialImage, force: true)
        }

        let stream = SCStream(filter: filter, configuration: config, delegate: nil)
        try stream.addStreamOutput(output, type: .screen, sampleHandlerQueue: queue)
        try await stream.startCapture()

        let workItem = DispatchWorkItem { [weak self] in
            guard let service = self else { return }
            Task {
                await service.pauseStreamForMaxDuration()
            }
        }
        DispatchQueue.global(qos: .utility).asyncAfter(
            deadline: .now() + .seconds(max(5, maxDurationSeconds)),
            execute: workItem
        )

        stateLock.withLock {
            self.activeStream = stream
            self.streamOutput = output
            self.recordingQueue = queue
            self.maxDurationWorkItem = workItem
        }
    }

    /// Stops recording and returns deduplicated chronological JPEG frames.
    public func stopRecording() async throws -> [Data] {
        let frames = await stopRecordingIfActive()
        guard !frames.isEmpty else {
            throw ScreenCaptureError.noFramesCaptured
        }
        return frames
    }

    public func cancelRecording() async {
        _ = await stopRecordingIfActive()
    }

    // MARK: - Internals

    private func pauseStreamForMaxDuration() async {
        let stream: SCStream? = stateLock.withLock { activeStream }
        if let stream {
            try? await stream.stopCapture()
        }
    }

    private func stopRecordingIfActive() async -> [Data] {
        let (stream, output, workItem): (SCStream?, StreamOutputHandler?, DispatchWorkItem?) = stateLock.withLock {
            let s = activeStream
            let o = streamOutput
            let w = maxDurationWorkItem
            activeStream = nil
            streamOutput = nil
            recordingQueue = nil
            maxDurationWorkItem = nil
            return (s, o, w)
        }

        workItem?.cancel()

        if let stream {
            try? await stream.stopCapture()
        }

        return output?.finalizeFrames() ?? []
    }

    private func targetDisplay() async throws -> SCDisplay {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard !content.displays.isEmpty else {
            throw ScreenCaptureError.noDisplayAvailable
        }

        let cursorDisplayID = await MainActor.run { () -> CGDirectDisplayID? in
            let mouseLoc = NSEvent.mouseLocation
            for screen in NSScreen.screens where screen.frame.contains(mouseLoc) {
                return screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
            }
            return NSScreen.main?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
        }

        if let targetID = cursorDisplayID,
           let matched = content.displays.first(where: { $0.displayID == targetID }) {
            return matched
        }
        return content.displays[0]
    }

    private func scaledCaptureSize(for display: SCDisplay, maxLongEdge: Int) -> (width: Int, height: Int) {
        let w = max(1, display.width)
        let h = max(1, display.height)
        let longest = max(w, h)
        guard longest > maxLongEdge else {
            return (w, h)
        }
        let scale = Double(maxLongEdge) / Double(longest)
        let scaledW = max(2, Int((Double(w) * scale).rounded()) & ~1)
        let scaledH = max(2, Int((Double(h) * scale).rounded()) & ~1)
        return (scaledW, scaledH)
    }
}

private final class StreamOutputHandler: NSObject, SCStreamOutput, @unchecked Sendable {
    private let lock = NSLock()
    private let ciContext = CIContext(options: [.useSoftwareRenderer: false])
    private let sampleIntervalSec: Double
    private let onFrameCountChanged: (@Sendable (Int) -> Void)?
    private var sampler: FrameSampler
    private var lastSampleTimestamp: TimeInterval = 0

    init(
        sampleIntervalSec: Double,
        maxFrames: Int,
        onFrameCountChanged: (@Sendable (Int) -> Void)?
    ) {
        self.sampleIntervalSec = sampleIntervalSec
        self.onFrameCountChanged = onFrameCountChanged
        self.sampler = FrameSampler(maxFrames: maxFrames, diffThreshold: 3.5)
    }

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of type: SCStreamOutputType
    ) {
        guard type == .screen,
              sampleBuffer.isValid,
              let imageBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else {
            return
        }

        let now = ProcessInfo.processInfo.systemUptime
        let shouldSample: Bool = lock.withLock {
            if lastSampleTimestamp == 0 || (now - lastSampleTimestamp) >= sampleIntervalSec {
                lastSampleTimestamp = now
                return true
            }
            return false
        }
        guard shouldSample else { return }

        let ciImage = CIImage(cvPixelBuffer: imageBuffer)
        let rect = CGRect(
            x: 0,
            y: 0,
            width: CVPixelBufferGetWidth(imageBuffer),
            height: CVPixelBufferGetHeight(imageBuffer)
        )
        guard let cgImage = ciContext.createCGImage(ciImage, from: rect) else { return }
        ingest(cgImage: cgImage, force: false)
    }

    func ingest(cgImage: CGImage, force: Bool) {
        guard let jpeg = FrameImageEncoder.encodeJPEG(from: cgImage, maxLongEdge: 1600, quality: 0.76) else {
            return
        }
        let fingerprint = FrameImageEncoder.computeFingerprint(from: cgImage)
        let frame = EncodedFrame(jpegData: jpeg, fingerprint: fingerprint)

        let newCount: Int? = lock.withLock {
            if force {
                lastSampleTimestamp = ProcessInfo.processInfo.systemUptime
            }
            let added = sampler.appendIfDistinct(frame)
            return added ? sampler.count : nil
        }

        if let newCount {
            onFrameCountChanged?(newCount)
        }
    }

    func finalizeFrames() -> [Data] {
        lock.withLock {
            sampler.finalizeJPEGFrames()
        }
    }
}
