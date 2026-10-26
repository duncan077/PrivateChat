using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// The congestion controller decides what every participant is allowed to send. Its
/// mistakes are invisible in a lab and expensive on a real link: too eager and the queue
/// fills until the picture breaks up, too timid and a fast connection is left unused.
/// </summary>
public class BandwidthEstimatorTests
{
    private const int Start = 1_000_000;
    private const int Floor = 300_000;
    private const int Ceiling = 20_000_000;

    private static BandwidthEstimator Create(int start = Start) => new(start, Floor, Ceiling);

    /// <summary>
    /// One second per sample by default, and long enough that growth past the measured
    /// capacity is never paced out — the tests that care about pacing say so explicitly.
    /// </summary>
    private static BandwidthSample Sample(
        int observedBps,
        double lossFraction = 0,
        double jitterMs = 5,
        bool fresh = true,
        double elapsedSeconds = 6) => new()
        {
            ObservedBps = observedBps,
            LossFraction = lossFraction,
            JitterMs = jitterMs,
            ReportIsFresh = fresh,
            Elapsed = TimeSpan.FromSeconds(elapsedSeconds)
        };

    [Fact]
    public void An_untested_path_is_climbed_quickly()
    {
        // Slow start. The send side only hears from RTCP every few seconds, so 8% a report
        // would leave a screen share soft for a minute before it reached its ceiling.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: Start));

        Assert.True(estimator.IsSlowStarting);
        Assert.Equal(BandwidthAction.Increase, estimator.LastAction);
        Assert.Equal(1_500_000, target);
    }

    [Fact]
    public void A_cut_records_what_the_link_carried_not_what_broke_it()
    {
        // The target that failed was by definition more than the link could take; the rate
        // that got through is the only measurement of it there is.
        var estimator = Create(start: 4_000_000);

        estimator.Update(Sample(observedBps: 1_000_000, lossFraction: 0.50));

        Assert.Equal(1_000_000, estimator.CapacityBps);
    }

    [Fact]
    public void Recovery_climbs_straight_back_to_the_measured_capacity()
    {
        // Returning to a rate the link carried seconds ago is not a gamble. Creeping there
        // at 8% a report is a minute of needlessly soft picture.
        var estimator = Create(start: 4_000_000);

        estimator.Update(Sample(observedBps: 4_000_000, lossFraction: 0.40));
        var afterCut = estimator.CurrentBps;

        var target = estimator.Update(Sample(observedBps: afterCut));

        Assert.True(target > afterCut, "recovery did not move");
        Assert.Equal(estimator.CapacityBps, target);
    }

    [Fact]
    public void Past_the_measured_capacity_it_only_creeps()
    {
        // Beyond what was measured, nothing is known again — and taking another fast step
        // there simply re-runs the congestion the cut just escaped.
        var estimator = Create(start: 4_000_000);

        estimator.Update(Sample(observedBps: 4_000_000, lossFraction: 0.40));

        // One fast step returns to the measured capacity exactly...
        var recovered = estimator.Update(Sample(observedBps: estimator.CurrentBps));
        Assert.Equal(estimator.CapacityBps, recovered);

        // ...and the next only feels its way past it.
        var beyond = estimator.Update(Sample(observedBps: estimator.CurrentBps));
        Assert.Equal((int)Math.Round(recovered * 1.08), beyond);
    }

    [Fact]
    public void Pushing_past_the_measured_capacity_is_paced_by_the_clock()
    {
        // The failure this exists to stop: cut on a bad report, then climb 8% a second
        // straight back into the same wall, because reception reports arrive far more
        // often than once per feedback interval. Ten steps taken before the first is
        // answered is a sawtooth, not control.
        var estimator = Create(start: 10_000_000);

        estimator.Update(Sample(observedBps: 3_000_000, lossFraction: 0.20, elapsedSeconds: 1));
        var afterCut = estimator.CurrentBps;

        // A second of clean samples at the controller's own tick rate.
        for (var i = 0; i < 10; i++)
            estimator.Update(Sample(observedBps: afterCut, elapsedSeconds: 0.1));

        Assert.Equal(afterCut, estimator.CurrentBps);
    }

    [Fact]
    public void Once_the_step_has_had_time_to_be_answered_it_grows_again()
    {
        var estimator = Create(start: 10_000_000);

        estimator.Update(Sample(observedBps: 3_000_000, lossFraction: 0.20, elapsedSeconds: 1));
        var afterCut = estimator.CurrentBps;

        estimator.Update(Sample(observedBps: afterCut, elapsedSeconds: 6));

        Assert.True(estimator.CurrentBps > afterCut, "growth never resumed");
    }

