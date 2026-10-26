namespace ChatApp.Media.Janus;

/// <summary>
/// Reads what an SDP actually offers, as opposed to what the peer connection was
/// configured to accept.
/// <para>
/// SIPSorcery's <c>OnVideoFormatsNegotiated</c> reports the formats of the local track,
/// so it fires on a recvonly subscriber whether or not the remote description contains a
/// video m-line at all. Telling "Janus never offered video" apart from "video was offered
/// but is not arriving" therefore has to come from the SDP itself.
/// </para>
/// </summary>
internal static class SdpMediaInspector
{
    /// <summary>
    /// True when the SDP contains a video m-line the far end will actually send on: a
    /// non-zero port, and not marked inactive or recvonly.
    /// </summary>
    public static bool HasSendingVideo(string? sdp) => HasSendingMedia(sdp, "video");

    public static bool HasSendingAudio(string? sdp) => HasSendingMedia(sdp, "audio");

    private static bool HasSendingMedia(string? sdp, string kind)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            return false;

        var inSection = false;
        var sending = false;
        var portIsOpen = false;

        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                // A previous section of the right kind already qualified.
                if (inSection && portIsOpen && sending)
                    return true;

                inSection = line.StartsWith($"m={kind} ", StringComparison.Ordinal);
                portIsOpen = inSection && GetPort(line) != 0;

                // Direction defaults to sendrecv when the section declares none.
                sending = inSection;
                continue;
            }

            if (!inSection)
                continue;

            if (line.StartsWith("a=inactive", StringComparison.Ordinal)
                || line.StartsWith("a=recvonly", StringComparison.Ordinal))
            {
                sending = false;
            }
            else if (line.StartsWith("a=sendonly", StringComparison.Ordinal)
                     || line.StartsWith("a=sendrecv", StringComparison.Ordinal))
            {
                sending = true;
            }
        }

        return inSection && portIsOpen && sending;
    }

    /// <summary>Port from an <c>m=</c> line, or 0 when it cannot be read.</summary>
    private static int GetPort(string mediaLine)
    {
        var parts = mediaLine.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var port) ? port : 0;
    }
}
