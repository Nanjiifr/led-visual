using System.Drawing;
using System.Drawing.Imaging;
using LedVisual.Core.Services;
using Xunit;

namespace LedVisual.Core.Tests;

public sealed class FrameSamplerTests
{
    [Fact]
    public void DeduplicatesIdenticalFramesAndKeepsDistinctOnes()
    {
        var sampler = new FrameSampler(maxFrames: 10, diffThreshold: 3.5);

        var f1 = new EncodedFrame([1], Enumerable.Repeat((byte)10, 256).ToArray());
        var f2 = new EncodedFrame([2], Enumerable.Repeat((byte)11, 256).ToArray()); // diff 1.0 -> skip
        var f3 = new EncodedFrame([3], Enumerable.Repeat((byte)80, 256).ToArray()); // diff 70 -> keep

        Assert.True(sampler.AppendIfDistinct(f1));
        Assert.False(sampler.AppendIfDistinct(f2));
        Assert.True(sampler.AppendIfDistinct(f3));

        var result = sampler.FinalizeJpegFrames();
        Assert.Equal(2, result.Count);
        Assert.Equal(new byte[] { 1 }, result[0]);
        Assert.Equal(new byte[] { 3 }, result[1]);
    }

    [Fact]
    public void SubsamplesWhilePreservingFirstAndLastFrames()
    {
        var sampler = new FrameSampler(maxFrames: 4, diffThreshold: 1.0);
        for (var i = 0; i < 10; i++)
        {
            var value = (byte)(i * 25);
            var frame = new EncodedFrame(new[] { (byte)i }, Enumerable.Repeat(value, 256).ToArray());
            sampler.AppendIfDistinct(frame);
        }

        var result = sampler.FinalizeJpegFrames();
        Assert.Equal(4, result.Count);
        Assert.Equal(new byte[] { 0 }, result[0]);
        Assert.Equal(new byte[] { 9 }, result[^1]);
    }

    [Fact]
    public void ImageEncoderProducesValidJpegAndDownscales()
    {
        using var bitmap = new Bitmap(2000, 1000, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
        }

        var jpeg = FrameImageEncoder.EncodeJpeg(bitmap, maxLongEdge: 800, quality: 75);
        // JPEG SOI marker: 0xFF 0xD8
        Assert.True(jpeg.Length > 100);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        using var ms = new MemoryStream(jpeg);
        using var decoded = new Bitmap(ms);
        Assert.Equal(800, decoded.Width);
        Assert.Equal(400, decoded.Height);

        var fp = FrameImageEncoder.ComputeFingerprint(bitmap);
        Assert.Equal(256, fp.Length);
        Assert.Equal(255, fp[0]);
    }
}