    [Fact]
    public void Recovering_below_the_measured_capacity_is_not_paced()
    {
        // Below what the link has carried there is evidence, so getting back there does
        // not have to wait — only exploring above it does.
        var estimator = Create(start: 4_000_000);

        estimator.Update(Sample(observedBps: 4_000_000, lossFraction: 0.40, elapsedSeconds: 1));
        var afterCut = estimator.CurrentBps;

        var target = estimator.Update(Sample(observedBps: afterCut, elapsedSeconds: 0.1));

        Assert.True(target > afterCut, "recovery to a known-good rate was paced out");
    }

    [Fact]
    public void Growth_is_rationed_per_report_not_per_tick()
    {
        // The loop runs every second; reports arrive every few. Without a ration, the
        // ticks in between compound several times over before anything can contradict
        // them — which is a sawtooth, not congestion control.
        var estimator = Create();

        // One fresh clean report, then four seconds of the same report seen again.
        estimator.Update(Sample(observedBps: Start));
        var afterReport = estimator.CurrentBps;

        for (var i = 0; i < 4; i++)
            estimator.Update(Sample(observedBps: estimator.CurrentBps, fresh: false));

        Assert.Equal(afterReport, estimator.CurrentBps);
    }

    [Fact]
    public void After_the_path_pushes_back_growth_drops_to_eight_percent()
    {
        // The ceiling is roughly known now, and climbing back at slow-start speed would
        // walk straight into the same wall.
        var estimator = Create();

        estimator.Update(Sample(observedBps: Start, lossFraction: 0.20));
        Assert.False(estimator.IsSlowStarting);

        var afterCut = estimator.CurrentBps;
        var target = estimator.Update(Sample(observedBps: afterCut));

        Assert.Equal(BandwidthAction.Increase, estimator.LastAction);
        Assert.Equal((int)Math.Round(afterCut * 1.08), target);
    }

    [Fact]
    public void Slow_start_still_respects_what_is_flowing()
    {
        // Climbing fast is only safe while the sender is actually using the grant. A quiet
        // sender must not drag the estimate up behind it.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: 100_000));

        Assert.Equal(Start, target);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void Growth_is_capped_by_what_is_actually_flowing()
    {
        // Granting far above anything the path has been shown to carry is how an estimate
        // drifts into fantasy: the first burst that tries to use it is the one that
        // discovers the link never had room.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: 700_000));

        // 1.5 x 700k + 10k = 1.06 Mbps, below the 1.08 Mbps that 8% growth would ask for.
        Assert.Equal(1_060_000, target);
    }

    [Fact]
    public void An_idle_sender_does_not_ratchet_its_own_ceiling_down()
    {
        // A still screen share sends almost nothing. That is not evidence the path shrank,
        // and clamping to it would leave the share unable to respond when the screen moves.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: 50_000));

        Assert.Equal(Start, target);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void Nothing_flowing_holds_rather_than_collapsing()
    {
        // A muted camera or a paused share must not leave the target on the floor for
        // whenever it comes back.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: 0));

        Assert.Equal(Start, target);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.03)]
    [InlineData(0.05)]
    public void Mild_loss_holds_station(double loss)
    {
        // Some loss is normal and does not mean the path is overloaded. Growing through it
        // makes things worse; cutting on it wastes a link that is working.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: Start, lossFraction: loss));

        Assert.Equal(Start, target);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void No_single_cut_falls_off_a_cliff()
    {
        // The cut is sized from what was flowing, measured over a window that may have
        // caught the encoder idling. One such sample must not take the target to a
        // fraction of what the link was really carrying.
        var estimator = Create(start: 10_000_000);

        var target = estimator.Update(Sample(observedBps: 800_000, lossFraction: 0.11));

        Assert.Equal(5_000_000, target);
    }

    [Fact]
    public void Repeated_bad_reports_still_converge_quickly()
    {
        // Bounded per step, not bounded overall: a genuinely bad link is still found in a
        // few reports rather than being approached forever.
        var estimator = Create(start: 10_000_000);

        for (var i = 0; i < 4; i++)
            estimator.Update(Sample(observedBps: 800_000, lossFraction: 0.11));

        Assert.True(
            estimator.CurrentBps <= 1_000_000,
            $"four bad reports should have found the floor of the link, got {estimator.CurrentBps}");
    }

    [Theory]
    [InlineData(0.10, 950_000)]
    [InlineData(0.20, 900_000)]
    [InlineData(0.40, 800_000)]
    [InlineData(0.80, 600_000)]
    public void Serious_loss_cuts_in_proportion_to_it(double loss, int expected)
    {
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: Start, lossFraction: loss));

        Assert.Equal(BandwidthAction.Decrease, estimator.LastAction);
        Assert.Equal(expected, target);
    }

