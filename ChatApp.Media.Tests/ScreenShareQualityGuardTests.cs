using ChatApp.Media.Engine;

namespace ChatApp.Media.Tests;

/// <summary>
/// The native equivalent of the Blazor publisher performance guard: hold the frame rate,
/// give up resolution. A fixed-size encoder answers a shrinking budget by quantizing until
/// the picture is mush while still emitting frames too big to survive the link.
/// </summary>
public class ScreenShareQualityGuardTests
{
    private const int Fps = 30;
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>Comfortably more than 1080p30 needs.</summary>
    private static readonly int Plenty = ScreenShareQualityGuard.RequiredBps(Width, Height, Fps) * 2;

    private static double? Feed(ScreenShareQualityGuard guard, int targetBps, int times)
    {
        double? last = null;

        for (var i = 0; i < times; i++)
            last = guard.Update(targetBps, Width, Height, Fps) ?? last;

        return last;
    }

    [Fact]
    public void A_budget_that_covers_the_resolution_changes_nothing()
    {
        var guard = new ScreenShareQualityGuard();

        Assert.Null(Feed(guard, Plenty, times: 10));
        Assert.Equal(1, guard.Scale);
    }

    [Fact]
    public void A_starved_budget_sheds_resolution_after_three_samples()
    {
        // Deliberately not on the first sample: a momentary dip is not a reason to rebuild
        // the encoder and force a keyframe.
        var guard = new ScreenShareQualityGuard();
        var starved = ScreenShareQualityGuard.RequiredBps(Width, Height, Fps) / 4;

        Assert.Null(guard.Update(starved, Width, Height, Fps));
        Assert.Null(guard.Update(starved, Width, Height, Fps));

        var scale = guard.Update(starved, Width, Height, Fps);

        Assert.Equal(1.25, scale);
        Assert.Equal(1.25, guard.Scale);
    }

    [Fact]
    public void It_keeps_shedding_while_the_budget_stays_short_but_stops_at_a_limit()
    {
        var guard = new ScreenShareQualityGuard();

        Feed(guard, 50_000, times: 60);

        // Past this the share is unreadable and the problem is not resolution.
        Assert.Equal(2.5, guard.Scale);
    }

    [Fact]
    public void Recovery_needs_headroom_not_merely_enough()
    {
        // Stepping back up the moment the budget exactly covers the larger size would
        // re-trigger the step down that had just been taken.
        var guard = new ScreenShareQualityGuard();
        Feed(guard, ScreenShareQualityGuard.RequiredBps(Width, Height, Fps) / 4, times: 3);
        Assert.Equal(1.25, guard.Scale);

        // Exactly what full size needs, and no more.
        Feed(guard, ScreenShareQualityGuard.RequiredBps(Width, Height, Fps), times: 10);

        Assert.Equal(1.25, guard.Scale);
    }

    [Fact]
    public void Clear_headroom_wins_the_resolution_back()
    {
        var guard = new ScreenShareQualityGuard();
        Feed(guard, ScreenShareQualityGuard.RequiredBps(Width, Height, Fps) / 4, times: 3);
        Assert.Equal(1.25, guard.Scale);

        Feed(guard, Plenty, times: 3);

        Assert.Equal(1, guard.Scale);
    }

    [Fact]
    public void An_ambiguous_sample_decays_the_recovery_run_rather_than_ending_it()
    {
        // Without this the scale ratchets down over a share's lifetime: every quarter-step
        // back needs an unbroken run, and real links do not give unbroken runs.
        var guard = new ScreenShareQualityGuard();
        Feed(guard, ScreenShareQualityGuard.RequiredBps(Width, Height, Fps) / 4, times: 3);

        var required = ScreenShareQualityGuard.RequiredBps(Width, Height, Fps);

        guard.Update(Plenty, Width, Height, Fps);
        guard.Update(Plenty, Width, Height, Fps);

        // One sample that neither starves nor clearly has room.
        guard.Update(required, Width, Height, Fps);

        // Two more good ones: with a decay this is enough, with a reset it would not be.
        guard.Update(Plenty, Width, Height, Fps);
        var scale = guard.Update(Plenty, Width, Height, Fps);

        Assert.Equal(1, scale);
    }

    [Fact]
    public void The_scaled_size_stays_encodable()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5 })
        {
            var (width, height) = ScreenShareQualityGuard.Sized(1920, 1080, scale);

            Assert.Equal(0, width % 2);
            Assert.Equal(0, height % 2);
            Assert.True(width >= 2 && height >= 2);
            Assert.True(width <= 1920 && height <= 1080);
        }
    }

    [Fact]
    public void The_requirement_tracks_pixels_and_frame_rate()
    {
        // Half the pixels needs half the bitrate; half the frame rate likewise.
        var full = ScreenShareQualityGuard.RequiredBps(1920, 1080, 30);

        Assert.Equal(full / 2, ScreenShareQualityGuard.RequiredBps(1920, 1080, 15), tolerance: 1000);
        Assert.Equal(full / 4, ScreenShareQualityGuard.RequiredBps(960, 540, 30), tolerance: 1000);

        // And it lands somewhere defensible for a 1080p30 share.
        Assert.InRange(full, 2_000_000, 3_500_000);
    }

    [Fact]
    public void Nonsense_input_is_ignored()
    {
        var guard = new ScreenShareQualityGuard();

        Assert.Null(guard.Update(0, Width, Height, Fps));
        Assert.Null(guard.Update(Plenty, 0, 0, Fps));
        Assert.Equal(1, guard.Scale);
    }
}
