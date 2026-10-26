using ChatApp.Media.Capture;

namespace ChatApp.Media.Tests;

/// <summary>
/// The watchdog exists for one specific failure: Desktop Duplication stops delivering
/// frames when a fullscreen application takes the display, and says nothing about it. No
/// frames are captured, so none are encoded, so none are sent, so none are lost — the
/// share freezes while every statistic on both ends reads perfectly healthy.
/// </summary>
public class CaptureWatchdogTests
{
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    private static CaptureWatchdog Watching(TimeSpan? stall = null, TimeSpan? cooldown = null)
    {
        var watchdog = new CaptureWatchdog(cooldown);
        watchdog.Started(TimeSpan.Zero, stall ?? Seconds(2));
        return watchdog;
    }

    [Fact]
    public void A_capture_that_keeps_producing_is_left_alone()
    {
        var watchdog = Watching();

        // 30 fps for ten seconds. Nothing here should ever trip the watchdog, and a false
        // positive is worse than the stall: it tears down a working share.
        for (var frame = 1; frame <= 300; frame++)
        {
            var now = Seconds(frame / 30.0);
            watchdog.FrameArrived(now);

            Assert.False(watchdog.TryBeginRestart(now, out _), $"restarted at frame {frame}");
        }

        Assert.Equal(0, watchdog.Restarts);
    }

    [Fact]
    public void A_capture_that_goes_quiet_is_restarted()
    {
        var watchdog = Watching();

        watchdog.FrameArrived(Seconds(1));

        Assert.False(watchdog.TryBeginRestart(Seconds(2.5), out _));
        Assert.True(watchdog.TryBeginRestart(Seconds(3.1), out var silence));
        Assert.Equal(2.1, silence.TotalSeconds, precision: 3);
    }