    [Fact]
    public void A_cut_backs_off_from_what_got_through_not_from_the_estimate()
    {
        // Trimming the estimate converges to just above the link's capacity, where the
        // controller sits holding at a steady few percent loss and calls it healthy.
        // Backing off from the delivered rate lands under the capacity instead.
        var estimator = Create(start: 2_000_000);

        var target = estimator.Update(Sample(observedBps: 1_500_000, lossFraction: 0.20));

        // From the 1.5 Mbps that got through, not the 2 Mbps that was hoped for.
        Assert.Equal(1_350_000, target);
    }

    [Fact]
    public void One_bad_report_is_only_acted_on_once()
    {
        // Reports arrive every few seconds and the controller runs every second, so the
        // same numbers are seen repeatedly. Cutting on each sighting turns one bad second
        // into a collapse the path never asked for.
        var estimator = Create();

        estimator.Update(Sample(observedBps: Start, lossFraction: 0.20));
        Assert.Equal(900_000, estimator.CurrentBps);

        for (var i = 0; i < 4; i++)
            estimator.Update(Sample(observedBps: Start, lossFraction: 0.20, fresh: false));

        Assert.Equal(900_000, estimator.CurrentBps);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void Loss_that_has_not_been_refuted_still_blocks_growth()
    {
        // The opposite failure: treating a stale report as no report would let the target
        // climb back up between reports while the loss was still happening.
        var estimator = Create();

        estimator.Update(Sample(observedBps: Start, lossFraction: 0.20));
        var afterCut = estimator.CurrentBps;

        for (var i = 0; i < 4; i++)
            estimator.Update(Sample(observedBps: Start, lossFraction: 0.20, fresh: false));

        Assert.Equal(afterCut, estimator.CurrentBps);
    }

    [Fact]
    public void Raised_jitter_stops_growth_without_forcing_a_cut()
    {
        // Jitter is a real signal but a noisy one, so on its own it only withholds more
        // traffic rather than taking any away.
        var estimator = Create();

        var target = estimator.Update(Sample(observedBps: Start, jitterMs: 45));

        Assert.Equal(Start, target);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void Jitter_that_is_extreme_and_still_climbing_does_force_a_cut()
    {
        // A queue filling up shows as delay before it shows as loss. Waiting for the loss
        // means waiting until the picture has already broken.
        var estimator = Create();

        estimator.Update(Sample(observedBps: Start, jitterMs: 100));
        estimator.Update(Sample(observedBps: Start, jitterMs: 110));
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);

        var target = estimator.Update(Sample(observedBps: Start, jitterMs: 120));

        Assert.Equal(BandwidthAction.Decrease, estimator.LastAction);
        Assert.Equal(850_000, target);
    }

    [Fact]
    public void Steady_high_jitter_is_not_treated_as_climbing()
    {
        // A link with consistently poor jitter is not a link that is deteriorating.
        var estimator = Create();

        for (var i = 0; i < 6; i++)
            estimator.Update(Sample(observedBps: Start, jitterMs: 120));

        Assert.Equal(Start, estimator.CurrentBps);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void Repeated_reports_cannot_fake_a_jitter_trend()
    {
        // The controller sees each report several times. If repeats counted, three
        // sightings of one report would look identical to a genuine three-report climb.
        var estimator = Create();

        estimator.Update(Sample(observedBps: Start, jitterMs: 100));

        for (var i = 0; i < 6; i++)
            estimator.Update(Sample(observedBps: Start, jitterMs: 100, fresh: false));

        Assert.Equal(Start, estimator.CurrentBps);
        Assert.Equal(BandwidthAction.Hold, estimator.LastAction);
    }

    [Fact]
    public void The_target_never_leaves_its_bounds()
    {
        var estimator = Create();

        for (var i = 0; i < 200; i++)
            estimator.Update(Sample(observedBps: 30_000_000));

        Assert.Equal(Ceiling, estimator.CurrentBps);

        for (var i = 0; i < 200; i++)
            estimator.Update(Sample(observedBps: 30_000_000, lossFraction: 0.9));

        Assert.Equal(Floor, estimator.CurrentBps);
    }

    [Fact]
    public void Lowering_the_ceiling_binds_immediately()
    {
        // Turning the quality setting down has to take effect now, not after the next
        // several reports.
        var estimator = Create(start: 8_000_000);

        estimator.SetCeiling(1_000_000);

        Assert.Equal(1_000_000, estimator.CurrentBps);
    }

    [Fact]
    public void Raising_the_ceiling_only_permits_growth()
    {
        // The new headroom has to be earned a step at a time; jumping to it would be the
        // overshoot the slow start exists to avoid.
        var estimator = Create();

        estimator.SetCeiling(20_000_000);

        Assert.Equal(Start, estimator.CurrentBps);
    }

    [Fact]
    public void Congestion_seen_outside_rtcp_backs_off_immediately()
    {
        // A receiver that has stopped decoding says so now; RTCP takes seconds and only
        // covers the leg to the SFU. Waiting for it means seconds of sending into a link
        // that is already dropping the stream.
        var estimator = Create(start: 4_000_000);

        var target = estimator.ReportCongestion(4_000_000, "keyframe storm");

        Assert.Equal(BandwidthAction.Decrease, estimator.LastAction);
        Assert.False(estimator.IsSlowStarting);

        // Deeper than a jitter cut, and deeper than the ceiling the same failure sets, so
        // the rate converges just under the wall rather than sitting on it.
        Assert.Equal(2_800_000, target);
        Assert.Equal(3_400_000, estimator.ReceiverCeilingBps);

        // Capacity is the reduced rate, not the one that broke the receiver. Recording the
        // failing rate here used to make everything below 85% of it look like ground the
        // link had already been measured to carry, so the target climbed back into it.
        Assert.Equal(target, estimator.CapacityBps);
    }

    [Fact]
    public void Congestion_reported_outside_rtcp_still_respects_the_floor()
    {
        var estimator = Create(start: Floor);

        for (var i = 0; i < 20; i++)
            estimator.ReportCongestion(Floor, "keyframe storm");

        Assert.Equal(Floor, estimator.CurrentBps);
    }

    [Fact]
    public void A_back_off_from_outside_rtcp_does_not_grow_back_until_a_report_arrives()
    {
        // Same rule as any cut: the next report says whether it worked. Growing in the
        // meantime would undo it before anything could confirm it.
        var estimator = Create(start: 4_000_000);

        var afterBackoff = estimator.ReportCongestion(4_000_000, "keyframe storm");

        for (var i = 0; i < 4; i++)
            estimator.Update(Sample(observedBps: afterBackoff, fresh: false));

        Assert.Equal(afterBackoff, estimator.CurrentBps);
    }

    [Fact]
    public void Reset_returns_to_a_starting_estimate()
    {
        // After a reconnect the path is unknown again, whatever was learned before.
        var estimator = Create();

        for (var i = 0; i < 20; i++)
            estimator.Update(Sample(observedBps: 30_000_000));

        estimator.Reset(2_000_000);

        Assert.Equal(2_000_000, estimator.CurrentBps);
    }

    [Fact]
    public void Congestion_then_recovery_settles_instead_of_oscillating()
    {
        // The shape that matters end to end: a link degrades, the controller backs off,
        // the link recovers, and the target climbs back without ringing.
        var estimator = Create(start: 4_000_000);

        // Ten seconds of a link dropping a fifth of everything, reported once per second.
        for (var i = 0; i < 10; i++)
            estimator.Update(Sample(observedBps: 4_000_000, lossFraction: 0.20));

        var trough = estimator.CurrentBps;
        Assert.True(trough < 2_000_000, $"expected a real back-off, got {trough}");
        Assert.True(trough >= Floor);

        // Then it clears, and the sender uses what it is given.
        for (var i = 0; i < 30; i++)
            estimator.Update(Sample(observedBps: estimator.CurrentBps));

        Assert.True(
            estimator.CurrentBps > trough * 2,
            $"expected recovery well above the trough of {trough}, got {estimator.CurrentBps}");

        Assert.True(estimator.CurrentBps <= Ceiling);
    }
}
