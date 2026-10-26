using System.Text.Json;

namespace ChatApp.Media.Janus;

/// <summary>
/// Turns raw Janus JSON into <see cref="JanusEvent"/> instances.
/// <para>
/// Janus multiplexes every shape onto one long-poll channel and is loose about types —
/// <c>unpublished</c> can be the string <c>"ok"</c> or a numeric feed id, publisher ids
/// arrive as numbers or numeric strings, and <c>plugindata.data</c> may be absent. All
/// of that leniency is reproduced here, matching the JS client's behaviour.
/// </para>
/// </summary>
internal static class JanusEventParser
{
    public static JanusEvent Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new JanusUnknownEvent { Raw = root, JanusType = "(non-object)" };

        var janusType = GetString(root, "janus") ?? string.Empty;
        var handleId = GetInt64(root, "sender") ?? 0;

        // Parsed up front and attached to whatever event we return. JSEP travels
        // alongside plugin data rather than instead of it — a joinandconfigure response
        // contains the publisher list AND the SDP answer.
        var jsep = TryParseJsep(root, out var parsedJsep) ? parsedJsep : null;

        switch (janusType)
        {
            case "keepalive":
                return new JanusKeepaliveEvent { Raw = root, HandleId = handleId };

            case "error":
                return ParseError(root, handleId);

            case "webrtcup":
                return new JanusWebRtcStateEvent { Raw = root, HandleId = handleId, Up = true, Jsep = jsep };

            case "hangup":
            case "detached":
                return new JanusWebRtcStateEvent { Raw = root, HandleId = handleId, Up = false, Jsep = jsep };

            case "media":
                return new JanusMediaEvent
                {
                    Raw = root,
                    HandleId = handleId,
                    Jsep = jsep,
                    Type = GetString(root, "type") ?? string.Empty,
                    Receiving = GetBool(root, "receiving") ?? false,
                    Mid = GetString(root, "mid")
                };
        }

        var data = TryGetPluginData(root);

        if (data is { } pluginData)
        {
            if (pluginData.TryGetProperty("error", out var pluginError))
            {
                return new JanusErrorEvent
                {
                    Raw = root,
                    HandleId = handleId,
                    Jsep = jsep,
                    Code = GetInt32(pluginData, "error_code") ?? 0,
                    Reason = pluginError.ValueKind == JsonValueKind.String
                        ? pluginError.GetString() ?? "Janus plugin error"
                        : "Janus plugin error"
                };
            }

            if (TryParsePublisherGone(pluginData, root, handleId, jsep, out var gone))
                return gone;

            var hasPublishers = pluginData.TryGetProperty("publishers", out var publishers)
                                && publishers.ValueKind == JsonValueKind.Array;
            var privateId = GetInt64(pluginData, "private_id");

            // A publishers-less "joined" still carries private_id, which subscriber joins
            // need, so surface it rather than dropping it.
            if (hasPublishers || privateId is not null)
            {
                return new JanusPublishersEvent
                {
                    Raw = root,
                    HandleId = handleId,
                    Jsep = jsep,
                    Publishers = hasPublishers ? ParsePublisherArray(publishers) : [],
                    PrivateId = privateId
                };
            }
        }

        if (jsep is not null)
            return new JanusJsepEvent { Raw = root, HandleId = handleId, Jsep = jsep };

        return new JanusUnknownEvent { Raw = root, HandleId = handleId, JanusType = janusType };
    }

    private static JanusErrorEvent ParseError(JsonElement root, long handleId)
    {
        var code = 0;
        var reason = "Janus error";

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = GetInt32(error, "code") ?? 0;
            reason = GetString(error, "reason") ?? reason;
        }

        return new JanusErrorEvent { Raw = root, HandleId = handleId, Code = code, Reason = reason };
    }

    private static bool TryParsePublisherGone(
        JsonElement data,
        JsonElement root,
        long handleId,
        Jsep? jsep,
        out JanusPublisherGoneEvent result)
    {
        if (TryReadFeedId(data, "leaving", out var leavingId))
        {
            result = new JanusPublisherGoneEvent
            {
                Raw = root,
                HandleId = handleId,
                Jsep = jsep,
                FeedId = leavingId,
                Unpublished = false
            };
            return true;
        }

        // "unpublished": "ok" is the acknowledgement of our OWN unpublish request, not
        // a remote publisher going away — only a feed id counts.
        if (TryReadFeedId(data, "unpublished", out var unpublishedId))
        {
            result = new JanusPublisherGoneEvent
            {
                Raw = root,
                HandleId = handleId,
                Jsep = jsep,
                FeedId = unpublishedId,
                Unpublished = true
            };
            return true;
        }

        result = null!;
        return false;
    }

    private static bool TryReadFeedId(JsonElement data, string propertyName, out long feedId)
    {
        feedId = 0;

        if (!data.TryGetProperty(propertyName, out var value))
            return false;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out feedId);

            case JsonValueKind.String:
                var text = value.GetString();
                // Filters out "ok" and the "leaving": "ok" self-acknowledgement.
                return long.TryParse(text, out feedId);

            default:
                return false;
        }
    }

    public static IReadOnlyList<JanusRemotePublisher> ParseParticipants(JsonElement root)
    {
        if (TryGetPluginData(root) is { } data
            && data.TryGetProperty("participants", out var participants)
            && participants.ValueKind == JsonValueKind.Array)
        {
            return ParsePublisherArray(participants);
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("participants", out var direct)
            && direct.ValueKind == JsonValueKind.Array)
        {
            return ParsePublisherArray(direct);
        }

        return [];
    }

    private static List<JanusRemotePublisher> ParsePublisherArray(JsonElement array)
    {
        var result = new List<JanusRemotePublisher>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (GetInt64(element, "id") is not { } id)
                continue;

            // listparticipants reports every member; only publishers have media.
            if (GetBool(element, "publisher") == false && !element.TryGetProperty("streams", out _))
                continue;

            result.Add(new JanusRemotePublisher
            {
                Id = id,
                Display = GetString(element, "display"),
                Streams = ParseStreams(element)
            });
        }

        return result;
    }

    private static IReadOnlyList<JanusPublisherStream> ParseStreams(JsonElement publisher)
    {
        if (!publisher.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<JanusPublisherStream>();

        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object)
                continue;

            result.Add(new JanusPublisherStream
            {
                Type = GetString(stream, "type") ?? string.Empty,
                Mid = GetString(stream, "mid"),
                Codec = GetString(stream, "codec"),
                Description = GetString(stream, "description"),
                Disabled = GetBool(stream, "disabled") ?? false
            });
        }

        return result;
    }

    private static bool TryParseJsep(JsonElement root, out Jsep jsep)
    {
        jsep = null!;

        if (!root.TryGetProperty("jsep", out var element) || element.ValueKind != JsonValueKind.Object)
            return false;

        var type = GetString(element, "type");
        var sdp = GetString(element, "sdp");

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(sdp))
            return false;

        jsep = new Jsep(type, sdp);
        return true;
    }

    private static JsonElement? TryGetPluginData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("plugindata", out var pluginData) || pluginData.ValueKind != JsonValueKind.Object)
            return null;

        if (!pluginData.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        return data;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            // Janus sometimes stringifies large ids.
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt32(JsonElement element, string name)
        => GetInt64(element, name) is { } value ? (int)value : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
