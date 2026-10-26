namespace ChatApp.Media.Janus;

/// <summary>
/// Splits an Annex-B access unit into RTP payloads per RFC 6184.
/// <para>
/// SIPSorcery packetizes internally when handed a whole frame, but it also sends every
/// packet of that frame back to back. A 1080p keyframe is hundreds of packets in a row,
/// which is a burst no link absorbs — so the packets have to be produced here, where they
/// can be handed to a pacer instead of the socket.
/// </para>
/// <para>
/// Single NAL unit packets, and FU-A fragmentation for anything above the payload limit.
/// STAP-A aggregation of small NALs is deliberately left out: it saves a few packets per
/// frame on parameter sets and is one more thing for a receiver to disagree about.
/// </para>
/// </summary>
internal static class H264RtpPacketizer
{
    /// <summary>
    /// Bytes of payload per packet.
    /// <para>
    /// A 1500-byte path MTU has to carry IPv6 (40) + UDP (8) + RTP (12) + the SRTP
    /// authentication tag and any header extensions. 1100 leaves room for all of it and
    /// for a tunnel or VPN in the path, at a cost of a few percent more packets.
    /// </para>
    /// </summary>
    public const int MaxPayloadSize = 1100;

    /// <summary>Fragmentation unit, indicator type 28.</summary>
    private const byte FuAIndicatorType = 28;

    private const byte FuAStart = 0x80;
    private const byte FuAEnd = 0x40;

    /// <summary>
    /// Produces the payloads for one access unit, in order. The caller marks the last one.
    /// </summary>
    public static List<byte[]> Packetize(ReadOnlySpan<byte> annexB, int maxPayloadSize = MaxPayloadSize)
    {
        var packets = new List<byte[]>();

        if (maxPayloadSize < 3 || annexB.IsEmpty)
            return packets;

        foreach (var range in FindNalUnits(annexB))
        {
            var nal = annexB.Slice(range.Start, range.Length);
            if (nal.IsEmpty)
                continue;

            if (nal.Length <= maxPayloadSize)
            {
                packets.Add(nal.ToArray());
                continue;
            }

            Fragment(nal, maxPayloadSize, packets);
        }

        return packets;
    }

    /// <summary>
    /// Splits one NAL unit across FU-A packets: the original header is replaced by an
    /// indicator plus a fragment header, so every fragment costs two bytes.
    /// </summary>
    private static void Fragment(ReadOnlySpan<byte> nal, int maxPayloadSize, List<byte[]> packets)
    {
        var header = nal[0];
        var indicator = (byte)((header & 0xE0) | FuAIndicatorType);
        var type = (byte)(header & 0x1F);

        var body = nal[1..];
        var perFragment = maxPayloadSize - 2;
        var offset = 0;

        while (offset < body.Length)
        {
            var length = Math.Min(perFragment, body.Length - offset);
            var packet = new byte[length + 2];

            packet[0] = indicator;
            packet[1] = type;

            if (offset == 0)
                packet[1] |= FuAStart;

            if (offset + length >= body.Length)
                packet[1] |= FuAEnd;

            body.Slice(offset, length).CopyTo(packet.AsSpan(2));
            packets.Add(packet);

            offset += length;
        }
    }

    /// <summary>
    /// Walks the start codes, yielding each NAL unit's extent. Three- and four-byte start
    /// codes both appear in the same stream, often within one frame.
    /// </summary>
    private static List<(int Start, int Length)> FindNalUnits(ReadOnlySpan<byte> annexB)
    {
        var units = new List<(int, int)>();
        var start = -1;

        for (var i = 0; i + 2 < annexB.Length;)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                if (start >= 0)
                {
                    // The previous unit ends before this start code, minus the zero byte a
                    // four-byte code shares with it.
                    var end = i > start && annexB[i - 1] == 0 ? i - 1 : i;
                    if (end > start)
                        units.Add((start, end - start));
                }

                start = i + 3;
                i += 3;
                continue;
            }

            i++;
        }

        if (start >= 0 && start < annexB.Length)
            units.Add((start, annexB.Length - start));

        return units;
    }
}
