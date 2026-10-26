using System.Globalization;
using System.Text;

namespace ChatApp.Media.Janus;

/// <summary>
/// Adds the RTCP feedback attributes SIPSorcery does not offer.
/// <para>
/// Its video m-line advertises <c>transport-cc</c> and nothing else. In particular it
/// never offers <c>nack</c>, and feedback is negotiated per direction: a peer that was not
/// offered NACK will not send one, so a lost packet is never asked for again and the frame
/// it belonged to is gone until the next keyframe. Janus offers all four of these in its
/// own answer, which is the shape every other endpoint expects.
/// </para>
/// <para>
/// Rewriting the SDP text is not elegant, but the alternative is a fork of the library's
/// SDP generation, and the Blazor client rewrites its own SDP for bitrate parameters in
/// the same way.
/// </para>
/// </summary>
internal static class SdpFeedback
{
    /// <summary>
    /// What we offer: keyframe requests by both mechanisms, and receiver-estimated
    /// bandwidth. All three are things this client actually acts on.
    /// <para>
    /// Bare <c>nack</c> is deliberately absent. Offering it invites a peer to ask for a
    /// lost packet to be sent again, and answering that on the same SSRC means emitting a
    /// far lower sequence number than the ones around it — which a sender's SRTP layer
    /// reads as the sequence having wrapped, and it increments its rollover counter
    /// permanently. Every packet after that is encrypted against a counter the receiver
    /// does not share, so the stream dies outright rather than losing a frame. RTX
    /// (RFC 4588), where retransmissions carry their own SSRC and their own ascending
    /// sequence numbers, is the mechanism that exists to avoid this; until that is built,
    /// offering NACK would only make a peer wait for repairs that never come before
    /// falling back to asking for a keyframe.
    /// </para>
    /// </summary>
    private static readonly string[] Wanted = ["nack pli", "ccm fir", "goog-remb"];

    /// <summary>
    /// Returns the SDP with the missing feedback attributes added to the video section.
    /// </summary>
    public static string AddVideoFeedback(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
            return sdp ?? string.Empty;

        var lines = sdp.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder(sdp.Length + 256);

        var inVideo = false;

        // Feedback already present for a payload type, so nothing is offered twice.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadTypes = new List<string>();

        // First pass: which payload types the video section has, and what they already
        // advertise. Attributes can appear in any order relative to the rtpmap.
        foreach (var line in lines)
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
                continue;
            }

            if (!inVideo)
                continue;

            if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                var payloadType = line[9..].Split(' ')[0];
                if (payloadType.Length > 0)
                    payloadTypes.Add(payloadType);
            }
            else if (line.StartsWith("a=rtcp-fb:", StringComparison.Ordinal))
            {
                existing.Add(line[10..].Trim());
            }
        }

        if (payloadTypes.Count == 0)
            return sdp;

        // Second pass: emit, adding what is missing after each rtpmap.
        inVideo = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // The final element after a trailing newline is empty; keep the shape intact.
            if (i == lines.Length - 1 && line.Length == 0)
                break;

            result.Append(line).Append("\r\n");

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
                continue;
            }

            if (!inVideo || !line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
                continue;

            var payloadType = line[9..].Split(' ')[0];
            if (payloadType.Length == 0)
                continue;

            foreach (var feedback in Wanted)
            {
                var attribute = string.Create(
                    CultureInfo.InvariantCulture, $"{payloadType} {feedback}");

                if (existing.Contains(attribute))
                    continue;

                result.Append("a=rtcp-fb:").Append(attribute).Append("\r\n");
                existing.Add(attribute);
            }
        }

        return result.ToString();
    }
}
