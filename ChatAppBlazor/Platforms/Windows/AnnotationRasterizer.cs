#if WINDOWS
using System.Buffers;
using Shared.Models;

namespace ChatAppBlazor.Platforms.Windows;

/// <summary>
/// A 32-bit premultiplied BGRA pixel buffer. Layout matches a top-down DIB section,
/// so a pixel stored as 0xAARRGGBB lands in memory as B, G, R, A.
/// </summary>
internal sealed class PixelSurface
{
    public PixelSurface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new int[Width * Height];
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public void Clear() => Array.Clear(Pixels);

    /// <summary>Copies a row range from another surface of identical size.</summary>
    public void CopyRowsFrom(PixelSurface source, int top, int bottom)
    {
        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException("Surface size mismatch", nameof(source));
        }

        top = Math.Clamp(top, 0, Height);
        bottom = Math.Clamp(bottom, 0, Height);
        if (bottom <= top)
        {
            return;
        }

        var offset = top * Width;
        Array.Copy(source.Pixels, offset, Pixels, offset, (bottom - top) * Width);
    }
}

/// <summary>Integer pixel rectangle, right/bottom exclusive.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public static PixelRect Empty { get; } = new(0, 0, 0, 0);

    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;

        return new PixelRect(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : new PixelRect(left, top, right, bottom);
    }
}

/// <summary>
/// One annotation stroke in normalized (0..1) coordinates, kept until the owner
/// undoes or clears it.
/// </summary>
internal sealed class OverlayStroke
{
    public required Guid Owner { get; init; }
    public required string StrokeId { get; init; }
    public ScreenAnnotationTool Tool { get; set; }
    public uint Color { get; set; }
    public float Width { get; set; }
    public List<float> Points { get; } = [];
    public bool Completed { get; set; }

    /// <summary>
    /// Environment.TickCount64 when the stroke finished, which starts its fade
    /// clock. In-progress strokes never fade.
    /// </summary>
    public long CompletedAtTicks { get; set; }

    /// <summary>
    /// Opacity for this stroke given a fade window, or 0 once it has expired.
    /// <paramref name="fadeAfterMs"/> of 0 or less means the stroke is pinned.
    /// </summary>
    public float GetOpacity(long nowTicks, int fadeAfterMs, int fadeDurationMs)
    {
        if (fadeAfterMs <= 0 || !Completed || CompletedAtTicks == 0)
        {
            return 1f;
        }

        var elapsed = nowTicks - CompletedAtTicks - fadeAfterMs;
        if (elapsed <= 0) return 1f;
        if (elapsed >= fadeDurationMs) return 0f;

        return 1f - ((float)elapsed / fadeDurationMs);
    }

    public void AppendPoints(float[]? values)
    {
        if (values == null)
        {
            return;
        }

        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            var x = values[i];
            var y = values[i + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                continue;
            }

            Points.Add(Math.Clamp(x, -0.5f, 1.5f));
            Points.Add(Math.Clamp(y, -0.5f, 1.5f));
        }
    }
}

/// <summary>
/// Software rasterizer for annotation strokes.
///
/// Each stroke is first rendered into an 8-bit coverage mask sized to its bounding
/// box and then composited in one pass. Building the mask with max-coverage instead
/// of blending segment by segment keeps translucent ink (the highlighter) from
/// darkening wherever a stroke overlaps itself.
/// </summary>
internal static class AnnotationRasterizer
{
    private const float MinStrokePixels = 1.5f;
    private const float MaxStrokePixels = 512f;

