using ChatApp.Media.Engine;

namespace ChatApp.Media.Tests;

/// <summary>
/// A share encoded above the resolution its bitrate was chosen for spends the whole budget
/// on keyframes: each one is a burst large enough to lose packets, every loss brings
/// another keyframe request, and the share never recovers. The Blazor client caps shares
/// the same way (getRequiredScreenShareScaleDownBy).
/// </summary>
public class ScreenShareScalingTests
{
    [Fact]
    public void A_larger_monitor_is_brought_down_to_the_configured_resolution()
    {
        // The case that broke: a 1440p desktop shared against a 1080p setting is 1.78x the
        // pixels the bitrate was chosen for.
        var (width, height) = NativeMediaEngine.ScaleToFit(2560, 1440, 1920, 1080);

        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void Aspect_ratio_survives_the_cap()
    {
        // An ultrawide must not be squashed to the limit's shape.
        var (width, height) = NativeMediaEngine.ScaleToFit(3440, 1440, 1920, 1080);

        Assert.Equal(1440.0 / 3440.0, height / (double)width, 3);
        Assert.True(width <= 1920 && height <= 1080, $"{width}x{height} exceeds the limit");
    }

    [Fact]
    public void A_smaller_monitor_is_left_alone()
    {
        // Enlarging invents detail that was never captured and costs bitrate to carry.
        var (width, height) = NativeMediaEngine.ScaleToFit(1280, 720, 1920, 1080);

        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void An_exact_match_is_left_alone()
    {
        var (width, height) = NativeMediaEngine.ScaleToFit(1920, 1080, 1920, 1080);

        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    [InlineData(1366, 768)]
    [InlineData(3840, 2160)]
    public void The_result_is_always_encodable(int sourceWidth, int sourceHeight)
    {
        // 4:2:0 chroma cannot represent odd dimensions; libx264 refuses to open on them.
        var (width, height) = NativeMediaEngine.ScaleToFit(sourceWidth, sourceHeight, 1920, 1080);

        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
        Assert.True(width >= 2 && height >= 2);
    }

    [Fact]
    public void A_missing_limit_leaves_the_source_untouched()
    {
        // Nothing configured is not a reason to shrink to nothing.
        Assert.Equal((2560, 1440), NativeMediaEngine.ScaleToFit(2560, 1440, 0, 0));
        Assert.Equal((0, 0), NativeMediaEngine.ScaleToFit(0, 0, 1920, 1080));
    }

    [Fact]
    public void The_shrink_stops_at_a_quarter()
    {
        // Past that the picture is unreadable anyway, and a share this far over its limit
        // wants a smaller capture target rather than more scaling.
        var (width, _) = NativeMediaEngine.ScaleToFit(15360, 8640, 1920, 1080);

        Assert.Equal(15360 / 4, width);
    }
}
