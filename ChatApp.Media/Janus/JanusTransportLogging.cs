using Microsoft.Extensions.Logging;

namespace ChatApp.Media.Janus;

/// <summary>
/// Routes SIPSorcery's own ICE, DTLS and SRTP logging into the app's logger.
/// <para>
/// Without this a transport failure surfaces only as a final <c>failed</c> connection
/// state, with nothing to say which candidate pairs were tried or what came back. The
/// library keeps per-packet logging at Trace, so Debug stays readable.
/// </para>
/// </summary>
public static class JanusTransportLogging
{
    private static int _enabled;

    public static void Enable(ILoggerFactory loggerFactory)
    {
        if (Interlocked.Exchange(ref _enabled, 1) == 1)
            return;

        SIPSorcery.LogFactory.Set(loggerFactory);
    }
}
