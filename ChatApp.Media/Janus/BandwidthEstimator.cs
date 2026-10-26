namespace ChatApp.Media.Janus;

/// <summary>What one control interval actually saw.</summary>
public readonly record struct BandwidthSample
{
    /// <summary>Bits per second observed over the interval — received, or sent.</summary>
    public required int ObservedBps { get; init; }

    /// <summary>Packets lost over the interval as a fraction of those expected, 0..1.</summary>
    public required double LossFraction { get; init; }

    /// <summary>RTCP interarrival jitter, converted to milliseconds.</summary>
    public required double JitterMs { get; init; }

    /// <summary>
    /// Time since the previous sample. Growth past what the link has been shown to carry
    /// is paced by this rather than by sample count: reception reports arrive whenever the
    /// far end feels like sending RTCP, which is often enough that counting them let the
    /// target climb 8% a second straight back into the wall it had just been cut from.
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Whether the loss and jitter figures are new since the last update.
    /// <para>
    /// RTCP reception reports arrive every few seconds while the controller runs every
    /// second, so the same numbers are seen several times. Without this flag one bad
    /// report would be acted on repeatedly and the target would fall far further than the
    /// loss measured justified. A stale report still blocks growth — loss that has not
    /// been refuted is loss — it just cannot force a second cut.
    /// </para>
    /// </summary>
    public required bool ReportIsFresh { get; init; }
}

/// <summary>What the estimator did with the last sample.</summary>
public enum BandwidthAction
{
    Hold,
    Increase,
    Decrease
}

/// <summary>
/// The congestion controller: turns loss and jitter into a bitrate target.
/// <para>
/// Additive-increase/multiplicative-decrease, following the loss-based rules from
/// draft-alvestrand-rmcat-remb (which is what browsers run when they speak REMB): clean
/// paths grow 8% per interval, 2–10% loss holds station, and past 10% the target is cut
/// in proportion to the loss actually measured. Jitter is a second, weaker signal — a
/// queue filling up shows as rising delay before it shows as loss, but RTCP jitter is
/// noisy enough that it only suppresses growth, and only forces a cut when it is both
/// extreme and still climbing.
/// </para>
/// <para>
/// This is deliberately not transport-wide congestion control. TWCC needs per-packet
/// arrival feedback and a delay-gradient filter; what this has is the loss and jitter
/// already carried by RTCP. The difference shows up as slower reaction to a link that
/// degrades without dropping anything, which is the trade the plan accepted.
/// </para>
/// <para>
/// Not thread-safe: <see cref="Update"/> is expected to be called from one place.
/// <see cref="CurrentBps"/> may be read from anywhere — a stale value in a log line does
/// no harm.
/// </para>
/// </summary>
public sealed class BandwidthEstimator
{
    /// <summary>Growth per interval on a clean path. The rate browsers use.</summary>
    private const double IncreaseFactor = 1.08;

    /// <summary>
    /// Growth while climbing through rates the path has already been shown to carry, and
    /// before it has shown any limit at all.
    /// <para>
    /// 8% per interval is the right caution when approaching an unknown ceiling, but it is
    /// pointless timidity on the way back to a rate that was working seconds ago: at 8% a
    /// step, recovering from a dip took over a minute of visibly soft picture. Browsers
    /// solve this by probing with transport-wide CC, which needs per-packet feedback we do
    /// not have; recovering fast to the last known good rate and slowing down near it gets
    /// most of the same behaviour from the feedback we do have.
    /// </para>
    /// </summary>
    private const double FastIncreaseFactor = 1.5;

    /// <summary>
    /// Where fast recovery gives way to caution, as a share of the rate the link was last
    /// measured to carry. Beyond it, nothing is known again.
    /// </summary>
    private const double RecoveryThreshold = 0.85;

    /// <summary>
    /// Above this, cut. Below <see cref="LossIgnorable"/>, grow. Between, hold.
    /// <para>
    /// Browsers hold until 10%, but they pair the loss controller with a delay-gradient
    /// one that reacts long before loss appears. With loss as the only dependable signal,
    /// sitting at the browser's threshold means sitting at 10% loss — a visibly broken
    /// picture — so the cut comes earlier here. Being more cautious than a browser is the
    /// right trade when you know less than one does.
    /// </para>
    /// </summary>
    private const double LossSerious = 0.05;

