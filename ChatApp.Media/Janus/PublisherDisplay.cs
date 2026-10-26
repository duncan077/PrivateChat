using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatApp.Media.Janus;

/// <summary>
/// Identity carried in a Janus publisher's <c>display</c> field.
/// <para>
/// Janus has no notion of application users, so the app encodes JSON into the free-text
/// display slot. Two older formats are still accepted, because a room can contain
/// clients that predate the JSON encoding: a bare user GUID, or a plain label.
/// </para>
/// </summary>
public sealed record PublisherDisplay
{
    /// <summary>Marks the participant's main camera/mic publisher.</summary>
    public const string RolePrimary = "primary";

    /// <summary>Marks a separate screen-share publisher from the same user.</summary>
    public const string RoleNativeScreen = "native-screen";

    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;

    public bool IsScreenShare
        => string.Equals(Role, RoleNativeScreen, StringComparison.OrdinalIgnoreCase);

    public static string Encode(Guid userId, string displayName, string? role)
    {
        var payload = new DisplayPayload
        {
            UserId = userId.ToString(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userId.ToString() : displayName,
            Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToLowerInvariant()
        };

        return JsonSerializer.Serialize(payload, DisplayJsonContext.Default.DisplayPayload);
    }

    public static PublisherDisplay Parse(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return new PublisherDisplay();

        var trimmed = display.Trim();

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                var payload = JsonSerializer.Deserialize(trimmed, DisplayJsonContext.Default.DisplayPayload);
                if (payload is not null)
                {
                    return new PublisherDisplay
                    {
                        UserId = Guid.TryParse(payload.UserId, out var id) ? id : Guid.Empty,
                        DisplayName = payload.DisplayName ?? payload.Name ?? string.Empty,
                        Role = (payload.Role ?? string.Empty).Trim().ToLowerInvariant()
                    };
                }
            }
            catch (JsonException)
            {
                // Fall through to the legacy formats.
            }
        }

        // Legacy: the display was just the user's GUID.
        if (Guid.TryParse(trimmed, out var legacyId))
            return new PublisherDisplay { UserId = legacyId };

        // Legacy: the display was a plain human label.
        return new PublisherDisplay { DisplayName = trimmed };
    }

    /// <summary>
    /// Stable key for collapsing the multiple Janus feeds that belong to one person.
    /// </summary>
    public string IdentityKey
    {
        get
        {
            if (UserId != Guid.Empty)
                return $"uid:{UserId:N}";

            return string.IsNullOrWhiteSpace(DisplayName)
                ? string.Empty
                : $"name:{DisplayName.Trim().ToLowerInvariant()}";
        }
    }

    internal sealed class DisplayPayload
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        /// <summary>Accepted on read only; older clients wrote <c>name</c>.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Role { get; set; }
    }
}

[JsonSerializable(typeof(PublisherDisplay.DisplayPayload))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DisplayJsonContext : JsonSerializerContext;
