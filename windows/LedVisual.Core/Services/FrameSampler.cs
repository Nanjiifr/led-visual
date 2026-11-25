using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LedVisual.Core.Services;

public sealed record EncodedFrame(byte[] JpegData, byte[] Fingerprint);

public static class FrameImageEncoder
{
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Scales a Bitmap down proportionally if either dimension exceeds <paramref name="maxLongEdge"/>,
    /// then encodes it as JPEG bytes for fast upload with crisp text readability.
    /// </summary>
    public static byte[] EncodeJpeg(Bitmap source, int maxLongEdge = 1600, long quality = 78)
    {
        ArgumentNullException.ThrowIfNull(source);

        Bitmap? resized = null;
        try
        {
            var target = source;
            var longest = Math.Max(source.Width, source.Height);
            if (maxLongEdge > 0 && longest > maxLongEdge)
            {
                var scale = (double)maxLongEdge / longest;
                var newWidth = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
                var newHeight = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

                resized = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(resized))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, newWidth, newHeight));
                }

                target = resized;
            }

            using var stream = new MemoryStream();
            if (JpegCodec is not null)
            {
                var clampedQuality = Math.Clamp(quality, 1L, 100L);
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, clampedQuality);
                target.Save(stream, JpegCodec, encoderParams);
            }
            else
            {
                target.Save(stream, ImageFormat.Jpeg);
            }

            return stream.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>
    /// Computes a lightweight 16x16 grayscale fingerprint (256 bytes) for visual change detection.
    /// </summary>
    public static byte[] ComputeFingerprint(Bitmap source, int gridSize = 16)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = Math.Max(1, gridSize);
        var pixels = new byte[size * size];

        using var small = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.Low;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        var rect = new Rectangle(0, 0, size, size);
        var bitmapData = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(bitmapData.Stride);
            var buffer = new byte[stride * size];
            Marshal.Copy(bitmapData.Scan0, buffer, 0, buffer.Length);

            for (var y = 0; y < size; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < size; x++)
                {
                    var offset = rowOffset + (x * 3);
                    // 24bppRgb in GDI+ is stored as BGR
                    var b = buffer[offset];
                    var g = buffer[offset + 1];
                    var r = buffer[offset + 2];
                    pixels[(y * size) + x] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                }
            }
        }
        finally
        {
            small.UnlockBits(bitmapData);
        }

        return pixels;
    }

    /// <summary>
    /// Mean absolute difference (0.0 ... 255.0) between two equal-sized fingerprints.
    /// </summary>
    public static double MeanAbsoluteDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.IsEmpty)
        {
            return 255.0;
        }

        long total = 0;
        for (var i = 0; i < a.Length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return (double)total / a.Length;
    }
}

/// <summary>
/// Deduplicates and caps chronological screen-recording frames so scrolling through a long MCQ
/// sends only distinct, readable keyframes within the configured frame budget.
/// </summary>
public sealed class FrameSampler
{
    private readonly List<EncodedFrame> _frames = [];

    public int MaxFrames { get; }
    public double DiffThreshold { get; }
    public int Count => _frames.Count;

    public FrameSampler(int maxFrames = 24, double diffThreshold = 3.5)
    {
        MaxFrames = Math.Max(1, maxFrames);
        DiffThreshold = Math.Max(0.0, diffThreshold);
    }

    /// <summary>
    /// Appends a frame if it is the first frame or differs visually from the previous kept frame.
    /// </summary>
    public bool AppendIfDistinct(EncodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_frames.Count > 0)
        {
            var last = _frames[^1];
            var diff = FrameImageEncoder.MeanAbsoluteDifference(last.Fingerprint, frame.Fingerprint);
            if (diff < DiffThreshold)
            {
                return false;
            }
        }

        _frames.Add(frame);
        return true;
    }

    /// <summary>
    /// Returns up to <see cref="MaxFrames"/> JPEG payloads in chronological order, always keeping the first and last frames.
    /// </summary>
    public IReadOnlyList<byte[]> FinalizeJpegFrames()
    {
        if (_frames.Count == 0)
        {
            return [];
        }

        if (_frames.Count <= MaxFrames)
        {
            return _frames.Select(f => f.JpegData).ToArray();
        }

        if (MaxFrames == 1)
        {
            return [_frames[0].JpegData];
        }

        var selected = new List<byte[]>(MaxFrames);
        var lastIndex = _frames.Count - 1;
        for (var i = 0; i < MaxFrames; i++)
        {
            var position = (double)i * lastIndex / (MaxFrames - 1);
            var idx = Math.Min(lastIndex, (int)Math.Round(position, MidpointRounding.AwayFromZero));
            selected.Add(_frames[idx].JpegData);
        }

        return selected;
    }
}