    private const double LossIgnorable = 0.02;

    /// <summary>Cut to this share of what is flowing when delay says the queue is filling.</summary>
    private const double DelayDecreaseFactor = 0.85;

    /// <summary>
    /// The furthest a single cut may go, as a share of the current target.
    /// <para>
    /// A cut is sized from what was flowing, and what was flowing is measured over a
    /// window that may have caught the encoder idling — a still moment in a screen share
    /// is a fraction of a busy one. Without a bound, one such sample takes a 10 Mbps
    /// target to 800 kbps in a single step and the picture collapses far below anything
    /// the link asked for. Halving per report still converges on a genuinely bad link
    /// within a few seconds, without betting everything on one measurement.
    /// </para>
    /// </summary>
    private const double MaximumSingleDecrease = 0.5;

    // Growth is capped relative to what is actually flowing: granting 20 Mbps to a sender
    // using 200 kbps buys nothing, and would let the target drift far above anything the
    // path has been shown to carry. The cap never lowers the target, so a sender that is
    // simply idle (a still screen share) does not ratchet its own ceiling down.
    private const double IncreaseHeadroomFactor = 1.5;
    private const int IncreaseHeadroomBps = 10_000;

    /// <summary>Jitter at which growth stops. Healthy conferencing video sits well under this.</summary>
    private const double JitterCautionMs = 30;

    /// <summary>Jitter at which, if still climbing, the target is cut.</summary>
    private const double JitterOveruseMs = 100;

    /// <summary>Consecutive climbing samples before jitter is believed.</summary>
    private const int RisingJitterSamples = 3;

    /// <summary>
    /// How long to wait between steps that push past the rate the link has been measured
    /// to carry.
    /// <para>
    /// Below that rate there is evidence and recovery can be quick. Above it there is
    /// none, and each step has to be given time to be contradicted — a step per second is
    /// eight or ten steps before the first of them is answered, which is how a controller
    /// ends up sawtoothing between twice the capacity and half of it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CautiousGrowthInterval = TimeSpan.FromSeconds(5);

    /// <summary>Growth in jitter that counts as "climbing" rather than noise.</summary>
    private const double JitterRiseMs = 5;

    /// <summary>
    /// How long the target sits still after a receiver reports it has stopped decoding.
    /// <para>
    /// Long enough to cover several reports, so the hold ends on evidence the reduced rate
    /// is working rather than on a timer that happens to expire first.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ReceiverHold = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How far below a rate that broke a viewer the ceiling is set.
    /// <para>
    /// The failing rate is an upper bound on what the path carries, not a measurement of
    /// it, so the ceiling sits under it. Two storms are then enough to converge: a share
    /// that breaks at 8 Mbps is capped at 6.8, breaks again around 6 and is capped at 5.1
    /// — which is where the path actually lives.
    /// </para>
    /// </summary>
    private const double ReceiverCeilingMargin = 0.85;

    /// <summary>
    /// How far the target drops when a viewer says it has stopped decoding.
    /// <para>
    /// Deeper than the ceiling that same failure sets, and deliberately so. Cutting only
    /// to the ceiling would land exactly on it, leaving the controller pinned there with
    /// nowhere to converge from and no way to tell whether the ceiling was too generous.
    /// Dropping below it clears the backlog faster — which is what un-freezes the picture
    /// — and leaves a band to climb back through, so the rate settles just under the wall
    /// instead of on it.
    /// </para>
    /// </summary>
    private const double ReceiverDecreaseFactor = 0.70;

    private double _previousJitterMs;
    private int _risingJitterRun;
    private bool _slowStart = true;
    private int _capacityBps;
    private int _growthCeilingBps;
    private TimeSpan _sinceGrowth;
    private TimeSpan _receiverHold;
    private int _receiverCeilingBps;

    /// <summary>True until the path first pushes back.</summary>
    public bool IsSlowStarting => _slowStart;

    /// <summary>
    /// What the link was actually carrying when it last pushed back — not the target that
    /// broke, which was by definition more than it could take. Climbing back toward this
    /// is quick, since the link carried it moments ago; climbing past it is not.
    /// </summary>
    public int CapacityBps => _capacityBps;

