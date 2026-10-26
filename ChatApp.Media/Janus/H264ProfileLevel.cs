namespace ChatApp.Media.Janus;

/// <summary>
/// Works out the <c>profile-level-id</c> to advertise for a stream we are about to send.
/// <para>
/// This is not decoration. A receiver configures its decoder from this string — a hardware
/// one sizes its surface pool and picture buffer from the level, and enables or forbids
/// CABAC from the profile. Advertising Constrained Baseline 3.1 (the value every WebRTC
/// example uses) while sending Main 4.2 gives it a stream more than twice the frame size
/// it was told to expect, entropy-coded in a way that profile forbids. It decodes a
/// handful of frames and stops, while the bitrate keeps reading healthy, because the
/// packets are arriving perfectly — they are simply not the stream that was negotiated.
/// </para>
/// </summary>
internal static class H264ProfileLevel
{
    /// <summary>
    /// Main profile, with the constraint flags this app's encoders actually set. All of
    /// them are configured to produce Main, so the advertised profile cannot drift from
    /// the stream just because a different encoder was picked.
    /// </summary>
    private const string MainProfile = "4d40";

    /// <summary>
    /// Levels, in ascending order, with their frame-size and macroblock-rate ceilings from
    /// Annex A of the specification.
    /// </summary>
    private static readonly (int LevelIdc, int MaxMacroblocks, int MaxMacroblocksPerSecond)[] Levels =
    [
        (0x1F, 3600, 108_000),      // 3.1 — 720p30
        (0x20, 5120, 216_000),      // 3.2
        (0x28, 8192, 245_760),      // 4.0 — 1080p30
        (0x29, 8192, 245_760),      // 4.1
        (0x2A, 8704, 522_240),      // 4.2 — 1080p60
        (0x32, 22_080, 589_824),    // 5.0
        (0x33, 36_864, 983_040),    // 5.1 — 1440p60, 4K30
        (0x34, 36_864, 2_073_600)   // 5.2 — 4K60
    ];

    /// <summary>Macroblocks in a frame of this size. A macroblock is 16x16, rounded up.</summary>
    public static int Macroblocks(int width, int height)
        => (Math.Max(width, 1) + 15) / 16 * ((Math.Max(height, 1) + 15) / 16);

    /// <summary>
    /// The lowest level that can carry this resolution and frame rate, as its level_idc.
    /// Never below 3.1, which every endpoint supports and which nothing gains by
    /// undercutting; capped at 5.2, beyond which the specification has nothing more to
    /// offer a video call.
    /// </summary>
    public static int LevelFor(int width, int height, int frameRate)
    {
        var macroblocks = Macroblocks(width, height);
        var perSecond = macroblocks * Math.Max(frameRate, 1);

        foreach (var (levelIdc, maxMacroblocks, maxPerSecond) in Levels)
        {
            if (macroblocks <= maxMacroblocks && perSecond <= maxPerSecond)
                return levelIdc;
        }

        return Levels[^1].LevelIdc;
    }

    /// <summary>The fmtp value to advertise for a stream of this size and rate.</summary>
    public static string ProfileLevelId(int width, int height, int frameRate)
        => $"{MainProfile}{LevelFor(width, height, frameRate):x2}";

    /// <summary>
    /// The fmtp line for sending, including the packetization mode. Mode 1 permits FU-A
    /// fragmentation, which any frame larger than one packet needs.
    /// </summary>
    public static string SendFmtp(int width, int height, int frameRate)
        => $"packetization-mode=1;profile-level-id={ProfileLevelId(width, height, frameRate)}";

    /// <summary>
    /// The fmtp line for receiving. Levelled at the top because decoding is done by FFmpeg,
    /// which does not care, and understating it would invite senders to hold back.
    /// </summary>
    public static string ReceiveFmtp() => $"packetization-mode=1;profile-level-id={MainProfile}34";
}
