using ChatServer.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace ChatServer.Services;

public static class RoomUnreadQuery
{
    // One grouped statement, including memberships with zero unread messages.
    // JSON matching remains here to avoid a data backfill; it runs only on unread rows.
    public static IQueryable<RoomUnreadState> Build(AppDbContext db, Guid userId, string mentionToken, string mentionAllToken)
    {
        var unread = from membership in db.ChatRoomMembers.AsNoTracking()
                     join message in db.Messages.AsNoTracking() on membership.RoomId equals message.RoomId
                     where membership.UserId == userId && !message.IsDeleted &&
                           message.SenderId != userId && message.SentAt > membership.LastReadAt
                     group message by membership.RoomId into messages
                     select new
                     {
                         RoomId = messages.Key,
                         Count = (int?)messages.Count(),
                         Mentions = (int?)messages.Sum(m => m.MentionedUserIdsJson.Contains(mentionToken) ||
                             m.MentionedUserIdsJson.Contains(mentionAllToken) ? 1 : 0)
                     };

        return from membership in db.ChatRoomMembers.AsNoTracking()
               where membership.UserId == userId
               join summary in unread on membership.RoomId equals summary.RoomId into summaries
               from summary in summaries.DefaultIfEmpty()
               select new RoomUnreadState
               {
                   RoomId = membership.RoomId,
                   LastReadAt = membership.LastReadAt,
                   UnreadCount = Math.Min(summary.Count ?? 0, 99),
                   HasMention = (summary.Mentions ?? 0) > 0
               };
    }
}
