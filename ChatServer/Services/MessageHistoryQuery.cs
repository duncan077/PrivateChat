using ChatServer.Data.Entities;
using MagicOnion;
using Shared.Models;

namespace ChatServer.Services;

public static class MessageHistoryQuery
{
    public static IQueryable<MessageEntity> RoomPage(IQueryable<MessageEntity> query, MessageCursor? before, MessageCursor? after)
    {
        Validate(before, after);
        if (before is not null)
            query = query.Where(m => m.SentAt < before.SentAt || (m.SentAt == before.SentAt && m.Id.CompareTo(before.Id) < 0));
        if (after is not null)
            query = query.Where(m => m.SentAt > after.SentAt || (m.SentAt == after.SentAt && m.Id.CompareTo(after.Id) > 0));
        return after is null
            ? query.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
            : query.OrderBy(m => m.SentAt).ThenBy(m => m.Id);
    }

    public static IQueryable<PrivateMessageEntity> PrivatePage(IQueryable<PrivateMessageEntity> query, MessageCursor? before, MessageCursor? after)
    {
        Validate(before, after);
        if (before is not null)
            query = query.Where(m => m.SentAt < before.SentAt || (m.SentAt == before.SentAt && m.Id.CompareTo(before.Id) < 0));
        if (after is not null)
            query = query.Where(m => m.SentAt > after.SentAt || (m.SentAt == after.SentAt && m.Id.CompareTo(after.Id) > 0));
        return after is null
            ? query.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
            : query.OrderBy(m => m.SentAt).ThenBy(m => m.Id);
    }

    private static void Validate(MessageCursor? before, MessageCursor? after)
    {
        if (before is not null && after is not null)
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Use either before or after, not both.");
    }
}