    /// <summary>
    /// Pixel bounding box a stroke touches on a surface of the given size.
    /// </summary>
    public static PixelRect GetBounds(OverlayStroke stroke, int surfaceWidth, int surfaceHeight)
    {
        var points = stroke.Points;
        if (points.Count < 2)
        {
            return PixelRect.Empty;
        }

        var radius = GetStrokeRadius(stroke, surfaceHeight);
        var pad = radius + 2f;

        // Arrow heads extend past the last point.
        if (stroke.Tool == ScreenAnnotationTool.Arrow)
        {
            pad += GetArrowHeadLength(stroke, surfaceWidth, surfaceHeight);
        }

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        for (var i = 0; i + 1 < points.Count; i += 2)
        {
            var x = points[i] * surfaceWidth;
            var y = points[i + 1] * surfaceHeight;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var rect = new PixelRect(
            (int)MathF.Floor(minX - pad),
            (int)MathF.Floor(minY - pad),
            (int)MathF.Ceiling(maxX + pad) + 1,
            (int)MathF.Ceiling(maxY + pad) + 1);

        return rect.Intersect(new PixelRect(0, 0, surfaceWidth, surfaceHeight));
    }

    /// <summary>
    /// Composites a stroke onto <paramref name="surface"/> and returns the region
    /// that was touched.
    /// </summary>
    public static PixelRect Draw(PixelSurface surface, OverlayStroke stroke, float opacity = 1f)
    {
        if (opacity <= 0f)
        {
            return PixelRect.Empty;
        }

        var bounds = GetBounds(stroke, surface.Width, surface.Height);
        if (bounds.IsEmpty)
        {
            return PixelRect.Empty;
        }

        var maskWidth = bounds.Right - bounds.Left;
        var maskHeight = bounds.Bottom - bounds.Top;
        var mask = ArrayPool<byte>.Shared.Rent(maskWidth * maskHeight);

        try
        {
            Array.Clear(mask, 0, maskWidth * maskHeight);
            BuildMask(mask, bounds, stroke, surface.Width, surface.Height);
            Composite(surface, mask, bounds, ScaleAlpha(stroke.Color, opacity));
            return bounds;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask);
        }
    }

    /// <summary>
    /// Scales a stroke's alpha channel for fading, leaving RGB untouched so the
    /// coverage mask still composites correctly.
    /// </summary>
    private static uint ScaleAlpha(uint color, float opacity)
    {
        if (opacity >= 1f)
        {
            return color;
        }

        var alpha = (uint)Math.Clamp((int)(((color >> 24) & 0xFF) * opacity), 0, 255);
        return (alpha << 24) | (color & 0x00FFFFFF);
    }

    private static float GetStrokeRadius(OverlayStroke stroke, int surfaceHeight)
    {
        var pixels = Math.Clamp(stroke.Width * surfaceHeight, MinStrokePixels, MaxStrokePixels);
        return pixels * 0.5f;
    }

    private static float GetArrowHeadLength(OverlayStroke stroke, int surfaceWidth, int surfaceHeight)
    {
        var points = stroke.Points;
        if (points.Count < 4)
        {
            return 0f;
        }

        var x0 = points[0] * surfaceWidth;
        var y0 = points[1] * surfaceHeight;
        var x1 = points[^2] * surfaceWidth;
        var y1 = points[^1] * surfaceHeight;
        var length = MathF.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        var radius = GetStrokeRadius(stroke, surfaceHeight);

        return Math.Clamp(length * 0.28f, radius * 3.5f, radius * 12f);
    }

    private static void BuildMask(
        byte[] mask,
        PixelRect bounds,
        OverlayStroke stroke,
        int surfaceWidth,
        int surfaceHeight)
    {
        var radius = GetStrokeRadius(stroke, surfaceHeight);
        var points = stroke.Points;

        switch (stroke.Tool)
        {
            case ScreenAnnotationTool.Rectangle:
            case ScreenAnnotationTool.Ellipse:
            {
                if (points.Count < 4)
                {
                    return;
                }

                var x0 = points[0] * surfaceWidth;
                var y0 = points[1] * surfaceHeight;
                var x1 = points[^2] * surfaceWidth;
                var y1 = points[^1] * surfaceHeight;

                if (stroke.Tool == ScreenAnnotationTool.Rectangle)
                {
                    StampRectangleOutline(mask, bounds, x0, y0, x1, y1, radius);
                }
                else
                {
                    StampEllipseOutline(mask, bounds, x0, y0, x1, y1, radius);
                }

                return;
            }

            case ScreenAnnotationTool.Arrow:
            {
                if (points.Count < 4)
                {
                    StampFreehand(mask, bounds, points, surfaceWidth, surfaceHeight, radius);
                    return;
                }

                var x0 = points[0] * surfaceWidth;
                var y0 = points[1] * surfaceHeight;
                var x1 = points[^2] * surfaceWidth;
                var y1 = points[^1] * surfaceHeight;

                StampSegment(mask, bounds, x0, y0, x1, y1, radius);
                StampArrowHead(mask, bounds, x0, y0, x1, y1, radius,
                    GetArrowHeadLength(stroke, surfaceWidth, surfaceHeight));
                return;
            }

            default:
                StampFreehand(mask, bounds, points, surfaceWidth, surfaceHeight, radius);
                return;
        }
    }

