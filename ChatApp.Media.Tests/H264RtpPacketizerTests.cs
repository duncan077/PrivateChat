using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// We packetize H.264 ourselves so the packets can be paced rather than burst at the
/// socket. Getting RFC 6184 wrong does not fail loudly — it produces a stream that no
/// receiver can decode, which looks exactly like the network problem it was meant to fix.
/// </summary>
public class H264RtpPacketizerTests
{
    private const int Mtu = 1100;

    private static byte[] Nal(byte header, int payloadLength, byte fill = 0xAB)
    {
        var nal = new byte[1 + payloadLength];
        nal[0] = header;
        nal.AsSpan(1).Fill(fill);
        return nal;
    }

    private static byte[] AnnexB(params byte[][] nals)
    {
        var stream = new List<byte>();

        foreach (var nal in nals)
        {
            stream.AddRange([0, 0, 0, 1]);
            stream.AddRange(nal);
        }

        return [.. stream];
    }

    [Fact]
    public void A_small_nal_becomes_one_packet_carrying_it_unchanged()
    {
        // Single NAL unit mode: the payload is the NAL itself, start code stripped.
        var nal = Nal(0x65, 200);

        var packets = H264RtpPacketizer.Packetize(AnnexB(nal), Mtu);

        Assert.Single(packets);
        Assert.Equal(nal, packets[0]);
    }

    [Fact]
    public void Three_and_four_byte_start_codes_are_both_understood()
    {
        // Real encoders emit both, often inside one access unit.
        var stream = new List<byte> { 0, 0, 0, 1, 0x67, 0x11, 0x22, 0, 0, 1, 0x68, 0x33 };

        var packets = H264RtpPacketizer.Packetize([.. stream], Mtu);

        Assert.Equal(2, packets.Count);
        Assert.Equal([0x67, 0x11, 0x22], packets[0]);
        Assert.Equal([0x68, 0x33], packets[1]);
    }

    [Fact]
    public void Every_nal_of_an_access_unit_gets_its_own_packet()
    {
        var packets = H264RtpPacketizer.Packetize(
            AnnexB(Nal(0x67, 10), Nal(0x68, 5), Nal(0x65, 300)), Mtu);

        Assert.Equal(3, packets.Count);
        Assert.Equal(0x67, packets[0][0]);
        Assert.Equal(0x68, packets[1][0]);
        Assert.Equal(0x65, packets[2][0]);
    }

    [Fact]
    public void A_large_nal_is_fragmented_and_reassembles_to_the_original()
    {
        // The case that matters: a keyframe is far larger than any MTU, and a receiver has
        // to be able to put it back together byte for byte.
        var nal = Nal(0x65, 10_000);

        var packets = H264RtpPacketizer.Packetize(AnnexB(nal), Mtu);

        Assert.True(packets.Count > 1, "a 10 KB NAL was not fragmented");
        Assert.All(packets, p => Assert.True(p.Length <= Mtu, $"packet of {p.Length} exceeds the MTU"));

        var reassembled = new List<byte>();

        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];

            var indicator = packet[0];
            var fuHeader = packet[1];

            // FU-A: indicator type 28, the original NRI preserved, F never set here.
            Assert.Equal(28, indicator & 0x1F);
            Assert.Equal(0x65 & 0xE0, indicator & 0xE0);
            Assert.Equal(0x65 & 0x1F, fuHeader & 0x1F);

            var start = (fuHeader & 0x80) != 0;
            var end = (fuHeader & 0x40) != 0;

            Assert.Equal(i == 0, start);
            Assert.Equal(i == packets.Count - 1, end);

            if (start)
            {
                // Rebuild the original header from the two FU bytes.
                reassembled.Add((byte)((indicator & 0xE0) | (fuHeader & 0x1F)));
            }

            reassembled.AddRange(packet.AsSpan(2).ToArray());
        }

        Assert.Equal(nal, reassembled);
    }

    [Fact]
    public void A_nal_exactly_at_the_limit_is_not_fragmented()
    {
        var nal = Nal(0x41, Mtu - 1);

        var packets = H264RtpPacketizer.Packetize(AnnexB(nal), Mtu);

        Assert.Single(packets);
        Assert.Equal(Mtu, packets[0].Length);
    }

    [Fact]
    public void One_byte_over_the_limit_fragments_into_exactly_two()
    {
        var nal = Nal(0x41, Mtu);

        var packets = H264RtpPacketizer.Packetize(AnnexB(nal), Mtu);

        Assert.Equal(2, packets.Count);
    }

    [Fact]
    public void A_frame_of_many_nals_keeps_them_in_order()
    {
        // Order is the whole contract: a decoder fed parameter sets after the slice they
        // describe has nothing to decode with.
        var nals = Enumerable.Range(0, 20).Select(i => Nal(0x41, 50, (byte)i)).ToArray();

        var packets = H264RtpPacketizer.Packetize(AnnexB(nals), Mtu);

        Assert.Equal(20, packets.Count);

        for (var i = 0; i < packets.Count; i++)
            Assert.Equal((byte)i, packets[i][1]);
    }

    [Fact]
    public void Nonsense_produces_nothing_rather_than_throwing()
    {
        Assert.Empty(H264RtpPacketizer.Packetize([], Mtu));
        Assert.Empty(H264RtpPacketizer.Packetize([0, 0, 0, 1], Mtu));
        Assert.Empty(H264RtpPacketizer.Packetize([0xAA, 0xBB], Mtu));
    }
}
