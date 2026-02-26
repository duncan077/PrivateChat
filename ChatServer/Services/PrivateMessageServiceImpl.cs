using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Shared.Models;
using Shared.Services;

namespace ChatServer.Services;

[JwtAuthFilter]
public class PrivateMessageServiceImpl : ServiceBase<IPrivateMessageService>, IPrivateMessageService
{
    private readonly AppDbContext _db;

    public PrivateMessageServiceImpl(AppDbContext db)
    {
        _db = db;
    }

    public async UnaryResult<UserInfo[]> GetContactsAsync()
    {
        var userId = Context.GetUserId();

        var contactIds = await _db.PrivateMessages
            .Where(m => m.SenderId == userId || m.RecipientId == userId)
            .Select(m => m.SenderId == userId ? m.RecipientId : m.SenderId)
            .Distinct()
            .ToListAsync();

        var contacts = await _db.Users
            .Where(u => contactIds.Contains(u.Id))
            .Select(u => new UserInfo
            {
                Id = u.Id,
                UserName = u.UserName!,
                DisplayName = u.DisplayName,
                AvatarUrl = u.AvatarUrl
            })
            .ToArrayAsync();

        return contacts;
    }

    public async UnaryResult<PrivateMessage[]> GetMessagesAsync(Guid otherUserId, int count, DateTime? before)
    {
        var userId = Context.GetUserId();

        var query = _db.PrivateMessages
            .Where(m => (m.SenderId == userId && m.RecipientId == otherUserId) ||
                        (m.SenderId == otherUserId && m.RecipientId == userId))
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .OrderByDescending(m => m.SentAt)
            .AsQueryable();

        if (before.HasValue)
        {
            query = query.Where(m => m.SentAt < before.Value);
        }

        return await query
            .Take(count)
            .Select(m => new PrivateMessage
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = m.Sender.UserName!,
                RecipientId = m.RecipientId,
                Content = m.Content,
                SentAt = m.SentAt,
                Attachments = m.Attachments.Select(a => new AttachmentInfo
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    Url = a.Url
                }).ToArray(),
                MentionedUserIds = JsonSerializer.Deserialize<Guid[]>(m.MentionedUserIdsJson) ?? Array.Empty<Guid>(),
                IsRead = m.IsRead
            })
            .ToArrayAsync();
    }
}