    /// <summary>
    /// The most this call may send, learned from viewers rather than from reception
    /// reports.
    /// <para>
    /// Reception reports come from the SFU and describe only the leg up to it — a server
    /// on a good link, which reads clean while a viewer beyond it is being flooded. The
    /// one thing a viewer itself ever tells us is that it has stopped decoding, and that
    /// arrives as a burst of keyframe requests. Treating it as a passing problem is what
    /// let the target climb back to a rate already known to fail, freezing the picture
    /// each time round; treating it as a ceiling makes the call converge on what the path
    /// carries and stay there.
    /// </para>
    /// <para>
    /// The ceiling only ever comes down during a call. Probing back up would mean
    /// rediscovering the wall, and rediscovering it costs a frozen picture — a permanently
    /// slightly-lower bitrate is the cheaper mistake. A reconnect clears it, since that may
    /// well be a different path.
    /// </para>
    /// </summary>
    public int ReceiverCeilingBps => _receiverCeilingBps;

    public int MinimumBps { get; }

    /// <summary>The configured ceiling. Never exceeded, however clean the path looks.</summary>
    public int MaximumBps { get; private set; }

    public int CurrentBps { get; private set; }

    public BandwidthAction LastAction { get; private set; } = BandwidthAction.Hold;

    /// <summary>Why the last decision was taken, for the health log.</summary>
    public string LastReason { get; private set; } = "no samples yet";

    public BandwidthEstimator(int startBps, int minimumBps, int maximumBps)
    {
        if (minimumBps <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBps));

        MinimumBps = minimumBps;
        MaximumBps = Math.Max(maximumBps, minimumBps);
        CurrentBps = Math.Clamp(startBps, MinimumBps, MaximumBps);

