using ChatApp.Media.Janus;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatApp.Media.Tests;

/// <summary>
/// The pacer exists because an encoder hands over a whole frame at once and the socket
/// will happily send all of it immediately. At 1080p a keyframe is hundreds of packets;
/// back to back they are a burst far above the link's rate whatever the average says.
/// </summary>
public class RtpPacerTests
{
    private sealed record Sent(int Length, uint Timestamp, bool Marker, int PayloadType);

    private static PacedFrame Frame(int packets, int bytesEach = 1100, uint timestamp = 1000)
        => new(
            Enumerable.Range(0, packets).Select(_ => new byte[bytesEach]).ToArray(),
            timestamp,
            PayloadType: 96);

    private static async Task<List<Sent>> DrainAsync(
        Action<RtpPacer> use, int targetBps, TimeSpan allow)
    {
        var sent = new List<Sent>();
        var gate = new Lock();

        using var pacer = new RtpPacer(
            (packet, timestamp, marker, payloadType) =>
            {
                lock (gate)
                    sent.Add(new Sent(packet.Length, timestamp, marker, payloadType));
            },
            NullLogger.Instance) { TargetBps = targetBps };

        use(pacer);
        await Task.Delay(allow);

        lock (gate)
            return [.. sent];
    }

    [Fact]
    public async Task A_burst_is_spread_rather_than_sent_at_once()
    {
        // 200 packets of 1100 bytes is 1.76 Mbit. At 2 Mbps paced 2.5x, about 350ms of
        // work — so a 120ms window must not have carried all of it.
        var sent = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 200)),
            targetBps: 2_000_000,
            allow: TimeSpan.FromMilliseconds(120));

        Assert.NotEmpty(sent);
        Assert.True(sent.Count < 200, $"the whole burst went out at once ({sent.Count} packets)");
    }

    [Fact]
    public async Task Everything_queued_does_eventually_go_out()
    {
        // Spreading must not mean losing: a frame within the queue limit is sent in full.
        var sent = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 40)),
            targetBps: 10_000_000,
            allow: TimeSpan.FromMilliseconds(300));

        Assert.Equal(40, sent.Count);
    }

    [Fact]
    public async Task The_marker_lands_on_the_last_packet_of_a_frame_and_nowhere_else()
    {
        // The marker is how a receiver knows the access unit is complete. Set early, it
        // hands a decoder half a frame.
        var sent = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 25, timestamp: 4242)),
            targetBps: 10_000_000,
            allow: TimeSpan.FromMilliseconds(300));

        Assert.Equal(25, sent.Count);
        Assert.All(sent, s => Assert.Equal(4242u, s.Timestamp));
        Assert.All(sent.Take(24), s => Assert.False(s.Marker));
        Assert.True(sent[^1].Marker, "the last packet of the frame carried no marker");
    }

    [Fact]
    public async Task A_frame_split_across_ticks_still_ends_with_exactly_one_marker()
    {
        // The interesting case: the budget runs out mid-frame, so the rest goes next tick.
        var sent = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 150)),
            targetBps: 3_000_000,
            allow: TimeSpan.FromMilliseconds(600));

        Assert.Equal(150, sent.Count);
        Assert.Equal(1, sent.Count(s => s.Marker));
        Assert.True(sent[^1].Marker, "the marker was not on the final packet");
    }

    [Fact]
    public async Task Frames_leave_in_the_order_they_arrived_even_when_split_across_ticks()
    {
        // The bug this exists for: when the budget ran out mid-frame, the remainder was
        // re-queued at the BACK. A receiver then got the start of one frame, all of the
        // next few, and the end of the first — timestamps going backwards. Nothing
        // downstream recovers from that, which is why it presented as a share that worked
        // for a few seconds and then never came back.
        var sent = await DrainAsync(
            pacer =>
            {
                for (uint i = 0; i < 6; i++)
                    pacer.Enqueue(Frame(packets: 60, timestamp: 1000 + i));
            },
            targetBps: 4_000_000,
            allow: TimeSpan.FromMilliseconds(700));

        Assert.NotEmpty(sent);

        // Timestamps must never go backwards, and a frame must never be interleaved with
        // another one.
        var order = sent.Select(s => s.Timestamp).ToArray();

        for (var i = 1; i < order.Length; i++)
            Assert.True(order[i] >= order[i - 1], $"timestamp went backwards at packet {i}");

        var distinct = order.Distinct().ToArray();
        Assert.Equal(distinct, distinct.OrderBy(t => t).ToArray());
        Assert.Equal(distinct.Length, order.Chunk(1).Select(c => c[0]).Distinct().Count());
    }

    [Fact]
    public async Task A_queue_that_runs_away_drops_whole_frames_and_says_so()
    {
        // Half a frame is worth nothing to a decoder, so the drop has to be by frame — and
        // whoever is listening needs to know a keyframe is owed.
        var dropped = 0;

        var sent = await DrainAsync(
            pacer =>
            {
                pacer.FramesDropped += () => Interlocked.Increment(ref dropped);

                // Far more than 500ms of media at this rate.
                for (var i = 0; i < 30; i++)
                    pacer.Enqueue(Frame(packets: 100, timestamp: (uint)(1000 + i)));
            },
            targetBps: 1_000_000,
            allow: TimeSpan.FromMilliseconds(200));

        Assert.True(dropped > 0, "the queue grew without bound instead of dropping");

        // Whatever did go out was whole frames, so no frame is half-sent.
        foreach (var group in sent.GroupBy(s => s.Timestamp))
            Assert.True(group.Count(s => s.Marker) <= 1, "a frame was sent twice or split across timestamps");
    }

    [Fact]
    public async Task Raising_the_target_drains_faster()
    {
        var slow = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 300)),
            targetBps: 1_000_000,
            allow: TimeSpan.FromMilliseconds(150));

        var fast = await DrainAsync(
            pacer => pacer.Enqueue(Frame(packets: 300)),
            targetBps: 20_000_000,
            allow: TimeSpan.FromMilliseconds(150));

        Assert.True(
            fast.Count > slow.Count * 2,
            $"pacing rate had little effect: {slow.Count} slow vs {fast.Count} fast");
    }

    [Fact]
    public void Disposing_stops_the_thread_without_throwing()
    {
        var pacer = new RtpPacer((_, _, _, _) => { }, NullLogger.Instance);

        pacer.Enqueue(Frame(packets: 500));
        pacer.Dispose();
        pacer.Dispose();
    }
}
