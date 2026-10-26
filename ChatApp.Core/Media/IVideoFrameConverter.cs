namespace ChatApp.Core.Media;

/// <summary>
/// Turns a decoded frame into BGRA at a chosen size, ready to upload into a renderer's
/// bitmap.
/// <para>
/// Scaling is part of the contract rather than the renderer's job: a 1080p feed drawn in
/// a 480 px tile would otherwise be converted at full resolution and then thrown away —
/// roughly ten times the pixel work per frame, per tile, on every frame.
/// </para>
/// <para>
/// One converter belongs to one renderer and is called from that source's decode thread.
/// Implementations cache scaler state, so changing size on an existing instance is cheap,
/// but sharing an instance between sources is not.
/// </para>
/// </summary>
public interface IVideoFrameConverter : IDisposable
{
    /// <summary>
    /// Converts <paramref name="frame"/> into <paramref name="destination"/> as BGRA.
    /// Returns false when the frame or the destination cannot be used, which the caller
    /// should treat as a dropped frame rather than an error.
    /// </summary>
    bool Convert(
        in VideoFrame frame,
        int targetWidth,
        int targetHeight,
        Span<byte> destination,
        int destinationStride);
}

/// <summary>
/// Supplies converters to the UI layer.
/// <para>
/// A factory rather than a shared instance because each renderer needs its own scaler
/// state. Registered by the head, since the implementation lives beside the codecs.
/// </para>
/// </summary>
public interface IVideoFrameConverterFactory
{
    IVideoFrameConverter Create();
}