        // Nothing has pushed back yet, so the whole configured range counts as territory
        // worth climbing quickly.
        _capacityBps = MaximumBps;
        _growthCeilingBps = MaximumBps;
        _receiverCeilingBps = MaximumBps;
    }

    /// <summary>
    /// Re-caps the estimator, e.g. after the user changes the configured bitrate. Lowering
    /// the ceiling takes effect at once; raising it only permits future growth.
    /// </summary>
    public void SetCeiling(int maximumBps)
    {
        MaximumBps = Math.Max(maximumBps, MinimumBps);
        CurrentBps = Math.Min(CurrentBps, MaximumBps);
    }

    /// <summary>
    /// Reports congestion observed outside RTCP, and backs off as if a report had said so.
    /// <para>
    /// Reception reports cover the leg between us and the SFU and take seconds to arrive.
    /// A receiver that has stopped decoding says so immediately, and says it about the
    /// whole path — including the part beyond the SFU that our own reports structurally
    /// cannot see. Waiting for RTCP to agree means seconds of sending into a link that is
    /// already dropping the stream.
    /// </para>
    /// </summary>
    public int ReportCongestion(int observedBps, string reason)
    {
        var flowing = observedBps > 0 ? Math.Min(CurrentBps, observedBps) : CurrentBps;

        _slowStart = false;

        LastAction = BandwidthAction.Decrease;
        LastReason = reason;

        // The rate that broke a viewer is an upper bound on the whole path, and the only
        // one anyone ever gives us. It ratchets down and does not come back within the
        // call: see ReceiverCeilingBps.
        var failing = observedBps > 0 ? observedBps : CurrentBps;

        _receiverCeilingBps = Math.Max(
            MinimumBps,
            Math.Min(_receiverCeilingBps, (int)Math.Round(failing * ReceiverCeilingMargin)));

        CurrentBps = Math.Clamp(
            (int)Math.Round(Floor(flowing * ReceiverDecreaseFactor)), MinimumBps, Ceiling());

        // The two signals mean opposite things about the rate they arrive at. A reception
        // report saying a rate got through is evidence the path carries it, so recording
        // it as capacity and climbing back is right. A receiver that has stopped decoding
        // is saying that rate did NOT work — filing it as capacity is how the target
        // climbs straight back into the wall it just hit. The reduced rate is the most
        // that can be believed now, and even that is a hope rather than a measurement.
        _capacityBps = CurrentBps;

        // A receiver going quiet is a far stronger signal than a few percent of loss, and
        // the ordinary five-second pause before growing again is not enough to respect it:
        // two 8% steps put the target back at the failing rate inside fifteen seconds, and
        // the whole cycle repeats with the picture frozen each time. Sitting still for
        // longer costs a little quality on a path that has recovered, and saves a
        // rediscovery of the same wall on one that has not.
        _receiverHold = ReceiverHold;

        // As with any cut: nothing more until a report says the cut worked.
        _growthCeilingBps = CurrentBps;

        return CurrentBps;
    }

    /// <summary>Returns to a starting target — after a reconnect, the path is unknown again.</summary>
    public void Reset(int startBps)
    {
        CurrentBps = Math.Clamp(startBps, MinimumBps, MaximumBps);
        _previousJitterMs = 0;
        _risingJitterRun = 0;

        // A reconnect may well be a different path; what the old one could carry is not
        // evidence about this one.
        _slowStart = true;
        _capacityBps = MaximumBps;
        _growthCeilingBps = MaximumBps;
        _receiverCeilingBps = MaximumBps;
        _sinceGrowth = TimeSpan.Zero;
        _receiverHold = TimeSpan.Zero;
        LastAction = BandwidthAction.Hold;
        LastReason = "reset";
    }

    /// <summary>Folds one interval's observation into the target and returns the new one.</summary>
    public int Update(in BandwidthSample sample)
    {
        // Only a fresh report moves the trend: repeats of the same value would otherwise
        // reset the run on every tick between reports, and a genuine climb spread across
        // reports could never accumulate.
        if (sample.ReportIsFresh)
            UpdateJitterTrend(sample.JitterMs);

        _sinceGrowth += sample.Elapsed;

        if (_receiverHold > TimeSpan.Zero)
            _receiverHold -= sample.Elapsed;

        // A fresh report renews the growth allowance for the interval it opens.
        if (sample.ReportIsFresh)
            _growthCeilingBps = NextGrowthCeiling();

        var climbing = _risingJitterRun >= RisingJitterSamples;
        double target;

        if (sample.LossFraction > LossSerious && !sample.ReportIsFresh)
        {
            // Already acted on. Cutting again for the same measurement is how a single bad
            // second turns into a collapse the path never asked for.
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = $"{sample.LossFraction:P1} loss, already acted on";
        }
        else if (sample.LossFraction > LossSerious)
        {
            // Back off from what actually got through, not from what was hoped for. This
            // is the difference between converging below the link's capacity and settling
            // just above it, where the controller would hold happily at a steady few
            // percent loss forever. libwebrtc does the same.
            var delivered = sample.ObservedBps > 0
                ? Math.Min(CurrentBps, sample.ObservedBps)
                : CurrentBps;

            target = Floor(delivered * (1 - 0.5 * sample.LossFraction));
            LastAction = BandwidthAction.Decrease;
            LastReason = $"{sample.LossFraction:P1} loss";
        }
        else if (climbing && sample.JitterMs >= JitterOveruseMs)
        {
            // Nothing is being dropped yet, but the queue is deep and still filling. Cut
            // below what is currently flowing so the queue has a chance to drain.
            var flowing = sample.ObservedBps > 0 ? Math.Min(CurrentBps, sample.ObservedBps) : CurrentBps;
            target = Floor(flowing * DelayDecreaseFactor);
            LastAction = BandwidthAction.Decrease;
            LastReason = $"jitter {sample.JitterMs:F0}ms and climbing";
        }
        else if (sample.ObservedBps <= 0)
        {
            // Nothing is flowing, so nothing has been learned. Holding matters: a muted
            // camera must not leave the target on the floor for when it comes back.
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = "nothing flowing";
        }
        else if (sample.LossFraction >= LossIgnorable)
        {
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = $"{sample.LossFraction:P1} loss, holding";
        }
        else if (sample.JitterMs >= JitterCautionMs)
        {
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = $"jitter {sample.JitterMs:F0}ms, not growing";
        }
        else if (_receiverHold > TimeSpan.Zero)
        {
            // A receiver said it could not decode what we were sending. Loss reports come
            // from the SFU and cover only the leg up to it, so a clean one here says
            // nothing at all about the leg that just failed — growing on the strength of
            // it is growing on evidence from the wrong half of the path.
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = $"holding {_receiverHold.TotalSeconds:F0}s more after a receiver stopped decoding";
        }
        else if (!(_slowStart || CurrentBps < _capacityBps * RecoveryThreshold)
                 && _sinceGrowth < CautiousGrowthInterval)
        {
            // Above what the link has carried, and the last step has not been answered
            // yet. Stepping again now is how the target races back to a rate already known
            // to fail.
            target = CurrentBps;
            LastAction = BandwidthAction.Hold;
            LastReason = "clean, letting the last step be answered before pushing higher";
        }
        else
        {
            // Fast while below a rate this link has already been measured to carry,
            // cautious once level with it — recovering to a known operating point is not
            // the same risk as discovering a new one, and a fast step is clamped so it
            // cannot leap past what was measured.
            var recovering = _slowStart || CurrentBps < _capacityBps * RecoveryThreshold;

            var raised = recovering
                ? Math.Min(CurrentBps * FastIncreaseFactor, Math.Max(_capacityBps, CurrentBps))
                : CurrentBps * IncreaseFactor;

            var headroom = sample.ObservedBps * IncreaseHeadroomFactor + IncreaseHeadroomBps;

            // Growth is rationed per report, not per tick: this loop runs faster than the
            // feedback that steers it, and without the ration a few ticks would compound
            // several times over before anything could contradict them.
            var permitted = Math.Max(CurrentBps, _growthCeilingBps);

            target = Math.Max(CurrentBps, Math.Min(Math.Min(raised, headroom), permitted));

            LastAction = target > CurrentBps ? BandwidthAction.Increase : BandwidthAction.Hold;
            LastReason = LastAction == BandwidthAction.Increase
                ? recovering ? "path is clean, climbing fast" : "path is clean"
                : "clean, waiting for the next report before growing again";
        }

        if (LastAction == BandwidthAction.Decrease)
        {
            // What the link actually carried, which is the only measurement of it we get.
            _capacityBps = sample.ObservedBps > 0 ? sample.ObservedBps : CurrentBps;

            // The first sign of a limit ends slow start for the rest of the call.
            _slowStart = false;
        }

        CurrentBps = Math.Clamp((int)Math.Round(target), MinimumBps, Ceiling());

        // A cut authorises nothing further until the next report says whether it worked,
        // and restarts the clock that paces stepping past the measured capacity.
        if (LastAction is BandwidthAction.Decrease or BandwidthAction.Increase)
            _sinceGrowth = TimeSpan.Zero;

        if (LastAction == BandwidthAction.Decrease)
            _growthCeilingBps = CurrentBps;

        return CurrentBps;
    }

    /// <summary>
    /// The most the target may reach before the next report — one step's worth of growth,
    /// however many ticks pass in the meantime.
    /// <para>
    /// The opening allowance is the whole range (see the constructor), because no report
    /// has arrived yet to ration against and the estimator is expected to start just below
    /// its ceiling: the most that can be overshot before the first feedback is that
    /// opening gap.
    /// </para>
    /// </summary>
    private int NextGrowthCeiling()
    {
        var fast = _slowStart || CurrentBps < _capacityBps * RecoveryThreshold;
        return (int)Math.Round(CurrentBps * (fast ? FastIncreaseFactor : IncreaseFactor));
    }

    /// <summary>
    /// The effective maximum: the configured ceiling, or whatever a viewer has shown to be
    /// less, whichever is lower. Never below the floor, so a badly behaved viewer cannot
    /// clamp the call to nothing.
    /// </summary>
    private int Ceiling() => Math.Max(MinimumBps, Math.Min(MaximumBps, _receiverCeilingBps));

    /// <summary>Keeps one cut from falling further than <see cref="MaximumSingleDecrease"/>.</summary>
    private double Floor(double target) => Math.Max(target, CurrentBps * MaximumSingleDecrease);

    private void UpdateJitterTrend(double jitterMs)
    {
        if (jitterMs > _previousJitterMs + JitterRiseMs)
            _risingJitterRun++;
        else
            _risingJitterRun = 0;

        _previousJitterMs = jitterMs;
    }
}
