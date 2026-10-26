namespace ChatApp.Media.Engine;

/// <summary>
/// Sheds resolution when the bitrate we are allowed cannot carry the one we are sending.
/// <para>
/// A fixed-resolution encoder answers a shrinking budget the only way it can: by
/// quantizing harder. The picture turns to mush, and under motion the frames stay large
/// anyway — large enough to lose packets, which costs the receiver its reference chain.
/// Sending fewer, better pixels is what a browser does here, through
/// <c>degradationPreference: maintain-framerate</c> plus the publisher performance guard
/// in <c>webrtc-manager.js</c>, which steps <c>scaleResolutionDownBy</c> in quarters.
/// This is that guard, driven by the signal this client actually has: the congestion
/// controller's target.
/// </para>
/// </summary>
public sealed class ScreenShareQualityGuard
{
    /// <summary>
    /// Bits per pixel per frame that screen content needs to hold up under motion. Roughly
    /// 2.8 Mbps for 1080p30, 1.3 for 720p30 — in the range browsers settle at for shares.
    /// </summary>
    private const double BitsPerPixel = 0.045;

    /// <summary>Step size, and ceiling, both matching the Blazor guard.</summary>
    private const double ScaleStep = 0.25;

    private const double MaximumScale = 2.5;

    /// <summary>Consecutive samples before acting, in either direction.</summary>
    private const int Streak = 3;

    /// <summary>
    /// Headroom required before climbing back, so the step up does not immediately
    /// re-trigger the step down it just undid.
    /// </summary>
    private const double RecoveryHeadroom = 1.25;

    private int _lowStreak;
    private int _recoveryStreak;

    /// <summary>How much the encode is currently shrunk by. 1 is full size.</summary>
    public double Scale { get; private set; } = 1;

    public string LastReason { get; private set; } = "no samples yet";

    /// <summary>Bitrate needed to send this many pixels at this frame rate.</summary>
    public static int RequiredBps(int width, int height, int frameRate)
        => (int)Math.Min(int.MaxValue, (double)width * height * Math.Max(1, frameRate) * BitsPerPixel);

    public void Reset()
    {
        Scale = 1;
        _lowStreak = 0;
        _recoveryStreak = 0;
        LastReason = "reset";
    }

    /// <summary>
    /// Folds in one sample. Returns the new scale when it changed, otherwise null.
    /// <paramref name="width"/> and <paramref name="height"/> are the full capture size,
    /// not the currently-scaled one.
    /// </summary>
    public double? Update(int targetBps, int width, int height, int frameRate)
    {
        if (width <= 0 || height <= 0 || targetBps <= 0)
            return null;

        var current = Sized(width, height, Scale);
        var needed = RequiredBps(current.Width, current.Height, frameRate);

        if (targetBps < needed)
        {
            _lowStreak++;
            _recoveryStreak = 0;
        }
        else
        {
            _lowStreak = 0;

            var larger = Sized(width, height, Math.Max(1, Scale - ScaleStep));
            var neededForLarger = RequiredBps(larger.Width, larger.Height, frameRate);

            if (Scale > 1 && targetBps > neededForLarger * RecoveryHeadroom)
            {
                _recoveryStreak++;
            }
            else
            {
                // Decay rather than reset: a single ambiguous sample should not throw away
                // a run of good ones, or the scale ratchets down over a share's lifetime
                // and never climbs back.
                _recoveryStreak = Math.Max(0, _recoveryStreak - 1);
            }
        }

        if (_lowStreak >= Streak && Scale < MaximumScale)
        {
            _lowStreak = 0;
            Scale = Math.Min(MaximumScale, Scale + ScaleStep);
            LastReason = $"{targetBps / 1000} kbps cannot carry {current.Width}x{current.Height}";
            return Scale;
        }

        if (_recoveryStreak >= Streak && Scale > 1)
        {
            _recoveryStreak = 0;
            Scale = Math.Max(1, Scale - ScaleStep);
            LastReason = $"{targetBps / 1000} kbps has room for more detail";
            return Scale;
        }

        return null;
    }

    /// <summary>The encode size for a capture size at a given scale, kept even for 4:2:0.</summary>
    public static (int Width, int Height) Sized(int width, int height, double scale)
    {
        if (scale <= 1)
            return (width, height);

        var scaled = (
            Width: (int)Math.Round(width / scale) & ~1,
            Height: (int)Math.Round(height / scale) & ~1);

        return (Math.Max(2, scaled.Width), Math.Max(2, scaled.Height));
    }
}
