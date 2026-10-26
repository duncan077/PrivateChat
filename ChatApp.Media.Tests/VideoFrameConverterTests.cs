using ChatApp.Core.Media;
using ChatApp.Media.Codec;

namespace ChatApp.Media.Tests;

/// <summary>
/// Exercises the renderer's colour conversion against real swscale.
/// <para>
/// The plane offsets and strides handed to swscale are the kind of thing that produces a
/// plausible-looking but wrong picture rather than an error, so the expected colours are
/// checked numerically instead of by eye.
/// </para>
/// </summary>
public class VideoFrameConverterTests
{
    // BT.601 limited range, which is what swscale assumes for yuv420p.
    private const byte BlackLuma = 16;
    private const byte WhiteLuma = 235;
    private const byte NeutralChroma = 128;

    [FFmpegFact]
    public void Black_and_white_convert_to_the_expected_levels()
    {
        using var converter = new SwsVideoFrameConverter();

        var black = ConvertI420(converter, MakeI420(32, 32, BlackLuma, NeutralChroma, NeutralChroma), 32, 32, 32, 32);
        AssertPixel(black, 0, blue: 0, green: 0, red: 0);

        var white = ConvertI420(converter, MakeI420(32, 32, WhiteLuma, NeutralChroma, NeutralChroma), 32, 32, 32, 32);
        AssertPixel(white, 0, blue: 255, green: 255, red: 255);
    }

    [FFmpegFact]
    public void Primaries_convert_to_the_expected_colours()
    {
        using var converter = new SwsVideoFrameConverter();

        // If the U and V planes were swapped the picture would still look like video —
        // just with red and blue exchanged — so both are pinned here.
        var red = ConvertI420(converter, MakeI420(32, 32, 81, 90, 240), 32, 32, 32, 32);
        AssertPixel(red, 0, blue: 0, green: 0, red: 255, tolerance: 8);

        var blue = ConvertI420(converter, MakeI420(32, 32, 41, 240, 110), 32, 32, 32, 32);
        AssertPixel(blue, 0, blue: 255, green: 0, red: 0, tolerance: 8);
    }

    [FFmpegFact]
    public void Alpha_is_opaque()
    {
        // The renderer declares AlphaFormat.Opaque, and a converter that left alpha at
        // zero would produce an invisible tile on any backend that honours it.
        using var converter = new SwsVideoFrameConverter();

        var pixels = ConvertI420(converter, MakeI420(16, 16, WhiteLuma, NeutralChroma, NeutralChroma), 16, 16, 16, 16);

        for (var i = 3; i < pixels.Length; i += 4)
            Assert.Equal(255, pixels[i]);
    }

    [FFmpegFact]
    public void Scales_down_to_the_requested_size()
    {
        using var converter = new SwsVideoFrameConverter();

        var pixels = ConvertI420(
            converter, MakeI420(64, 64, WhiteLuma, NeutralChroma, NeutralChroma), 64, 64, 16, 16);

        Assert.Equal(16 * 16 * 4, pixels.Length);

        // A solid source stays solid at any scale; anything else means the scaler read
        // outside the frame.
        for (var i = 0; i < pixels.Length; i += 4)
            AssertPixel(pixels, i / 4, blue: 255, green: 255, red: 255);
    }

    [FFmpegFact]
    public void Reuses_one_instance_across_changing_sizes()
    {
        // Resizing a window walks the scaler through a series of target sizes; the cached
        // context has to be rebuilt each time rather than silently reused.
        using var converter = new SwsVideoFrameConverter();
        var source = MakeI420(64, 64, WhiteLuma, NeutralChroma, NeutralChroma);

        foreach (var size in new[] { 64, 48, 32, 48 })
        {
            var pixels = ConvertI420(converter, source, 64, 64, size, size);

            Assert.Equal(size * size * 4, pixels.Length);
            AssertPixel(pixels, 0, blue: 255, green: 255, red: 255);
        }
    }

    [FFmpegFact]
    public void Bgra_input_passes_through_unchanged()
    {
        using var converter = new SwsVideoFrameConverter();

        var source = new byte[8 * 8 * 4];
        Random.Shared.NextBytes(source);

        for (var i = 3; i < source.Length; i += 4)
            source[i] = 255;

        Span<int> offsets = [0];
        Span<int> strides = [8 * 4];

        var frame = new VideoFrame
        {
            Width = 8,
            Height = 8,
            Format = VideoPixelFormat.Bgra32,
            Data = source,
            PlaneOffsets = offsets,
            PlaneStrides = strides,
            RtpTimestamp = 0
        };

        var destination = new byte[8 * 8 * 4];
        Assert.True(converter.Convert(frame, 8, 8, destination, 8 * 4));
        Assert.Equal(source, destination);
    }

