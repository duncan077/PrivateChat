using System.Text.Json;
using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Janus is loose about types on the long-poll channel, and several of its quirks are
/// easy to get subtly wrong. These pin the behaviour the JS client relied on.
/// </summary>
public class JanusEventParserTests
{
    private static JanusEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JanusEventParser.Parse(document.RootElement.Clone());
    }

    [Fact]
    public void Parses_publishers_with_streams_and_private_id()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 42,
          "plugindata": {
            "data": {
              "videoroom": "joined",
              "private_id": 998877,
              "publishers": [
                {
                  "id": 1234,
                  "display": "alice",
                  "streams": [
                    { "type": "audio", "mid": "0", "codec": "opus" },
                    { "type": "video", "mid": "1", "codec": "h264", "description": "screen" }
                  ]
                }
              ]
            }
          }
        }
        """);

        var publishers = Assert.IsType<JanusPublishersEvent>(evt);
        Assert.Equal(42, publishers.HandleId);
        Assert.Equal(998877, publishers.PrivateId);

        var publisher = Assert.Single(publishers.Publishers);
        Assert.Equal(1234, publisher.Id);
        Assert.Equal("alice", publisher.Display);
        Assert.Equal(2, publisher.Streams.Count);
        Assert.Contains(publisher.Streams, s => s.IsAudio && s.Codec == "opus");
        Assert.Contains(publisher.Streams, s => s.IsVideo && s.Description == "screen");
    }

    [Fact]
    public void Treats_unpublished_ok_as_our_own_ack_not_a_departing_publisher()
    {
        // "unpublished": "ok" acknowledges OUR unpublish request. Reading it as a feed
        // id would tear down a random remote tile.
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "videoroom": "event", "unpublished": "ok" } }
        }
        """);

        Assert.IsNotType<JanusPublisherGoneEvent>(evt);
    }

    [Fact]
    public void Parses_unpublished_feed_id_as_publisher_gone()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "videoroom": "event", "unpublished": 5551 } }
        }
        """);

        var gone = Assert.IsType<JanusPublisherGoneEvent>(evt);
        Assert.Equal(5551, gone.FeedId);
        Assert.True(gone.Unpublished);
    }

    [Fact]
    public void Parses_leaving_feed_id_as_publisher_gone()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "videoroom": "event", "leaving": 7777 } }
        }
        """);

        var gone = Assert.IsType<JanusPublisherGoneEvent>(evt);
        Assert.Equal(7777, gone.FeedId);
        Assert.False(gone.Unpublished);
    }

    [Fact]
    public void Accepts_stringified_feed_ids()
    {
        // Janus stringifies large ids in some builds.
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "videoroom": "event", "leaving": "9007199254740993" } }
        }
        """);

        var gone = Assert.IsType<JanusPublisherGoneEvent>(evt);
        Assert.Equal(9007199254740993, gone.FeedId);
    }

    [Fact]
    public void Parses_jsep_offer()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "sender": 77,
          "jsep": { "type": "offer", "sdp": "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\n" }
        }
        """);

        var jsepEvent = Assert.IsType<JanusJsepEvent>(evt);
        Assert.NotNull(jsepEvent.Jsep);
        Assert.Equal("offer", jsepEvent.Jsep!.Type);
        Assert.StartsWith("v=0", jsepEvent.Jsep.Sdp);
        Assert.Equal(77, jsepEvent.HandleId);
    }

    [Fact]
    public void Surfaces_private_id_even_when_no_publishers_are_present()
    {
        // An empty room still returns private_id, and subscriber joins need it.
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "videoroom": "joined", "private_id": 4242 } }
        }
        """);

        var publishers = Assert.IsType<JanusPublishersEvent>(evt);
        Assert.Equal(4242, publishers.PrivateId);
        Assert.Empty(publishers.Publishers);
    }

    [Fact]
    public void Parses_transport_level_error()
    {
        var evt = Parse("""
        { "janus": "error", "error": { "code": 458, "reason": "No such session" } }
        """);

        var error = Assert.IsType<JanusErrorEvent>(evt);
        Assert.Equal(458, error.Code);
        Assert.Equal("No such session", error.Reason);
    }

    [Fact]
    public void Parses_plugin_level_error()
    {
        var evt = Parse("""
        {
          "janus": "event",
          "plugindata": { "data": { "error_code": 426, "error": "No such room" } }
        }
        """);

        var error = Assert.IsType<JanusErrorEvent>(evt);
        Assert.Equal(426, error.Code);
        Assert.Equal("No such room", error.Reason);
    }

    [Theory]
    [InlineData("webrtcup", true)]
    [InlineData("hangup", false)]
    [InlineData("detached", false)]
    public void Parses_webrtc_state(string janusType, bool expectedUp)
    {
        var evt = Parse($$"""{ "janus": "{{janusType}}", "sender": 5 }""");

        var state = Assert.IsType<JanusWebRtcStateEvent>(evt);
        Assert.Equal(expectedUp, state.Up);
        Assert.Equal(5, state.HandleId);
    }

    [Fact]
    public void Parses_media_event()
    {
        var evt = Parse("""
        { "janus": "media", "sender": 9, "type": "video", "receiving": true, "mid": "1" }
        """);

        var media = Assert.IsType<JanusMediaEvent>(evt);
        Assert.Equal("video", media.Type);
        Assert.True(media.Receiving);
        Assert.Equal("1", media.Mid);
    }

    [Fact]
    public void Recognises_keepalive()
        => Assert.IsType<JanusKeepaliveEvent>(Parse("""{ "janus": "keepalive" }"""));

    [Fact]
    public void Does_not_throw_on_unexpected_shapes()
    {
        Assert.IsType<JanusUnknownEvent>(Parse("""{ "janus": "trickle" }"""));
        Assert.IsType<JanusUnknownEvent>(Parse("[]"));
        Assert.IsType<JanusUnknownEvent>(Parse("""{ "plugindata": { "data": "not-an-object" } }"""));
    }

    [Fact]
    public void ParseParticipants_reads_listparticipants_response()
    {
        using var document = JsonDocument.Parse("""
        {
          "janus": "success",
          "plugindata": {
            "data": {
              "videoroom": "participants",
              "participants": [
                { "id": 11, "display": "alice", "publisher": true,
                  "streams": [ { "type": "video", "mid": "0", "codec": "h264" } ] },
                { "id": 22, "display": "bob", "publisher": false }
              ]
            }
          }
        }
        """);

        var participants = JanusEventParser.ParseParticipants(document.RootElement);

        // Non-publishers have no media to subscribe to and must be filtered out.
        var publisher = Assert.Single(participants);
        Assert.Equal(11, publisher.Id);
        Assert.Equal("alice", publisher.Display);
    }
}
