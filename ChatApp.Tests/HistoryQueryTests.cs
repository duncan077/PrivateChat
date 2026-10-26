using ChatServer.Data;
using ChatServer.Data.Entities;
using ChatServer.Services;
using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace ChatApp.Tests;

public class HistoryQueryTests
{
    private static AppDbContext MariaDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql("Server=localhost;Database=translation_only;User=test;Password=test", new MariaDbServerVersion(new Version(11, 4))).Options);

    [Fact]
    public void Cursor_queries_translate_for_the_production_provider()
    {
        using var db = MariaDb();
        var cursor = new MessageCursor { SentAt = DateTime.UtcNow, Id = Guid.NewGuid() };
        foreach (var after in new[] { false, true })
        {
            var room = MessageHistoryQuery.RoomPage(db.Messages, after ? null : cursor, after ? cursor : null).Take(50).ToQueryString();
            var dm = MessageHistoryQuery.PrivatePage(db.PrivateMessages, after ? null : cursor, after ? cursor : null).Take(50).ToQueryString();
            Assert.Contains("ORDER BY", room);
            Assert.Contains("ORDER BY", dm);
            Assert.Contains("LIMIT", room);
            Assert.Contains("LIMIT", dm);
        }
    }

    [Fact]
    public void Unread_summary_translates_as_one_grouped_statement()
    {
        using var db = MariaDb();
        var sql = RoomUnreadQuery.Build(db, Guid.NewGuid(), "mention", "all").ToQueryString();
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("LEFT JOIN", sql);
        Assert.Contains("SUM(", sql);
    }

    [Fact]
    public void Equal_timestamp_pages_have_no_gaps_in_both_directions()
    {
        var time = DateTime.UtcNow;
        var all = Enumerable.Range(1, 125).Select(i => new MessageEntity
        {
            Id = Guid.Parse($"{i:x8}-0000-0000-0000-000000000000"), SentAt = time
        }).ToArray();
        var seen = new List<Guid>();
        MessageCursor? before = null;
        while (true)
        {
            var page = MessageHistoryQuery.RoomPage(all.AsQueryable(), before, null).Take(50).ToArray();
            if (page.Length == 0) break;
            seen.AddRange(page.Select(m => m.Id));
            before = new() { Id = page[^1].Id, SentAt = page[^1].SentAt };
        }
        Assert.Equal(all.Reverse().Select(m => m.Id), seen);
        var after = new MessageCursor { Id = all[49].Id, SentAt = time };
        Assert.Equal(all.Skip(50).Select(m => m.Id), MessageHistoryQuery.RoomPage(all.AsQueryable(), null, after).Select(m => m.Id));

        var dms = all.Select(m => new PrivateMessageEntity { Id = m.Id, SentAt = m.SentAt }).AsQueryable();
        Assert.Equal(all.Skip(50).Select(m => m.Id), MessageHistoryQuery.PrivatePage(dms, null, after).Select(m => m.Id));
        Assert.Equal(all.Take(49).Reverse().Select(m => m.Id), MessageHistoryQuery.PrivatePage(dms, after, null).Select(m => m.Id));
    }

    [Fact]
    public void Ambiguous_cursor_direction_is_rejected()
    {
        var cursor = new MessageCursor { SentAt = DateTime.UtcNow, Id = Guid.NewGuid() };
        Assert.Throws<MagicOnion.ReturnStatusException>(() => MessageHistoryQuery.RoomPage(Array.Empty<MessageEntity>().AsQueryable(), cursor, cursor));
        Assert.Throws<MagicOnion.ReturnStatusException>(() => MessageHistoryQuery.PrivatePage(Array.Empty<PrivateMessageEntity>().AsQueryable(), cursor, cursor));
    }

    [Fact]
    public async Task Unread_summary_preserves_empty_rooms_caps_counts_and_filters_messages()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = Guid.NewGuid(); var peer = Guid.NewGuid();
        var room = Guid.NewGuid(); var emptyRoom = Guid.NewGuid(); var unrelatedRoom = Guid.NewGuid();
        var readAt = DateTime.UtcNow;
        db.ChatRoomMembers.AddRange(new ChatRoomMember { Id = Guid.NewGuid(), UserId = me, RoomId = room, LastReadAt = readAt },
            new ChatRoomMember { Id = Guid.NewGuid(), UserId = me, RoomId = emptyRoom, LastReadAt = readAt });
        db.Messages.AddRange(Enumerable.Range(0, 120).Select(_ => new MessageEntity
        { Id = Guid.NewGuid(), RoomId = room, SenderId = peer, SentAt = readAt.AddSeconds(1) }));
        db.Messages.AddRange(
            new MessageEntity { Id = Guid.NewGuid(), RoomId = room, SenderId = peer, SentAt = readAt.AddSeconds(2), MentionedUserIdsJson = $"[\"{Guid.Empty:D}\"]" },
            new MessageEntity { Id = Guid.NewGuid(), RoomId = emptyRoom, SenderId = me, SentAt = readAt.AddSeconds(1) },
            new MessageEntity { Id = Guid.NewGuid(), RoomId = emptyRoom, SenderId = peer, SentAt = readAt },
            new MessageEntity { Id = Guid.NewGuid(), RoomId = emptyRoom, SenderId = peer, SentAt = readAt.AddSeconds(1), IsDeleted = true },
            new MessageEntity { Id = Guid.NewGuid(), RoomId = unrelatedRoom, SenderId = peer, SentAt = readAt.AddSeconds(1) });
        await db.SaveChangesAsync();
        var states = await RoomUnreadQuery.Build(db, me, $"\"{me:D}\"", $"\"{Guid.Empty:D}\"").ToArrayAsync();
        Assert.Equal(2, states.Length);
        Assert.Equal(99, states.Single(s => s.RoomId == room).UnreadCount);
        Assert.True(states.Single(s => s.RoomId == room).HasMention);
        Assert.Equal(0, states.Single(s => s.RoomId == emptyRoom).UnreadCount);
        Assert.False(states.Single(s => s.RoomId == emptyRoom).HasMention);
    }
}
