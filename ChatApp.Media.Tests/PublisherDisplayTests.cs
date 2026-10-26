using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// Janus has no concept of app users, so identity rides in the free-text display field.
/// Two legacy encodings are still on the wire and must keep parsing.
/// </summary>
public class PublisherDisplayTests
{
    private static readonly Guid UserId = Guid.Parse("2f1c9a4e-7b3d-4f18-9c0a-5d6e7f801234");

    [Fact]
    public void Round_trips_the_json_encoding()
    {
        var encoded = PublisherDisplay.Encode(UserId, "Alice", PublisherDisplay.RolePrimary);
        var parsed = PublisherDisplay.Parse(encoded);

        Assert.Equal(UserId, parsed.UserId);
        Assert.Equal("Alice", parsed.DisplayName);
        Assert.Equal("primary", parsed.Role);
        Assert.False(parsed.IsScreenShare);
    }

    [Fact]
    public void Recognises_the_screen_share_role()
    {
        var encoded = PublisherDisplay.Encode(UserId, "Alice", PublisherDisplay.RoleNativeScreen);

        Assert.True(PublisherDisplay.Parse(encoded).IsScreenShare);
    }

    [Fact]
    public void Omits_role_when_none_is_given()
    {
        var encoded = PublisherDisplay.Encode(UserId, "Alice", role: null);

        Assert.DoesNotContain("role", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, PublisherDisplay.Parse(encoded).Role);
    }

    [Fact]
    public void Falls_back_to_the_user_id_when_display_name_is_blank()
    {
        var encoded = PublisherDisplay.Encode(UserId, "   ", role: null);

        Assert.Equal(UserId.ToString(), PublisherDisplay.Parse(encoded).DisplayName);
    }

    [Fact]
    public void Parses_legacy_bare_guid_display()
    {
        var parsed = PublisherDisplay.Parse(UserId.ToString());

        Assert.Equal(UserId, parsed.UserId);
        Assert.Equal(string.Empty, parsed.DisplayName);
    }

    [Fact]
    public void Parses_legacy_plain_label_display()
    {
        var parsed = PublisherDisplay.Parse("Bob's laptop");

        Assert.Equal(Guid.Empty, parsed.UserId);
        Assert.Equal("Bob's laptop", parsed.DisplayName);
    }

    [Fact]
    public void Accepts_the_older_name_field()
    {
        var parsed = PublisherDisplay.Parse("""{"userId":"2f1c9a4e-7b3d-4f18-9c0a-5d6e7f801234","name":"Carol"}""");

        Assert.Equal(UserId, parsed.UserId);
        Assert.Equal("Carol", parsed.DisplayName);
    }

    [Fact]
    public void Treats_malformed_json_as_a_plain_label()
    {
        // Must not throw: a malformed display would otherwise kill the poll loop.
        var parsed = PublisherDisplay.Parse("{not valid json}");

        Assert.Equal(Guid.Empty, parsed.UserId);
        Assert.Equal("{not valid json}", parsed.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Handles_missing_display(string? display)
    {
        var parsed = PublisherDisplay.Parse(display);

        Assert.Equal(Guid.Empty, parsed.UserId);
        Assert.Equal(string.Empty, parsed.DisplayName);
        Assert.Equal(string.Empty, parsed.IdentityKey);
    }

    [Fact]
    public void Identity_key_prefers_user_id_over_display_name()
    {
        var byId = PublisherDisplay.Parse(PublisherDisplay.Encode(UserId, "Alice", null));
        var renamed = PublisherDisplay.Parse(PublisherDisplay.Encode(UserId, "Alice Renamed", null));

        // Same person across a rename must collapse to one identity.
        Assert.Equal(byId.IdentityKey, renamed.IdentityKey);
    }

    [Fact]
    public void Identity_key_is_case_insensitive_for_name_only_publishers()
    {
        Assert.Equal(
            PublisherDisplay.Parse("Alice").IdentityKey,
            PublisherDisplay.Parse("  alice  ").IdentityKey);
    }
}
