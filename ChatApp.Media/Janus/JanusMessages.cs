using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatApp.Media.Janus;

/// <summary>SDP offer/answer as Janus exchanges it.</summary>
public sealed record Jsep(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sdp")] string Sdp)
{
    public static Jsep Offer(string sdp) => new("offer", sdp);
    public static Jsep Answer(string sdp) => new("answer", sdp);
}

/// <summary>
/// One stream in a Janus multistream subscribe request.
/// The legacy <c>feed</c> field is kept alongside <c>mid</c> because the room may be
/// running either protocol generation.
/// </summary>
public sealed record JanusSubscribeStream
{
    [JsonPropertyName("feed")]
    public long Feed { get; init; }

    [JsonPropertyName("mid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mid { get; init; }
}

/// <summary>A publisher advertised by the room.</summary>
public sealed record JanusRemotePublisher
{
    public long Id { get; init; }
    public string? Display { get; init; }
    public IReadOnlyList<JanusPublisherStream> Streams { get; init; } = [];
}

/// <summary>One media stream belonging to a publisher.</summary>
public sealed record JanusPublisherStream
{
    /// <summary>"audio" or "video".</summary>
    public string Type { get; init; } = string.Empty;

    public string? Mid { get; init; }

    public string? Codec { get; init; }

    /// <summary>Publisher-supplied label; the app encodes screen-share role in it.</summary>
    public string? Description { get; init; }

    public bool Disabled { get; init; }

    public bool IsVideo => string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);
    public bool IsAudio => string.Equals(Type, "audio", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A decoded event from the long-poll loop. Janus multiplexes many shapes onto one
/// channel, so this is a discriminated view over the raw JSON.
/// </summary>
public abstract record JanusEvent
{
    /// <summary>Handle the event was addressed to, when Janus supplied one.</summary>
    public long HandleId { get; init; }

    /// <summary>
    /// SDP attached to this event, if any.
    /// <para>
    /// JSEP is orthogonal to the event kind, not an alternative to it: a
    /// <c>joinandconfigure</c> response carries the publisher list <b>and</b> the answer
    /// in the same message. Treating them as mutually exclusive silently discards the
    /// answer and the publisher never connects.
    /// </para>
    /// </summary>
    public Jsep? Jsep { get; init; }

    /// <summary>Raw payload, for cases the typed cases do not cover.</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>New or updated publishers appeared in the room.</summary>
public sealed record JanusPublishersEvent : JanusEvent
{
    public IReadOnlyList<JanusRemotePublisher> Publishers { get; init; } = [];

    /// <summary>Our own private id, needed to authenticate subscriber joins.</summary>
    public long? PrivateId { get; init; }
}

/// <summary>A publisher left the room or stopped publishing.</summary>
public sealed record JanusPublisherGoneEvent : JanusEvent
{
    public long FeedId { get; init; }

    /// <summary>True for <c>unpublished</c>, false for <c>leaving</c>.</summary>
    public bool Unpublished { get; init; }
}

/// <summary>
/// Janus sent SDP and nothing else — typically a subscriber offer. The payload lives on
/// <see cref="JanusEvent.Jsep"/>, since any event kind may carry SDP.
/// </summary>
public sealed record JanusJsepEvent : JanusEvent;

/// <summary>ICE/DTLS state change for a handle.</summary>
public sealed record JanusWebRtcStateEvent : JanusEvent
{
    public bool Up { get; init; }
}

/// <summary>Janus reported a media flow change (receiving/stopped receiving).</summary>
public sealed record JanusMediaEvent : JanusEvent
{
    public string Type { get; init; } = string.Empty;
    public bool Receiving { get; init; }
    public string? Mid { get; init; }
}

/// <summary>Plugin or transport level error.</summary>
public sealed record JanusErrorEvent : JanusEvent
{
    public int Code { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Nothing happened before the poll timed out.</summary>
public sealed record JanusKeepaliveEvent : JanusEvent;

/// <summary>Any event we do not model explicitly.</summary>
public sealed record JanusUnknownEvent : JanusEvent
{
    public string JanusType { get; init; } = string.Empty;
}

/// <summary>
/// Thrown when Janus no longer recognises the session. The caller must re-join
/// through the chat hub to obtain fresh credentials.
/// </summary>
public sealed class JanusSessionLostException(long sessionId)
    : Exception($"Janus session {sessionId} no longer exists.")
{
    public long SessionId { get; } = sessionId;
}
