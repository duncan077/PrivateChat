using System.Buffers.Binary;

namespace ChatApp.Media.Janus;

/// <summary>
/// Builds REMB (Receiver Estimated Maximum Bitrate) feedback packets.
/// <para>
/// A WebRTC sender does not raise its bitrate until a receiver tells it there is room.
/// Receiver Reports carry loss and jitter but never a bandwidth figure, so a receiver
/// that reports 0% loss and sends no REMB leaves the sender's estimate wherever it
/// started — which is why a 1080p publisher settles at ~130 kbps and drops resolution
/// while every other statistic looks healthy.
/// </para>
/// <para>
/// SIPSorcery has no REMB support, so the packet is assembled here and sent through
/// <c>SendRtcpRaw</c>. Format per draft-alvestrand-rmcat-remb-03.
/// </para>
/// </summary>
internal static class RtcpRemb
{
    /// <summary>RTCP payload-specific feedback.</summary>
    private const byte PayloadTypePsfb = 206;

    /// <summary>Application-layer feedback, the FMT that carries REMB.</summary>
    private const byte FormatApplicationLayer = 15;

    /// <summary>18-bit mantissa ceiling; larger values shift into the exponent.</summary>
    private const uint MaxMantissa = 0x3FFFF;

    /// <summary>
    /// Builds a REMB packet advertising <paramref name="bitrateBps"/> for one sender.
    /// </summary>
    /// <param name="senderSsrc">Our SSRC, as the sender of this feedback.</param>
    /// <param name="mediaSsrc">The SSRC we are estimating for.</param>
    public static byte[] Build(uint senderSsrc, uint mediaSsrc, uint bitrateBps)
    {
        // Header, sender SSRC, media source SSRC, "REMB", exponent+mantissa, one SSRC.
        var packet = new byte[24];

        packet[0] = (2 << 6) | FormatApplicationLayer;
        packet[1] = PayloadTypePsfb;

        // Length is in 32-bit words, minus one, per RFC 3550.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));

        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), senderSsrc);

        // Media source SSRC is always zero in REMB; the streams being estimated are
        // listed at the end instead.
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0);

        packet[12] = (byte)'R';
        packet[13] = (byte)'E';
        packet[14] = (byte)'M';
        packet[15] = (byte)'B';

        packet[16] = 1; // one SSRC follows

        var (exponent, mantissa) = Encode(bitrateBps);

        // 6-bit exponent then 18-bit mantissa, packed across three bytes. The exponent is
        // widened explicitly so it is not sign-extended into the OR.
        packet[17] = (byte)(((uint)exponent << 2) | (mantissa >> 16));
        packet[18] = (byte)(mantissa >> 8);
        packet[19] = (byte)mantissa;

        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), mediaSsrc);

        return packet;
    }

    /// <summary>
    /// Splits a bitrate into the 6-bit exponent and 18-bit mantissa REMB uses, choosing
    /// the smallest exponent that keeps the mantissa in range so precision is preserved.
    /// </summary>
    internal static (int Exponent, uint Mantissa) Encode(uint bitrateBps)
    {
        var exponent = 0;
        var mantissa = bitrateBps;

        while (mantissa > MaxMantissa)
        {
            mantissa >>= 1;
            exponent++;
        }

        return (exponent, mantissa);
    }

    /// <summary>Recovers the bitrate from an encoded pair, for tests and diagnostics.</summary>
    internal static uint Decode(int exponent, uint mantissa) => mantissa << exponent;
}
