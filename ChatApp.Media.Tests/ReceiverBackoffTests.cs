using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Covers the one signal that reaches us from beyond the SFU.
/// <para>
/// Reception reports describe the leg between this app and Janus, and that leg is a
/// server on a good link — it reads clean while a viewer on the far side is being flooded.
/// The only thing the viewer itself tells us is that it has stopped decoding, which
/// arrives as a burst of keyframe requests. Everything here is about not throwing that
/// signal away.
/// </para>
/// </summary>
public class ReceiverBackoffTests
{
    private static BandwidthSample Clean(int observedBps, double seconds = 5) => new()
    {
        ObservedBps = observedBps,
        LossFraction = 0,
        JitterMs = 5,
        ReportIsFresh = true,
        Elapsed = TimeSpan.FromSeconds(seconds)
    };

    private static BandwidthEstimator Estimator(int startBps = 8_000_000)
        => new(startBps, minimumBps: 300_000, maximumBps: 10_000_000);

    [Fact]
    public void A_receiver_that_stopped_decoding_cuts_the_target()
    {
        var estimator = Estimator();

        var after = estimator.ReportCongestion(8_000_000, "3 keyframe requests inside 3s");

        Assert.True(after < 8_000_000, "the target did not come down");
        Assert.Equal(BandwidthAction.Decrease, estimator.LastAction);
    }

    [Fact]
    public void The_rate_that_broke_a_receiver_is_not_remembered_as_capacity()
    {
        // This is what sent the target back into the wall. Capacity means "the link was
        // carrying this when it pushed back", which is true of a loss report and exactly
        // backwards for a receiver saying it could not decode. Filed as capacity, the
        // estimator treats anything below 85% of the failing rate as familiar ground and
        // climbs back at the fast recovery rate.
        var estimator = Estimator();

        var after = estimator.ReportCongestion(8_000_000, "receiver stopped decoding");

        Assert.Equal(after, estimator.CapacityBps);
        Assert.True(
            estimator.CapacityBps < 8_000_000,
            $"the failing rate was filed as capacity ({estimator.CapacityBps} bps)");
    }

    [Fact]
    public void Clean_reports_from_the_sfu_do_not_lift_the_hold()
    {
        // The exact shape of the logged failure: cut at 22:25:28, then two 8% steps on
        // clean reports put the target back at the failing rate by 22:25:39, and the
        // receiver broke again at 22:25:43 — with the picture frozen through all of it.
        var estimator = Estimator();

        var after = estimator.ReportCongestion(8_000_000, "receiver stopped decoding");

        // Three reports, every one of them clean, because the leg they measure is fine.
        for (var i = 0; i < 3; i++)
        {
            var target = estimator.Update(Clean(after));

            Assert.Equal(after, target);
            Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
            Assert.Contains("receiver stopped decoding", estimator.LastReason);
        }
    }

    [Fact]
    public void The_hold_expires_so_the_target_is_not_frozen_where_it_was_cut()
    {
        // The hold is a pause, not a stop: the reduced rate is a guess and the path may
        // well carry more, up to the ceiling the failure established.
        var estimator = Estimator();

        var after = estimator.ReportCongestion(8_000_000, "receiver stopped decoding");

        for (var i = 0; i < 5; i++)
            estimator.Update(Clean(after));

        var target = estimator.Update(Clean(after));

        Assert.True(target > after, $"the target never resumed growing ({target} bps)");
    }

    [Fact]
    public void The_rate_that_broke_a_viewer_becomes_a_ceiling_for_the_call()
    {
        // The logged failure in full: cut, hold, climb, break at the same place, repeat —
        // with the picture frozen every time round. Reception reports come from the SFU
        // and never object, so nothing else in the controller can stop it.
        var estimator = Estimator();

        estimator.ReportCongestion(8_000_000, "receiver stopped decoding");

        Assert.Equal(6_800_000, estimator.ReceiverCeilingBps);

        // Sixty clean reports — five minutes of the SFU saying everything is fine.
        var target = estimator.CurrentBps;
        for (var i = 0; i < 60; i++)
            target = estimator.Update(Clean(target));

        Assert.True(
            target <= 6_800_000,
            $"the target climbed back past the rate that broke a viewer ({target} bps)");
    }

    [Fact]
    public void Each_failure_lowers_the_ceiling_further()
    {
        // One storm bounds the path; the next bounds it tighter. Two are enough to land on
        // what it actually carries, rather than oscillating around it indefinitely.
        var estimator = Estimator();

        estimator.ReportCongestion(8_000_000, "receiver stopped decoding");
        Assert.Equal(6_800_000, estimator.ReceiverCeilingBps);

        estimator.ReportCongestion(6_000_000, "receiver stopped decoding");
        Assert.Equal(5_100_000, estimator.ReceiverCeilingBps);
    }

    [Fact]
    public void A_later_failure_at_a_higher_rate_does_not_raise_the_ceiling()
    {
        // Storms arrive while the target is still coming down, so a stale one can name a
        // rate above what has already been ruled out. The ceiling only ratchets.
        var estimator = Estimator();

        estimator.ReportCongestion(4_000_000, "receiver stopped decoding");
        var tight = estimator.ReceiverCeilingBps;

        estimator.ReportCongestion(9_000_000, "receiver stopped decoding");

        Assert.Equal(tight, estimator.ReceiverCeilingBps);
    }

    [Fact]
    public void A_viewer_cannot_clamp_the_call_below_its_floor()
    {
        var estimator = Estimator(startBps: 1_000_000);

        for (var i = 0; i < 20; i++)
            estimator.ReportCongestion(estimator.CurrentBps, "receiver stopped decoding");

        Assert.Equal(300_000, estimator.CurrentBps);
        Assert.True(estimator.ReceiverCeilingBps >= 300_000);
    }

    [Fact]
    public void Reconnecting_forgets_the_ceiling()
    {
        // A reconnect may be a different path entirely — a phone moving from a congested
        // cell to wifi. What broke the old one is not evidence about the new one.
        var estimator = Estimator();

        estimator.ReportCongestion(4_000_000, "receiver stopped decoding");
        Assert.True(estimator.ReceiverCeilingBps < 10_000_000);

        estimator.Reset(8_000_000);

        Assert.Equal(10_000_000, estimator.ReceiverCeilingBps);
    }

    [Fact]
    public void A_second_failure_during_the_hold_cuts_again_rather_than_being_ignored()
    {
        // Holding must not mean deaf. If the reduced rate is still too much, the receiver
        // says so again and that has to land.
        var estimator = Estimator();

        var first = estimator.ReportCongestion(8_000_000, "receiver stopped decoding");
        estimator.Update(Clean(first));

        var second = estimator.ReportCongestion(first, "receiver stopped decoding");

        Assert.True(second < first, $"the second failure did not cut ({first} -> {second} bps)");
    }

    [Fact]
    public void Loss_reported_by_the_sfu_still_acts_immediately_during_a_hold()
    {
        // The hold is about not *growing* on evidence from the wrong leg. Real loss on the
        // leg we can see is still real and must not be sat on.
        var estimator = Estimator();

        var after = estimator.ReportCongestion(8_000_000, "receiver stopped decoding");

        var target = estimator.Update(new BandwidthSample
        {
            ObservedBps = after,
            LossFraction = 0.20,
            JitterMs = 5,
            ReportIsFresh = true,
            Elapsed = TimeSpan.FromSeconds(5)
        });

        Assert.True(target < after, "serious loss was ignored while holding");
        Assert.Equal(BandwidthAction.Decrease, estimator.LastAction);
    }
}