    private static void StampFreehand(
        byte[] mask,
        PixelRect bounds,
        List<float> points,
        int surfaceWidth,
        int surfaceHeight,
        float radius)
    {
        if (points.Count < 2)
        {
            return;
        }

        if (points.Count == 2)
        {
            var px = points[0] * surfaceWidth;
            var py = points[1] * surfaceHeight;
            StampSegment(mask, bounds, px, py, px, py, radius);
            return;
        }

        for (var i = 0; i + 3 < points.Count; i += 2)
        {
            StampSegment(
                mask,
                bounds,
                points[i] * surfaceWidth,
                points[i + 1] * surfaceHeight,
                points[i + 2] * surfaceWidth,
                points[i + 3] * surfaceHeight,
                radius);
        }
    }

    private static void StampArrowHead(
        byte[] mask,
        PixelRect bounds,
        float x0,
        float y0,
        float x1,
        float y1,
        float radius,
        float headLength)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.001f || headLength <= 0f)
        {
            return;
        }

        // Unit vector pointing back along the shaft.
        var ux = -dx / length;
        var uy = -dy / length;

        const float headAngle = 0.5f; // ~28.6 degrees
        var cos = MathF.Cos(headAngle);
        var sin = MathF.Sin(headAngle);

        var leftX = x1 + (((ux * cos) - (uy * sin)) * headLength);
        var leftY = y1 + (((ux * sin) + (uy * cos)) * headLength);
        var rightX = x1 + (((ux * cos) + (uy * sin)) * headLength);
        var rightY = y1 + (((-ux * sin) + (uy * cos)) * headLength);

        StampSegment(mask, bounds, x1, y1, leftX, leftY, radius);
        StampSegment(mask, bounds, x1, y1, rightX, rightY, radius);
    }

    /// <summary>
    /// Stamps a round-capped segment using the distance from each pixel centre to
    /// the segment, which gives antialiased edges and seamless joins for free.
    /// </summary>
    private static void StampSegment(
        byte[] mask,
        PixelRect bounds,
        float ax,
        float ay,
        float bx,
        float by,
        float radius)
    {
        var pad = radius + 1.5f;
        var area = new PixelRect(
            (int)MathF.Floor(Math.Min(ax, bx) - pad),
            (int)MathF.Floor(Math.Min(ay, by) - pad),
            (int)MathF.Ceiling(Math.Max(ax, bx) + pad) + 1,
            (int)MathF.Ceiling(Math.Max(ay, by) + pad) + 1)
            .Intersect(bounds);

        if (area.IsEmpty)
        {
            return;
        }

        var maskWidth = bounds.Right - bounds.Left;
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = (dx * dx) + (dy * dy);

        for (var y = area.Top; y < area.Bottom; y++)
        {
            var rowOffset = ((y - bounds.Top) * maskWidth) - bounds.Left;
            var py = y + 0.5f;

            for (var x = area.Left; x < area.Right; x++)
            {
                var px = x + 0.5f;

                float distance;
                if (lengthSquared <= 0.000001f)
                {
                    var ddx = px - ax;
                    var ddy = py - ay;
                    distance = MathF.Sqrt((ddx * ddx) + (ddy * ddy));
                }
                else
                {
                    var t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0f, 1f);
                    var cx = ax + (t * dx);
                    var cy = ay + (t * dy);
                    var ddx = px - cx;
                    var ddy = py - cy;
                    distance = MathF.Sqrt((ddx * ddx) + (ddy * ddy));
                }

                WriteCoverage(mask, rowOffset + x, radius - distance);
            }
        }
    }

    private static void StampRectangleOutline(
        byte[] mask,
        PixelRect bounds,
        float x0,
        float y0,
        float x1,
        float y1,
        float radius)
    {
        var left = Math.Min(x0, x1);
        var right = Math.Max(x0, x1);
        var top = Math.Min(y0, y1);
        var bottom = Math.Max(y0, y1);
        var halfWidth = (right - left) * 0.5f;
        var halfHeight = (bottom - top) * 0.5f;
        var centerX = left + halfWidth;
        var centerY = top + halfHeight;

        var maskWidth = bounds.Right - bounds.Left;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            var rowOffset = ((y - bounds.Top) * maskWidth) - bounds.Left;
            var py = MathF.Abs((y + 0.5f) - centerY) - halfHeight;

            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var px = MathF.Abs((x + 0.5f) - centerX) - halfWidth;

                // Signed distance to the rectangle border.
                var outsideX = MathF.Max(px, 0f);
                var outsideY = MathF.Max(py, 0f);
                var outside = MathF.Sqrt((outsideX * outsideX) + (outsideY * outsideY));
                var inside = MathF.Min(MathF.Max(px, py), 0f);
                var distance = MathF.Abs(outside + inside);

                WriteCoverage(mask, rowOffset + x, radius - distance);
            }
        }
    }

    private static void StampEllipseOutline(
        byte[] mask,
        PixelRect bounds,
        float x0,
        float y0,
        float x1,
        float y1,
        float radius)
    {
        var radiusX = MathF.Max(MathF.Abs(x1 - x0) * 0.5f, 0.5f);
        var radiusY = MathF.Max(MathF.Abs(y1 - y0) * 0.5f, 0.5f);
        var centerX = (x0 + x1) * 0.5f;
        var centerY = (y0 + y1) * 0.5f;

        var maskWidth = bounds.Right - bounds.Left;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            var rowOffset = ((y - bounds.Top) * maskWidth) - bounds.Left;
            var ny = ((y + 0.5f) - centerY) / radiusY;

            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var nx = ((x + 0.5f) - centerX) / radiusX;

                // First-order distance estimate to the implicit ellipse f = 0.
                var value = (nx * nx) + (ny * ny) - 1f;
                var gradient = 2f * MathF.Sqrt(((nx * nx) / (radiusX * radiusX)) + ((ny * ny) / (radiusY * radiusY)));
                var distance = gradient <= 0.000001f
                    ? MathF.Min(radiusX, radiusY)
                    : MathF.Abs(value) / gradient;

                WriteCoverage(mask, rowOffset + x, radius - distance);
            }
        }
    }

    private static void WriteCoverage(byte[] mask, int index, float signedDistance)
    {
        // Half a pixel of feather keeps edges smooth without blurring thin ink.
        var coverage = signedDistance + 0.5f;
        if (coverage <= 0f)
        {
            return;
        }

        if (coverage > 1f)
        {
            coverage = 1f;
        }

        var value = (byte)(coverage * 255f);
        if (value > mask[index])
        {
            mask[index] = value;
        }
    }

    /// <summary>
    /// Source-over composite of a coverage mask tinted with an 0xAARRGGBB color.
    /// </summary>
    private static void Composite(PixelSurface surface, byte[] mask, PixelRect bounds, uint color)
    {
        var strokeAlpha = (int)((color >> 24) & 0xFF);
        if (strokeAlpha == 0)
        {
            return;
        }

        var srcR = (int)((color >> 16) & 0xFF);
        var srcG = (int)((color >> 8) & 0xFF);
        var srcB = (int)(color & 0xFF);

        var pixels = surface.Pixels;
        var surfaceWidth = surface.Width;
        var maskWidth = bounds.Right - bounds.Left;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            var maskRow = (y - bounds.Top) * maskWidth;
            var pixelRow = y * surfaceWidth;

            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var coverage = mask[maskRow + (x - bounds.Left)];
                if (coverage == 0)
                {
                    continue;
                }

                // Effective source alpha for this pixel.
                var alpha = (strokeAlpha * coverage) / 255;
                if (alpha == 0)
                {
                    continue;
                }

                var index = pixelRow + x;
                var dst = pixels[index];
                var dstA = (dst >> 24) & 0xFF;
                var dstR = (dst >> 16) & 0xFF;
                var dstG = (dst >> 8) & 0xFF;
                var dstB = dst & 0xFF;

                var inverse = 255 - alpha;

                // Premultiplied source-over: dst = src*a + dst*(1-a).
                var outA = alpha + ((dstA * inverse) / 255);
                var outR = ((srcR * alpha) / 255) + ((dstR * inverse) / 255);
                var outG = ((srcG * alpha) / 255) + ((dstG * inverse) / 255);
                var outB = ((srcB * alpha) / 255) + ((dstB * inverse) / 255);

                pixels[index] =
                    (Math.Min(outA, 255) << 24) |
                    (Math.Min(outR, 255) << 16) |
                    (Math.Min(outG, 255) << 8) |
                    Math.Min(outB, 255);
            }
        }
    }
}
#endif
