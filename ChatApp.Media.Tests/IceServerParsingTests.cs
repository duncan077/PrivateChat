using ChatApp.Media.Janus;

namespace ChatApp.Media.Tests;

/// <summary>
/// The server hands out ICE servers in the browser's WebRTC JSON shape. SIPSorcery takes
/// credentials as separate fields, so the percent-encoding the previous transport needed
/// - it embedded them in the URL - is gone, but a mis-parse still silently disables TURN
/// and only shows up as failed calls on restrictive networks.
/// </summary>
public class IceServerParsingTests
{
    [Fact]
    public void Keeps_turn_credentials_as_separate_fields()
    {
        var servers = JanusPeerFactory.ParseIceServers(
            """[{"urls":"turn:relay.example.invalid:3478","username":"example-credential","credential":"example-credential"}]""");

        var server = Assert.Single(servers);
        Assert.Equal("turn:relay.example.invalid:3478", server.urls);
        Assert.Equal("example-credential", server.username);
        Assert.Equal("example-credential", server.credential);
    }

    [Fact]
    public void Leaves_stun_without_credentials()
    {
        // STUN takes no credentials; attaching them produces an unusable server entry.
        var servers = JanusPeerFactory.ParseIceServers(
            """[{"urls":"stun:stun.l.google.com:19302","username":"example-credential","credential":"example-credential"}]""");

        var server = Assert.Single(servers);
        Assert.Equal("stun:stun.l.google.com:19302", server.urls);
        Assert.Null(server.username);
        Assert.Null(server.credential);
    }

    [Fact]
    public void Parses_the_real_server_configuration()
    {
        // Verbatim from ChatServer/appsettings.json.
        var servers = JanusPeerFactory.ParseIceServers(
            """[{"urls":"stun:stun.l.google.com:19302"},{"urls":"turn:relay.example.invalid:3478","username":"example-credential","credential":"example-credential"}]""");

        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:stun.l.google.com:19302", servers[0].urls);
        Assert.Equal("turn:relay.example.invalid:3478", servers[1].urls);
        Assert.Equal("example-credential", servers[1].username);
    }

    [Fact]
    public void Expands_an_array_of_urls()
    {
        var servers = JanusPeerFactory.ParseIceServers(
            """[{"urls":["turn:a.example:3478","turns:b.example:5349"],"username":"u","credential":"p"}]""");

        Assert.Equal(2, servers.Count);
        Assert.All(servers, s => Assert.Equal("u", s.username));
        Assert.Equal("turn:a.example:3478", servers[0].urls);
        Assert.Equal("turns:b.example:5349", servers[1].urls);
    }

    [Fact]
    public void Accepts_the_singular_url_field()
    {
        var servers = JanusPeerFactory.ParseIceServers("""[{"url":"stun:stun.example:3478"}]""");

        Assert.Equal("stun:stun.example:3478", Assert.Single(servers).urls);
    }

    [Fact]
    public void Passes_reserved_characters_through_untouched()
    {
        // Separate fields mean no escaping is needed; the previous transport had to
        // percent-encode these into the URL.
        var servers = JanusPeerFactory.ParseIceServers(
            """[{"urls":"turn:host:3478","username":"user@corp","credential":"p:ss@word"}]""");

        var server = Assert.Single(servers);
        Assert.Equal("user@corp", server.username);
        Assert.Equal("p:ss@word", server.credential);
    }

    [Fact]
    public void Omits_credentials_when_only_one_half_is_present()
    {
        var servers = JanusPeerFactory.ParseIceServers("""[{"urls":"turn:host:3478","username":"u"}]""");

        var server = Assert.Single(servers);
        Assert.Null(server.username);
        Assert.Null(server.credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"urls":[]}]""")]
    public void Falls_back_to_public_stun_on_unusable_input(string? json)
    {
        // A broken ICE config must degrade to "STUN only", never to "no servers", which
        // would make every call fail outright.
        var servers = JanusPeerFactory.ParseIceServers(json);

        Assert.Equal("stun:stun.l.google.com:19302", Assert.Single(servers).urls);
    }
}
