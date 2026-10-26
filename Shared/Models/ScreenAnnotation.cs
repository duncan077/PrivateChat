using MessagePack;

namespace Shared.Models;

/// <summary>
/// Tool used to render an annotation stroke.
/// </summary>
public enum ScreenAnnotationTool
{
    Pen = 0,
    Highlighter = 1,
    Arrow = 2,
    Rectangle = 3,
    Ellipse = 4
}

/// <summary>
/// What the sharer is actually capturing. Determines whether the native desktop
/// overlay is visible inside the published stream: a monitor capture includes the
/// topmost overlay window, a single-window capture does not.
/// </summary>
public enum ScreenAnnotationCaptureKind
{
    /// <summary>No screen share resolved yet.</summary>
    None = 0,
    /// <summary>Whole monitor / entire screen — overlay is captured into the stream.</summary>
    Monitor = 1,
    /// <summary>Single window — overlay draws on the sharer's desktop but is NOT captured.</summary>
    Window = 2,
    /// <summary>Target could not be resolved to a monitor or window.</summary>
    Unknown = 3,
    /// <summary>The sharer's platform has no native overlay implementation.</summary>
    Unsupported = 4
}

/// <summary>
/// Kind of annotation event sent over the hub.
/// </summary>
public enum ScreenAnnotationEventKind
{
    /// <summary>Start a new stroke.</summary>
    Begin = 0,
    /// <summary>Append points to an in-progress stroke.</summary>
    Points = 1,
    /// <summary>Finish an in-progress stroke.</summary>
    End = 2,
    /// <summary>Remove the sender's most recent stroke.</summary>
    Undo = 3,
    /// <summary>Remove every stroke drawn by the sender.</summary>
    Clear = 4,
    /// <summary>Remove every stroke from every participant (sharer only).</summary>
    ClearAll = 5
}

/// <summary>
/// Annotation permission + capture description for <em>one screen share</em>.
/// Published by the participant who is sharing; everyone else in the call receives
/// it and shows or hides their annotation tools for that share accordingly.
///
/// A call holds one of these per sharer, keyed by <see cref="SharerUserId"/>. Two
/// people can share and allow annotations at the same time without one evicting
/// the other, so every message that carries a state also carries the sharer it
/// belongs to.
/// </summary>
[MessagePackObject]
public class ScreenAnnotationState
{
    /// <summary>True while the sharer allows other participants to draw.</summary>
    [Key(0)] public bool Enabled { get; set; }

    /// <summary>
    /// User sharing their screen (and hosting the overlay). Doubles as the key
    /// identifying which of a call's concurrent shares this state describes.
    /// </summary>
    [Key(1)] public Guid SharerUserId { get; set; }

    /// <summary>What the sharer is capturing.</summary>
    [Key(2)] public ScreenAnnotationCaptureKind CaptureKind { get; set; }

    /// <summary>Captured surface width in pixels, or 0 when unknown.</summary>
    [Key(3)] public int SurfaceWidth { get; set; }

    /// <summary>Captured surface height in pixels, or 0 when unknown.</summary>
    [Key(4)] public int SurfaceHeight { get; set; }

    /// <summary>
    /// True when the sharer's native desktop overlay is running. When false,
    /// viewers fall back to rendering annotations locally over the video.
    /// </summary>
    [Key(5)] public bool OverlayActive { get; set; }

    /// <summary>
    /// Seconds a finished stroke stays on screen before fading out, or 0 to keep
    /// strokes until someone clears them.
    ///
    /// This lives in the shared state rather than as a local preference because
    /// the sharer's overlay is what actually draws the ink: if each viewer expired
    /// strokes on their own schedule, the canvas fallback and the captured video
    /// would disagree about what is still on screen.
    /// </summary>
    [Key(6)] public int AutoFadeSeconds { get; set; } = DefaultAutoFadeSeconds;

    /// <summary>Strokes vanish shortly after being drawn unless pinned.</summary>
    public const int DefaultAutoFadeSeconds = 5;

    /// <summary>True when strokes persist until explicitly cleared.</summary>
    [IgnoreMember]
    public bool AnnotationsArePermanent => AutoFadeSeconds <= 0;

    /// <summary>
    /// True when strokes drawn by viewers are visible to everyone through the
    /// published video (monitor capture with a live overlay). When false, each
    /// client must render annotations locally to see them.
    /// </summary>
    [IgnoreMember]
    public bool StrokesVisibleInStream
        => OverlayActive && CaptureKind == ScreenAnnotationCaptureKind.Monitor;

    public ScreenAnnotationState Clone() => new()
    {
        Enabled = Enabled,
        SharerUserId = SharerUserId,
        CaptureKind = CaptureKind,
        SurfaceWidth = SurfaceWidth,
        SurfaceHeight = SurfaceHeight,
        OverlayActive = OverlayActive,
        AutoFadeSeconds = AutoFadeSeconds
    };
}

/// <summary>
/// A single annotation update. Coordinates are normalized to the captured surface
/// (0..1 on both axes) so they line up regardless of each viewer's window size,
/// display scaling, or the encoder's output resolution.
///
/// Normalization is per share, so an event is only meaningful alongside the share
/// it was drawn on — see <see cref="TargetSharerUserId"/>.
/// </summary>
[MessagePackObject]
public class ScreenAnnotationEvent
{
    [Key(0)] public ScreenAnnotationEventKind Kind { get; set; }

    /// <summary>Client-generated stroke identifier, unique per sender.</summary>
    [Key(1)] public string StrokeId { get; set; } = string.Empty;

    [Key(2)] public ScreenAnnotationTool Tool { get; set; }

    /// <summary>Stroke color as 0xAARRGGBB.</summary>
    [Key(3)] public uint Color { get; set; }

    /// <summary>Stroke width as a fraction of the captured surface height.</summary>
    [Key(4)] public float Width { get; set; }

    /// <summary>Flat list of normalized x,y pairs.</summary>
    [Key(5)] public float[] Points { get; set; } = [];

    /// <summary>
    /// The share this stroke belongs to, i.e. whose screen it was drawn on. Required:
    /// with several people sharing at once, the coordinates only mean something
    /// relative to one particular captured surface, and the server uses this to pick
    /// which sharer's permission gates the event.
    /// </summary>
    [Key(6)] public Guid TargetSharerUserId { get; set; }
}