    [FFmpegFact]
    public void Writes_into_a_padded_destination_stride()
    {
        // A locked bitmap's row stride is whatever the platform chose, and is often wider
        // than the pixels. Rows must land on that pitch, leaving the padding alone.
        using var converter = new SwsVideoFrameConverter();

        const int Width = 16;
        const int Height = 16;
        const int Stride = Width * 4 + 64;

        var destination = new byte[Stride * Height];
        Array.Fill(destination, (byte)0x7F);

        var source = MakeI420(Width, Height, WhiteLuma, NeutralChroma, NeutralChroma);
        var lumaSize = Width * Height;
        var chromaSize = Width / 2 * (Height / 2);

        Span<int> offsets = [0, lumaSize, lumaSize + chromaSize];
        Span<int> strides = [Width, Width / 2, Width / 2];

        var frame = new VideoFrame
        {
            Width = Width,
            Height = Height,
            Format = VideoPixelFormat.I420,
            Data = source,
            PlaneOffsets = offsets,
            PlaneStrides = strides,
            RtpTimestamp = 0
        };

        Assert.True(converter.Convert(frame, Width, Height, destination, Stride));

        for (var y = 0; y < Height; y++)
        {
            Assert.Equal(255, destination[y * Stride]);
            Assert.Equal(0x7F, destination[y * Stride + Width * 4]);
        }
    }

    [FFmpegFact]
    public void Rejects_a_destination_that_cannot_hold_the_result()
    {
        using var converter = new SwsVideoFrameConverter();

        var source = MakeI420(16, 16, WhiteLuma, NeutralChroma, NeutralChroma);
        var lumaSize = 16 * 16;
        var chromaSize = 8 * 8;

        Span<int> offsets = [0, lumaSize, lumaSize + chromaSize];
        Span<int> strides = [16, 8, 8];

        var frame = new VideoFrame
        {
            Width = 16,
            Height = 16,
            Format = VideoPixelFormat.I420,
            Data = source,
            PlaneOffsets = offsets,
            PlaneStrides = strides,
            RtpTimestamp = 0
        };

        // One row short, and a stride narrower than the pixels: both would corrupt memory
        // rather than merely look wrong.
        Assert.False(converter.Convert(frame, 16, 16, new byte[16 * 4 * 15], 16 * 4));
        Assert.False(converter.Convert(frame, 16, 16, new byte[16 * 4 * 16], 16 * 4 - 4));
    }

    private static byte[] MakeI420(int width, int height, byte luma, byte u, byte v)
    {
        var lumaSize = width * height;
        var chromaWidth = (width + 1) / 2;
        var chromaSize = chromaWidth * ((height + 1) / 2);

        var buffer = new byte[lumaSize + chromaSize * 2];

        buffer.AsSpan(0, lumaSize).Fill(luma);
        buffer.AsSpan(lumaSize, chromaSize).Fill(u);
        buffer.AsSpan(lumaSize + chromaSize, chromaSize).Fill(v);

        return buffer;
    }

    /// <summary>
    /// Converts and returns the pixels. Inline rather than a frame factory because
    /// <see cref="VideoFrame"/> is a ref struct and may not be returned from a method.
    /// </summary>
    private static byte[] ConvertI420(
        SwsVideoFrameConverter converter,
        byte[] source,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        var lumaSize = width * height;
        var chromaWidth = (width + 1) / 2;
        var chromaSize = chromaWidth * ((height + 1) / 2);

        Span<int> offsets = [0, lumaSize, lumaSize + chromaSize];
        Span<int> strides = [width, chromaWidth, chromaWidth];

        var frame = new VideoFrame
        {
            Width = width,
            Height = height,
            Format = VideoPixelFormat.I420,
            Data = source,
            PlaneOffsets = offsets,
            PlaneStrides = strides,
            RtpTimestamp = 0
        };

        var stride = targetWidth * 4;
        var destination = new byte[stride * targetHeight];

        Assert.True(
            converter.Convert(frame, targetWidth, targetHeight, destination, stride),
            $"conversion to {targetWidth}x{targetHeight} failed");

        return destination;
    }

    private static void AssertPixel(
        byte[] pixels, int pixelIndex, byte blue, byte green, byte red, int tolerance = 3)
    {
        var offset = pixelIndex * 4;

        AssertNear(blue, pixels[offset], tolerance, "blue", pixelIndex);
        AssertNear(green, pixels[offset + 1], tolerance, "green", pixelIndex);
        AssertNear(red, pixels[offset + 2], tolerance, "red", pixelIndex);
    }

    private static void AssertNear(byte expected, byte actual, int tolerance, string channel, int pixelIndex)
        => Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"pixel {pixelIndex} {channel}: expected {expected} ± {tolerance}, got {actual}");
}
