using System.Drawing;
using System.Drawing.Imaging;
using LedVisual.Core.Services;

namespace LedVisual.App.Controllers;

public sealed class ScreenCaptureException : Exception
{
    public ScreenCaptureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Captures either a single screenshot or sampled keyframes while the hotkey is held down,
/// targeting the monitor containing the mouse cursor without showing any on-screen indicators.
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly object _stateLock = new();
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingLoopTask;
    private FrameSampler? _activeSampler;

    /// <summary>
    /// Captures a single screenshot of the monitor currently containing the mouse cursor.
    /// </summary>
    public Task<IReadOnlyList<byte[]>> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<byte[]>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = GetTargetScreenBounds();
            using var bitmap = CaptureBounds(bounds);
            var jpeg = FrameImageEncoder.EncodeJpeg(bitmap, maxLongEdge: 1600, quality: 78);
            return [jpeg];
        }, cancellationToken);
    }

    /// <summary>
    /// Begins recording deduplicated keyframes of the monitor under the cursor while the hotkey is held.
    /// </summary>
    public async Task StartRecordingAsync(
        int sampleIntervalMs,
        int maxFrames,
        int maxDurationSeconds,
        Action<int>? onFrameCountChanged = null,
        CancellationToken cancellationToken = default)
    {
        await CancelRecordingAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var intervalMs = Math.Max(200, sampleIntervalMs);
        var maxSeconds = Math.Max(5, maxDurationSeconds);
        var bounds = GetTargetScreenBounds();
        var sampler = new FrameSampler(maxFrames: maxFrames, diffThreshold: 3.5);
        var cts = new CancellationTokenSource();

        // Capture an immediate initial frame so even brief holds always have the start state
        await Task.Run(() => CaptureAndIngestFrame(bounds, sampler, onFrameCountChanged), cancellationToken)
            .ConfigureAwait(false);

        var loopTask = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
            try
            {
                while (!cts.Token.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                    if (cts.Token.IsCancellationRequested || DateTime.UtcNow >= deadline)
                    {
                        break;
                    }

                    CaptureAndIngestFrame(bounds, sampler, onFrameCountChanged);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        lock (_stateLock)
        {
            _recordingCts = cts;
            _recordingLoopTask = loopTask;
            _activeSampler = sampler;
        }
    }

    /// <summary>
    /// Stops recording and returns deduplicated chronological JPEG frames.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> StopRecordingAsync()
    {
        var frames = await StopRecordingInternalAsync().ConfigureAwait(false);
        if (frames.Count == 0)
        {
            throw new ScreenCaptureException("No screen frames were captured during recording.");
        }

        return frames;
    }

    public async Task CancelRecordingAsync()
    {
        _ = await StopRecordingInternalAsync().ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<byte[]>> StopRecordingInternalAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;
        FrameSampler? sampler;

        lock (_stateLock)
        {
            cts = _recordingCts;
            loopTask = _recordingLoopTask;
            sampler = _activeSampler;
            _recordingCts = null;
            _recordingLoopTask = null;
            _activeSampler = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        cts?.Dispose();

        if (sampler is null)
        {
            return [];
        }

        lock (sampler)
        {
            return sampler.FinalizeJpegFrames();
        }
    }

    private static void CaptureAndIngestFrame(
        Rectangle bounds,
        FrameSampler sampler,
        Action<int>? onFrameCountChanged)
    {
        try
        {
            using var bitmap = CaptureBounds(bounds);
            var jpeg = FrameImageEncoder.EncodeJpeg(bitmap, maxLongEdge: 1600, quality: 76);
            var fingerprint = FrameImageEncoder.ComputeFingerprint(bitmap);
            var frame = new EncodedFrame(jpeg, fingerprint);

            int? newCount;
            lock (sampler)
            {
                newCount = sampler.AppendIfDistinct(frame) ? sampler.Count : null;
            }

            if (newCount.HasValue)
            {
                onFrameCountChanged?.Invoke(newCount.Value);
            }
        }
        catch
        {
        }
    }

    private static Rectangle GetTargetScreenBounds()
    {
        var screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen;
        if (screen is null || screen.Bounds.Width <= 0 || screen.Bounds.Height <= 0)
        {
            throw new ScreenCaptureException("No active display was found to capture.");
        }

        return screen.Bounds;
    }

    private static Bitmap CaptureBounds(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Exception ex)
        {
            bitmap.Dispose();
            throw new ScreenCaptureException("Failed to capture screen frame.", ex);
        }
    }
}
