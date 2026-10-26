using System.Text.Json;
using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Regression tests for the first live-call failure: the publisher never connected
/// because the SDP answer was discarded.
/// <para>
/// Janus attaches JSEP <i>alongside</i> plugin data, not instead of it. The original
/// parser treated event kinds as mutually exclusive and returned the publisher list
/// first, silently dropping the answer — so our audio was never sent, and the only
/// symptom was a warning plus a handle that went "webrtc down" 12 seconds later.
/// </para>
/// </summary>
public class JanusJsepRoutingTests
{
    private static JanusEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JanusEventParser.Parse(document.RootElement.Clone());
    }

    /// <summary>Shape of the real joinandconfigure response, from the call that failed.</summary>
    private const string JoinAndConfigureResponse = """
    {
      "janus": "event",
      "sender": 5744572136203916,
      "plugindata": {
        "plugin": "janus.plugin.videoroom",
        "data": {
          "videoroom": "joined",
          "room": 1536272304,
          "description": "room",
          "id": 2620499878,
          "private_id": 2620499878,
          "publishers": []
        }
      },
      "jsep": {
        "type": "answer",
        "sdp": "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n"
      }
    }
    """;

    [Fact]
    public void JoinAndConfigure_response_exposes_both_publishers_and_the_answer()
    {
        var evt = Parse(JoinAndConfigureResponse);

        var publishers = Assert.IsType<JanusPublishersEvent>(evt);
        Assert.Equal(2620499878, publishers.PrivateId);

        // The regression: this was null, so the publisher never applied a remote
        // description and stayed in RTC_CONNECTING until Janus tore it down.
        Assert.NotNull(evt.Jsep);
        Assert.Equal("answer", evt.Jsep!.Type);
        Assert.Contains("m=audio", evt.Jsep.Sdp, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishers_event_with_feeds_still_carries_jsep()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 42,
          "plugindata": { "data": {
            "videoroom": "joined",
            "private_id": 99,
            "publishers": [ { "id": 7, "display": "alice", "streams": [] } ]
          } },
          "jsep": { "type": "answer", "sdp": "v=0\r\n" }
        }
        """);

        var publishers = Assert.IsType<JanusPublishersEvent>(evt);
        Assert.Single(publishers.Publishers);
        Assert.NotNull(evt.Jsep);
    }

    [Fact]
    public void Jsep_only_event_is_still_reported_as_a_jsep_event()
    {
        // The subscriber offer arrives with no plugin data at all.
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 1178995523733648,
          "jsep": { "type": "offer", "sdp": "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" }
        }
        """);

        Assert.IsType<JanusJsepEvent>(evt);
        Assert.NotNull(evt.Jsep);
        Assert.Equal("offer", evt.Jsep!.Type);
        Assert.Equal(1178995523733648, evt.HandleId);
    }

    [Fact]
    public void Publisher_gone_event_can_carry_jsep()
    {
        // Janus renegotiates the subscriber in the same message that reports a departure.
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 5,
          "plugindata": { "data": { "videoroom": "event", "leaving": 1287358528346292 } },
          "jsep": { "type": "offer", "sdp": "v=0\r\n" }
        }
        """);

        var gone = Assert.IsType<JanusPublisherGoneEvent>(evt);
        Assert.Equal(1287358528346292, gone.FeedId);
        Assert.NotNull(evt.Jsep);
    }

    [Fact]
    public void Events_without_sdp_report_no_jsep()
    {
        Assert.Null(Parse("""{ "janus": "webrtcup", "sender": 5 }""").Jsep);
        Assert.Null(Parse("""{ "janus": "keepalive" }""").Jsep);
    }

    [Fact]
    public void Malformed_jsep_is_ignored_rather_than_half_applied()
    {
        // Missing sdp: applying an empty remote description would break the connection
        // more confusingly than skipping it.
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 5,
          "jsep": { "type": "answer" }
        }
        """);

        Assert.Null(evt.Jsep);
    }

    [Fact]
    public void Joined_with_neither_publishers_nor_private_id_is_not_a_publishers_event()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 5,
          "plugindata": { "data": { "videoroom": "event", "configured": "ok" } }
        }
        """);

        Assert.IsType<JanusUnknownEvent>(evt);
    }
}
