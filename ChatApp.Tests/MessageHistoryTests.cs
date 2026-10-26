using ChatAppBlazor.State;
using Shared.Models;

namespace ChatApp.Tests;

public class MessageHistoryTests
{
    private static ChatMessage Message(int n) => new()
    {
        Id = Guid.Parse($"{n:x8}-0000-0000-0000-000000000000"),
        SentAt = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
        Content = n.ToString()
    };
    private static MessageCursor Position(ChatMessage m) => new() { Id = m.Id, SentAt = m.SentAt };

    [Fact]
    public void Merge_preserves_live_objects_and_orders_equal_timestamps()
    {
        var live = Message(3);
        live.Content = "Edited during request";
        var messages = new List<ChatMessage> { live };
        Assert.Equal(3, MessageHistory.Merge(messages, new[] { Message(5), Message(1), Message(3), Message(2), Message(2) }, Position));
        Assert.Equal(new[] { 1, 2, 3, 5 }.Select(n => Message(n).Id), messages.Select(m => m.Id));
        Assert.Same(live, messages[2]);
        Assert.Equal("Edited during request", messages[2].Content);
    }

    [Fact]
    public async Task Recovery_fetches_every_page_and_deduplicates_live_arrivals()
    {
        var messages = new List<ChatMessage> { Message(1) };
        var server = Enumerable.Range(2, 125).Select(Message).ToArray();
        var calls = 0;
        await MessageHistory.RecoverAsync(Position(messages[0]), cursor =>
        {
            calls++;
            MessageHistory.Merge(messages, new[] { Message(126) }, Position);
            return Task.FromResult(server.Where(m => MessageHistory.Compare(Position(m), cursor!) > 0).Take(50).ToArray());
        }, Position, () => true, page => MessageHistory.Merge(messages, page, Position));
        Assert.Equal(3, calls);
        Assert.Equal(126, messages.Count);
        Assert.Equal(Enumerable.Range(1, 126).Select(n => Message(n).Id), messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Recovery_discards_response_after_conversation_changes()
    {
        var current = true;
        var merged = false;
        await MessageHistory.RecoverAsync(Position(Message(1)), cursor =>
        {
            current = false;
            return Task.FromResult(new[] { Message(2) });
        }, Position, () => current, _ => merged = true);
        Assert.False(merged);
    }

    [Fact]
    public async Task Empty_conversation_loads_one_tail_page()
    {
        var calls = 0;
        await MessageHistory.RecoverAsync<ChatMessage>(null, cursor =>
        {
            calls++;
            return Task.FromResult(Enumerable.Range(1, 50).Reverse().Select(Message).ToArray());
        }, Position, () => true, _ => { });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Recovery_rejects_nonadvancing_cursor()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => MessageHistory.RecoverAsync(Position(Message(50)),
            _ => Task.FromResult(Enumerable.Range(1, 50).Select(Message).ToArray()), Position, () => true, _ => { }));
    }
}
