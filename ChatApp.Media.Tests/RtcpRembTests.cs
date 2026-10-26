using System.Buffers.Binary;
using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Byte-level checks on the REMB packet.
/// <para>
/// A malformed REMB is discarded silently by the far end, so the only symptom would be
/// the bitrate never rising — indistinguishable from not sending one at all.
/// </para>
/// </summary>
public class RtcpRembTests
{
    [Fact]
    public void Header_matches_the_psfb_application_layer_format()
    {
        var packet = RtcpRemb.Build(senderSsrc: 0x11223344, mediaSsrc: 0x55667788, bitrateBps: 2_000_000);

        Assert.Equal(24, packet.Length);

        // Version 2, no padding, FMT 15 (application-layer feedback).
        Assert.Equal(2, packet[0] >> 6);
        Assert.Equal(0, (packet[0] >> 5) & 1);
        Assert.Equal(15, packet[0] & 0x1F);

        // PT 206 = payload-specific feedback.
        Assert.Equal(206, packet[1]);

        // Length counts 32-bit words minus one.
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
    }

    [Fact]
    public void Carries_the_ssrcs_and_the_remb_identifier()
    {
        var packet = RtcpRemb.Build(senderSsrc: 0x11223344, mediaSsrc: 0x55667788, bitrateBps: 1_000_000);

        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));

        // The media source field is always zero in REMB; the estimated streams are listed
        // at the end. A receiver that fills this in has its packet ignored.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));

        Assert.Equal("REMB", System.Text.Encoding.ASCII.GetString(packet, 12, 4));

        Assert.Equal(1, packet[16]);
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(20)));
    }

    [Theory]
    [InlineData(100_000u)]
    [InlineData(262_143u)]   // largest value that still fits the mantissa unshifted
    [InlineData(262_144u)]   // first value that forces an exponent
    [InlineData(2_000_000u)]
    [InlineData(4_000_000u)]
    [InlineData(50_000_000u)]
    public void Bitrate_survives_the_exponent_mantissa_split(uint bitrate)
    {
        var (exponent, mantissa) = RtcpRemb.Encode(bitrate);

        Assert.InRange(exponent, 0, 63);
        Assert.InRange(mantissa, 0u, 0x3FFFFu);

        // Quantisation only ever discards low bits, so the result is within one step of
        // the requested rate and never above it.
        var decoded = RtcpRemb.Decode(exponent, mantissa);
        var step = 1u << exponent;

        Assert.True(decoded <= bitrate, $"{decoded} exceeded the requested {bitrate}");
        Assert.True(bitrate - decoded < step, $"{decoded} is more than one step below {bitrate}");
    }

    [Fact]
    public void Small_bitrates_need_no_exponent()
    {
        var (exponent, mantissa) = RtcpRemb.Encode(200_000);

        Assert.Equal(0, exponent);
        Assert.Equal(200_000u, mantissa);
    }

    [Fact]
    public void Packet_bits_decode_back_to_the_requested_bitrate()
    {
        const uint Bitrate = 4_000_000;
        var packet = RtcpRemb.Build(1, 2, Bitrate);

        // Reverse the 6-bit exponent / 18-bit mantissa packing across bytes 17..19.
        var exponent = packet[17] >> 2;
        var mantissa = (uint)((packet[17] & 0x03) << 16 | packet[18] << 8 | packet[19]);

        var decoded = RtcpRemb.Decode(exponent, mantissa);

        Assert.True(
            Math.Abs((long)decoded - Bitrate) < (1L << exponent),
            $"decoded {decoded} from a packet built for {Bitrate}");
    }
}
