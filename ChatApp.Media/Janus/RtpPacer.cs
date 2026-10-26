using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Janus;

/// <summary>One frame's worth of RTP payloads, waiting to go out.</summary>
internal sealed record PacedFrame(IReadOnlyList<byte[]> Packets, uint Timestamp, int PayloadType);

/// <summary>
/// Spreads a frame's packets over time instead of pushing them at the socket together.
/// <para>
/// An encoder hands over a whole frame at once, and at 1080p a keyframe is a few hundred
/// packets. Sent back to back they arrive as a burst far above the link's rate, however
/// modest the average is: the queue somewhere along the path fills, packets are dropped,
/// and the receiver loses the frame — then asks for a keyframe, which arrives as another
/// burst. Average bitrate never explains it, which is why a link that looks fine can still
/// break a share whenever the picture moves.
/// </para>
/// <para>
/// A token bucket at the congestion controller's target, times a factor: pacing exactly at
/// the target would add a frame of delay whenever the encoder overshoots slightly, while
/// pacing well above it still breaks up the burst. libwebrtc uses 2.5 for the same reason.
/// </para>
/// </summary>
internal sealed class RtpPacer : IDisposable
{
    /// <summary>How much faster than the target the queue may drain.</summary>
    private const double PacingFactor = 2.5;

    /// <summary>Longest burst the bucket may accumulate while idle.</summary>
    private static readonly TimeSpan MaximumBurst = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Queue depth, in media time, past which frames are dropped. Video delayed by more
    /// than this is no longer worth sending — the conversation has moved on, and holding
    /// it only delays everything behind it.
    /// </summary>
    private static readonly TimeSpan MaximumQueue = TimeSpan.FromMilliseconds(500);

    private readonly Action<byte[], uint, bool, int> _send;
    private readonly ILogger _logger;
    private readonly Queue<PacedFrame> _queue = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    /// <summary>The tail of a frame the last tick ran out of budget for. Always sent first.</summary>
    private PacedFrame? _remainder;

    private double _budgetBits;
    private long _queuedBits;
    private long _droppedFrames;
    private int _targetBps = 1_000_000;
    private int _disposed;

    /// <summary>Frames dropped because the queue was too deep. A receiver will want a keyframe.</summary>
    public event Action? FramesDropped;

    public RtpPacer(Action<byte[], uint, bool, int> send, ILogger logger)
    {
        _send = send;
        _logger = logger;

        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "RTP pacer",

            // Above normal: late packets are the problem this exists to solve, and the
            // work per tick is a handful of sends.
            Priority = ThreadPriority.AboveNormal
        };

        _thread.Start();
    }

    /// <summary>The rate to pace at, from the send-side congestion controller.</summary>
    public int TargetBps
    {
        get => Volatile.Read(ref _targetBps);
        set => Volatile.Write(ref _targetBps, Math.Max(100_000, value));
    }

    /// <summary>Packets waiting, for diagnostics.</summary>
    public int QueuedFrames { get { lock (_gate) return _queue.Count; } }

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Queues one frame. Returns false when it was dropped rather than queued.</summary>
    public bool Enqueue(PacedFrame frame)
    {
        if (_disposed != 0 || frame.Packets.Count == 0)
            return false;

        var bits = 0L;
        foreach (var packet in frame.Packets)
            bits += packet.Length * 8L;

        var dropped = 0;

        lock (_gate)
        {
            _queue.Enqueue(frame);
            _queuedBits += bits;

            // Drop whole frames, oldest first: half a frame is worth nothing to a decoder,
            // and the newest frames are the ones still worth showing.
            var limit = (long)(TargetBps * MaximumQueue.TotalSeconds);

            while (_queue.Count > 1 && _queuedBits > limit)
            {
                var stale = _queue.Dequeue();
                dropped++;

                foreach (var packet in stale.Packets)
                    _queuedBits -= packet.Length * 8L;
            }
        }

        if (dropped == 0)
            return true;

        Interlocked.Add(ref _droppedFrames, dropped);

        _logger.LogWarning(
            "Pacer queue is over {Limit}ms at {Kbps} kbps: dropped {Count} frame(s). " +
            "The encoder is producing more than the link was measured to carry.",
            MaximumQueue.TotalMilliseconds, TargetBps / 1000, dropped);

        FramesDropped?.Invoke();
        return true;
    }

    /// <summary>Empties the queue, e.g. when the share stops or the codec changes.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _remainder = null;
            _queuedBits = 0;
            _budgetBits = 0;
        }
    }

    private void Run(CancellationToken cancellationToken)
    {
        // Fine-grained: at 2.5 Mbps a 1100-byte packet is worth about 3.5ms of budget, so
        // a coarser tick would send them in clumps and defeat the point.
        var tick = TimeSpan.FromMilliseconds(2);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(tick);

                var now = clock.Elapsed;
                var elapsed = now - last;
                last = now;

                DrainOnce(elapsed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The RTP pacer stopped; video will not be sent.");
        }
    }

    private void DrainOnce(TimeSpan elapsed)
    {
        var target = TargetBps;
        var allowance = target * PacingFactor;

        List<(byte[] Packet, uint Timestamp, bool Marker, int PayloadType)>? ready = null;

        lock (_gate)
        {
            _budgetBits += allowance * elapsed.TotalSeconds;

            // Idle time must not bank a burst: that is the very thing being smoothed.
            var ceiling = allowance * MaximumBurst.TotalSeconds;
            if (_budgetBits > ceiling)
                _budgetBits = ceiling;

            while (_remainder is not null || _queue.Count > 0)
            {
                // The tail of a frame the previous tick could not finish always goes
                // first: order within a stream is not negotiable.
                var frame = _remainder ?? _queue.Peek();
                var fromRemainder = _remainder is not null;
                _remainder = null;

                var sent = 0;

                foreach (var packet in frame.Packets)
                {
                    var bits = packet.Length * 8;
                    if (_budgetBits < bits)
                        break;

                    _budgetBits -= bits;
                    _queuedBits -= bits;
                    sent++;

                    ready ??= [];
                    ready.Add((
                        packet,
                        frame.Timestamp,
                        Marker: sent == frame.Packets.Count,
                        frame.PayloadType));
                }

                if (sent < frame.Packets.Count)
                {
                    // Partly sent: the rest goes next tick, and it must stay at the FRONT.
                    // Re-queueing it would put the tail of this frame behind every frame
                    // already waiting, so a receiver would get the start of one frame, all
                    // of the next few, then the end of the first — with timestamps going
                    // backwards. Nothing downstream recovers from that.
                    _remainder = frame with { Packets = frame.Packets.Skip(sent).ToArray() };

                    if (!fromRemainder)
                        _queue.Dequeue();

                    break;
                }

                if (!fromRemainder)
                    _queue.Dequeue();
            }
        }

        if (ready is null)
            return;

        // Outside the lock: sending goes through SRTP and the socket.
        foreach (var (packet, timestamp, marker, payloadType) in ready)
        {
            try
            {
                _send(packet, timestamp, marker, payloadType);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A paced RTP packet could not be sent.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _cts.Cancel();

        if (!_thread.Join(TimeSpan.FromMilliseconds(500)))
            _logger.LogDebug("The RTP pacer thread did not stop promptly.");

        _cts.Dispose();
        Clear();
    }
}