    [Fact]
    public void The_silence_reported_is_measured_from_the_last_frame_not_from_the_share_starting()
    {
        // What gets logged has to name the real gap, because that number is the difference
        // between "the network hiccuped" and "the capture died ten seconds ago".
        var watchdog = Watching();

        watchdog.FrameArrived(Seconds(30));

        Assert.True(watchdog.TryBeginRestart(Seconds(45), out var silence));
        Assert.Equal(15, silence.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Only_one_restart_runs_at_a_time()
    {
        // The restart is asynchronous — it stops a device and opens another — while the
        // check runs every second on the engine's heartbeat. Without the claim, a slow
        // restart would have several more piling in behind it.
        var watchdog = Watching();

        Assert.True(watchdog.TryBeginRestart(Seconds(3), out _));
        Assert.False(watchdog.TryBeginRestart(Seconds(4), out _));
        Assert.False(watchdog.TryBeginRestart(Seconds(20), out _));

        watchdog.RestartFinished(Seconds(21), succeeded: true, stallTimeout: Seconds(2));

        // Still nothing, because the successful restart reset the stall clock.
        Assert.False(watchdog.TryBeginRestart(Seconds(22), out _));
        Assert.True(watchdog.TryBeginRestart(Seconds(30), out _));
    }

    [Fact]
    public void A_restart_that_does_not_help_is_retried_but_only_after_the_cooldown()
    {
        // A monitor that was unplugged will never capture again. Retrying is right;
        // retrying every second forever is not.
        var watchdog = Watching(stall: Seconds(2), cooldown: Seconds(5));

        Assert.True(watchdog.TryBeginRestart(Seconds(3), out _));
        watchdog.RestartFinished(Seconds(3.2), succeeded: true, stallTimeout: Seconds(2));

        // Still no frames after the restart.
        Assert.False(watchdog.TryBeginRestart(Seconds(6), out _), "retried inside the cooldown");
        Assert.True(watchdog.TryBeginRestart(Seconds(8.5), out _));

        Assert.Equal(1, watchdog.Restarts);
    }

    [Fact]
    public void A_failed_restart_releases_the_claim_so_the_next_attempt_can_run()
    {
        var watchdog = Watching(stall: Seconds(2), cooldown: Seconds(5));

        Assert.True(watchdog.TryBeginRestart(Seconds(3), out _));
        watchdog.RestartFinished(Seconds(3.5), succeeded: false);

        Assert.False(watchdog.TryBeginRestart(Seconds(4), out _), "ignored the cooldown");
        Assert.True(watchdog.TryBeginRestart(Seconds(9), out _));

        // A restart that threw is not a restart.
        Assert.Equal(0, watchdog.Restarts);
    }

    [Fact]
    public void A_successful_restart_gets_the_full_timeout_to_produce_its_first_frame()
    {
        // Opening a duplication device and getting the first frame out of it is not
        // instant. Judging the new capture on the old one's silence would restart it
        // again before it ever had a chance.
        var watchdog = Watching(stall: Seconds(2), cooldown: Seconds(1));

        Assert.True(watchdog.TryBeginRestart(Seconds(10), out _));
        watchdog.RestartFinished(Seconds(10.4), succeeded: true, stallTimeout: Seconds(2));

        Assert.False(watchdog.TryBeginRestart(Seconds(12), out _), "did not wait for the first frame");
        Assert.True(watchdog.TryBeginRestart(Seconds(12.5), out _));
    }

    [Fact]
    public void Nothing_is_restarted_before_a_share_starts_or_after_it_stops()
    {
        var watchdog = new CaptureWatchdog();

        // Never started: the clock reads zero, which is not two seconds of a dead capture.
        Assert.False(watchdog.TryBeginRestart(Seconds(600), out _));

        watchdog.Started(Seconds(600), Seconds(2));
        watchdog.Stopped();

        // Stopping a share is exactly zero frames arriving, and must not look like a stall.
        Assert.False(watchdog.TryBeginRestart(Seconds(700), out _));
    }

    [Fact]
    public void A_backend_that_only_sends_on_change_is_never_restarted_for_being_quiet()
    {
        // Windows Graphics Capture sends a frame when the screen changes and not
        // otherwise, so someone reading a still document produces nothing for minutes.
        // Restarting for that would interrupt a share that is working perfectly — and the
        // Desktop Duplication pathology this watchdog exists for cannot happen there
        // anyway, because WGC reports its failures instead of going quiet.
        var watchdog = new CaptureWatchdog();
        watchdog.Started(TimeSpan.Zero, stallTimeout: null);

        watchdog.FrameArrived(Seconds(1));

        Assert.False(watchdog.TryBeginRestart(Seconds(60), out _));
        Assert.False(watchdog.TryBeginRestart(Seconds(3600), out _));
        Assert.False(watchdog.SilenceIsSuspicious);
        Assert.Equal(0, watchdog.Restarts);
    }

    [Fact]
    public void Silence_is_only_called_suspicious_while_a_watched_backend_is_running()
    {
        var watchdog = new CaptureWatchdog();
        Assert.False(watchdog.SilenceIsSuspicious);

        watchdog.Started(TimeSpan.Zero, Seconds(2));
        Assert.True(watchdog.SilenceIsSuspicious);

        watchdog.Stopped();
        Assert.False(watchdog.SilenceIsSuspicious);
    }

    [Fact]
    public void A_rebuild_onto_a_different_backend_adopts_its_tolerance_for_silence()
    {
        // Duplication dies, the rebuild falls back — or forward — onto a backend that only
        // sends on change. Keeping the old two-second rule would then restart a healthy
        // capture every five seconds for as long as nobody touched the screen.
        var watchdog = Watching(stall: Seconds(2), cooldown: Seconds(1));

        Assert.True(watchdog.TryBeginRestart(Seconds(5), out _));
        watchdog.RestartFinished(Seconds(5), succeeded: true, stallTimeout: null);

        Assert.Equal(1, watchdog.Restarts);
        Assert.False(watchdog.SilenceIsSuspicious);
        Assert.False(watchdog.TryBeginRestart(Seconds(500), out _));
    }

    [Fact]
    public void Restarting_is_counted_per_share_not_for_the_life_of_the_process()
    {
        var watchdog = Watching(stall: Seconds(2), cooldown: Seconds(1));

        Assert.True(watchdog.TryBeginRestart(Seconds(3), out _));
        watchdog.RestartFinished(Seconds(3), succeeded: true, stallTimeout: Seconds(2));
        Assert.Equal(1, watchdog.Restarts);

        watchdog.Stopped();
        watchdog.Started(Seconds(100), Seconds(2));

        Assert.Equal(0, watchdog.Restarts);
    }

    [Fact]
    public void A_frame_from_a_lagging_thread_cannot_wind_the_clock_backwards()
    {
        // Capture callbacks arrive on a device thread and the check runs on the pump, so
        // the two can interleave. An older timestamp landing late must not make a live
        // capture look stalled.
        var watchdog = Watching();

        watchdog.FrameArrived(Seconds(10));
        watchdog.FrameArrived(Seconds(9.5));

        Assert.False(watchdog.TryBeginRestart(Seconds(11.5), out _));
    }
}
