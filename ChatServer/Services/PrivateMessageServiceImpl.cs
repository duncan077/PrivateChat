using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Serialization;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shared;
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

        // Left as IQueryable so this stays one statement with a subquery. Materializing
        // it first meant a second round trip whose IN list grew with every person the
        // account had ever exchanged a message with.
        var contactIds = _db.PrivateMessages
            .Where(m => m.SenderId == userId || m.RecipientId == userId)
            .Select(m => m.SenderId == userId ? m.RecipientId : m.SenderId)
            .Distinct();

        return await _db.Users
            .AsNoTracking()
            .Where(u => contactIds.Contains(u.Id))
            .Select(u => new UserInfo
            {
                Id = u.Id,
                UserName = u.UserName!,
                DisplayName = u.DisplayName,
                AvatarUrl = u.AvatarUrl
            })
            .ToArrayAsync();
    }

    public async UnaryResult<PrivateMessage[]> SearchMessagesAsync(string query, int count, DateTime? before)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return [];

        count = Math.Clamp(count, 1, ChatLimits.MaxSearchPageSize);
        var userId = Context.GetUserId();

        // Scoped to conversations the caller is part of.
        var search = _db.PrivateMessages
            .AsNoTracking()
            .Where(m => !m.IsDeleted && (m.SenderId == userId || m.RecipientId == userId));

        if (before is { } cursor)
        {
            search = search.Where(m => m.SentAt < cursor);
        }

        var likePattern = MessageSearchQuery.LikePattern(trimmed);
        search = MessageSearchQuery.UseFullText(trimmed)
            ? search.Where(m => EF.Functions.Match(m.Content, trimmed, MySqlMatchSearchMode.NaturalLanguage) > 0)
            : search.Where(m => EF.Functions.Like(m.Content, likePattern, "\\"));

        return await search
            .OrderByDescending(m => m.SentAt)
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
                MentionedUserIds = JsonSerializer.Deserialize(m.MentionedUserIdsJson, ServerJsonContext.Default.GuidArray) ?? Array.Empty<Guid>(),
                IsRead = m.IsRead,
                EditedAt = m.EditedAt,
                ReplyToMessageId = m.ReplyToMessageId
            })
            .ToArrayAsync();
    }

    public async UnaryResult<PrivateUnreadInfo[]> GetUnreadCountsAsync()
    {
        var userId = Context.GetUserId();

        return await _db.PrivateMessages
            .AsNoTracking()
            .Where(m => m.RecipientId == userId && !m.IsRead && !m.IsDeleted)
            .GroupBy(m => m.SenderId)
            .Select(g => new PrivateUnreadInfo
            {
                PeerUserId = g.Key,
                UnreadCount = g.Count()
            })
            .ToArrayAsync();
    }

    public async UnaryResult<PrivateMessage[]> GetMessagesAsync(Guid otherUserId, int count, DateTime? before)
        => await GetMessagesCoreAsync(otherUserId, count, before, null, null);

    public async UnaryResult<PrivateMessage[]> GetMessagePageAsync(Guid otherUserId, int count, MessageCursor? before, MessageCursor? after)
        => await GetMessagesCoreAsync(otherUserId, count, null, before, after);

    private async Task<PrivateMessage[]> GetMessagesCoreAsync(Guid otherUserId, int count, DateTime? legacyBefore, MessageCursor? before, MessageCursor? after)
    {
        var userId = Context.GetUserId();

        // Unclamped, this went straight from the wire into Take() - one call could pull
        // an entire conversation, longtext bodies and all.
        count = Math.Clamp(count, 1, CacheKeys.MaxMessagePageSize);

        var query = MessageHistoryQuery.PrivatePage(_db.PrivateMessages.AsNoTracking()
            .Where(m => !m.IsDeleted &&
                ((m.SenderId == userId && m.RecipientId == otherUserId) ||
                 (m.SenderId == otherUserId && m.RecipientId == userId))), before, after);
        if (legacyBefore.HasValue)
            query = query.Where(m => m.SentAt < legacyBefore.Value);

        var messages = await query
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
                MentionedUserIds = JsonSerializer.Deserialize(m.MentionedUserIdsJson, ServerJsonContext.Default.GuidArray) ?? Array.Empty<Guid>(),
                IsRead = m.IsRead,
                EditedAt = m.EditedAt,
                ReplyToMessageId = m.ReplyToMessageId
            })
            .ToArrayAsync();

        await MessageEnricher.AttachReactionsAsync(_db, messages);
        await MessageEnricher.AttachReplyPreviewsAsync(_db, messages);
        return messages;
    }
}
