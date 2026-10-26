using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Serialization;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shared;
using Shared.Models;
using Shared.Requests;
using Shared.Services;

namespace ChatServer.Services;

[JwtAuthFilter]
public class ChatRoomServiceImpl : ServiceBase<IChatRoomService>, IChatRoomService
{
    private readonly AppDbContext _db;
    private readonly IDistributedCache _cache;

    public ChatRoomServiceImpl(AppDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    // Cache payloads move as UTF-8 bytes, not strings. The string API round-trips every
    // value through a UTF-16 System.String on both read and write - two extra copies and
    // two transcodes per operation, on payloads large enough to matter.

    public async UnaryResult<ChatRoomInfo[]> GetRoomsAsync()
    {
        var cacheKey = CacheKeys.Rooms;
        var cached = await _cache.GetAsync(cacheKey);
        if (cached != null)
        {
            return JsonSerializer.Deserialize(cached, ServerJsonContext.Default.ChatRoomInfoArray)!;
        }

        var rooms = await _db.ChatRooms
            .Include(r => r.Members)
            .Select(r => new ChatRoomInfo
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                OwnerId = r.OwnerId,
                MemberCount = r.Members.Count,
                IsVoiceEnabled = r.IsVoiceEnabled,
                IsVideoEnabled = r.IsVideoEnabled,
                CreatedAt = r.CreatedAt
            })
            .ToArrayAsync();

        await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(rooms, ServerJsonContext.Default.ChatRoomInfoArray),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });

        return rooms;
    }

    public async UnaryResult<ChatRoomInfo> GetRoomAsync(Guid roomId)
    {
        var room = await _db.ChatRooms
            .Include(r => r.Members)
            .FirstOrDefaultAsync(r => r.Id == roomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        return new ChatRoomInfo
        {
            Id = room.Id,
            Name = room.Name,
            Description = room.Description,
            OwnerId = room.OwnerId,
            MemberCount = room.Members.Count,
            IsVoiceEnabled = room.IsVoiceEnabled,
            IsVideoEnabled = room.IsVideoEnabled,
            CreatedAt = room.CreatedAt
        };
    }

    public async UnaryResult<ChatRoomInfo> CreateRoomAsync(CreateRoomRequest request)
    {
        var userId = Context.GetUserId();

        // Name and Description are bounded columns now, so oversized input has to be
        // rejected here with a clear status rather than surfacing as a DbUpdateException.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Room name cannot be empty");
        }

        if (name.Length > CacheKeys.MaxRoomNameLength)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                $"Room name must be {CacheKeys.MaxRoomNameLength} characters or fewer");
        }

        if ((request.Description?.Length ?? 0) > CacheKeys.MaxRoomDescriptionLength)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                $"Room description must be {CacheKeys.MaxRoomDescriptionLength} characters or fewer");
        }

        var room = new Data.Entities.ChatRoomEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description,
            OwnerId = userId,
            IsVoiceEnabled = request.IsVoiceEnabled,
            IsVideoEnabled = request.IsVideoEnabled,
            CreatedAt = DateTime.UtcNow
        };

        var membership = new Data.Entities.ChatRoomMember
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = userId,
            Role = UserRole.Member,
            JoinedAt = DateTime.UtcNow,
            LastReadAt = DateTime.UtcNow
        };

        _db.ChatRooms.Add(room);
        _db.ChatRoomMembers.Add(membership);
        await _db.SaveChangesAsync();
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        return new ChatRoomInfo
        {
            Id = room.Id,
            Name = room.Name,
            Description = room.Description,
            OwnerId = room.OwnerId,
            MemberCount = 1,
            IsVoiceEnabled = room.IsVoiceEnabled,
            IsVideoEnabled = room.IsVideoEnabled,
            CreatedAt = room.CreatedAt
        };
    }

    public async UnaryResult<bool> DeleteRoomAsync(Guid roomId)
    {
        var userId = Context.GetUserId();
        var room = await _db.ChatRooms.FindAsync(roomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        if (room.OwnerId != userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only the owner can delete the room");
        }

        _db.ChatRooms.Remove(room);
        await _db.SaveChangesAsync();
        await CacheKeys.InvalidateDirectoryAsync(_cache);
        return true;
    }

    public async UnaryResult<ChatRoomInfo> RenameRoomAsync(RenameRoomRequest request)
    {
        var userId = Context.GetUserId();
        var room = await _db.ChatRooms.Include(r => r.Members).FirstOrDefaultAsync(r => r.Id == request.RoomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        if (room.OwnerId != userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only the owner can rename the room");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Room name cannot be empty");
        }

        if (request.Name.Trim().Length > CacheKeys.MaxRoomNameLength)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                $"Room name must be {CacheKeys.MaxRoomNameLength} characters or fewer");
        }

        room.Name = request.Name.Trim();
        await _db.SaveChangesAsync();
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        return new ChatRoomInfo
        {
            Id = room.Id,
            Name = room.Name,
            Description = room.Description,
            OwnerId = room.OwnerId,
            MemberCount = room.Members.Count,
            IsVoiceEnabled = room.IsVoiceEnabled,
            IsVideoEnabled = room.IsVideoEnabled,
            CreatedAt = room.CreatedAt
        };
    }

    public async UnaryResult<ServerMemberInfo[]> GetServerMembersAsync()
    {
        // Nothing here varies per caller except IsOnline, yet clients poll this every
        // 20s and several broadcast handlers re-request it - so one kick or role change
        // used to fan out into N clients each reading AspNetUsers and ChatRoomMembers
        // end to end. The directory is cached globally and presence is stamped on after,
        // since presence is a free process-static lookup and must never be served stale.
        var directory = await GetMemberDirectoryAsync();

        foreach (var member in directory)
        {
            member.IsOnline = Hubs.ChatHubImpl.IsUserOnline(member.UserId);
        }

        return directory;
    }

    /// <summary>
    /// The presence-independent part of the member list. Cached with <c>IsOnline</c>
    /// left at its default - callers stamp it themselves.
    /// </summary>
    private async Task<ServerMemberInfo[]> GetMemberDirectoryAsync()
    {
        var cached = await _cache.GetAsync(CacheKeys.ServerMembers);
        if (cached != null)
        {
            return JsonSerializer.Deserialize(cached, ServerJsonContext.Default.ServerMemberInfoArray)!;
        }

        // Captured before the read: if anything invalidates while these queries are in
        // flight, the snapshot below is already stale and must not be written back.
        var generation = CacheKeys.DirectoryGeneration;

        var users = await _db.Users
            .AsNoTracking()
            .Select(u => new
            {
                u.Id,
                UserName = u.UserName!,
                u.DisplayName,
                u.AvatarUrl,
                u.GlobalRole
            })
            .ToArrayAsync();

        var memberships = await _db.ChatRoomMembers
            .AsNoTracking()
            .Select(m => new { m.UserId, m.RoomId })
            .ToArrayAsync();

        var roomIdsByUser = memberships
            .GroupBy(m => m.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.RoomId).Distinct().ToArray());

        var directory = users
            .Select(u => new ServerMemberInfo
            {
                UserId = u.Id,
                UserName = u.UserName,
                DisplayName = u.DisplayName,
                AvatarUrl = u.AvatarUrl,
                RoomIds = roomIdsByUser.TryGetValue(u.Id, out var roomIds) ? roomIds : [],
                Role = u.GlobalRole
            })
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Short TTL: every mutation path invalidates explicitly, so this only bounds
        // the damage from a mutation site that gets added later and forgets to.
        // Skipped entirely if an invalidation landed while the queries were running -
        // storing this now would pin pre-mutation data for the whole TTL.
        if (CacheKeys.DirectoryGeneration == generation)
        {
            await _cache.SetAsync(
                CacheKeys.ServerMembers,
                JsonSerializer.SerializeToUtf8Bytes(directory, ServerJsonContext.Default.ServerMemberInfoArray),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        }

        return directory;
    }

    public async UnaryResult<RoomCallParticipantsInfo[]> GetRoomCallParticipantsAsync()
    {
        var userId = Context.GetUserId();
        var roomIds = await _db.ChatRoomMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.RoomId)
            .Distinct()
            .ToArrayAsync();

        return roomIds
            .Select(roomId => new RoomCallParticipantsInfo
            {
                RoomId = roomId,
                ParticipantUserIds = Hubs.ChatHubImpl.GetVoiceParticipantsSnapshot(roomId)
            })
            .ToArray();
    }

    public async UnaryResult<ChatMemberInfo[]> GetRoomMembersAsync(Guid roomId)
    {
        return await _db.ChatRoomMembers
            .Where(m => m.RoomId == roomId)
            .Include(m => m.User)
            .Select(m => new ChatMemberInfo
            {
                UserId = m.UserId,
                UserName = m.User.UserName!,
                DisplayName = m.User.DisplayName,
                AvatarUrl = m.User.AvatarUrl,
                Role = m.User.GlobalRole,
                JoinedAt = m.JoinedAt
            })
            .ToArrayAsync();
    }

    public async UnaryResult<ChatMessage[]> GetRoomMessagesAsync(Guid roomId, int count, DateTime? before)
        => await GetRoomMessagesCoreAsync(roomId, count, before, null, null);

    public async UnaryResult<ChatMessage[]> GetRoomMessagePageAsync(Guid roomId, int count, MessageCursor? before, MessageCursor? after)
        => await GetRoomMessagesCoreAsync(roomId, count, null, before, after);

    private async Task<ChatMessage[]> GetRoomMessagesCoreAsync(Guid roomId, int count, DateTime? legacyBefore, MessageCursor? before, MessageCursor? after)
    {
        // count arrives off the wire. Unclamped it flowed straight into Take() and into
        // the cache key, so one call could materialize a room's entire history, serialize
        // it, and push it into Redis as a single value.
        count = Math.Clamp(count, 1, CacheKeys.MaxMessagePageSize);

        // History is membership-gated: before this check any authenticated account
        // could read any room's messages by id. Runs before the cache read so the
        // cached tail is just as protected as a DB hit.
        var userId = Context.GetUserId();
        var isMember = await _db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId);
        if (!isMember)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not a member of this room");
        }

        // Only the live tail is cached, under a key built from nothing but the room id.
        // The old key embedded count and the cursor while invalidation removed a single
        // hardcoded ":50:" shape - so entries for any other shape were written once and
        // never reachable again, and a client choosing its own page size could mint
        // unbounded distinct entries. Historical pages skip the cache; with the
        // (RoomId, IsDeleted, SentAt) index a miss is an index range scan.
        var isCacheable = legacyBefore is null && before is null && after is null && count == CacheKeys.DefaultMessagePageSize;
        var cacheKey = CacheKeys.RoomMessages(roomId);

        if (isCacheable)
        {
            var cached = await _cache.GetAsync(cacheKey);
            if (cached != null)
            {
                return JsonSerializer.Deserialize(cached, ServerJsonContext.Default.ChatMessageArray)!;
            }
        }

        var query = MessageHistoryQuery.RoomPage(
            _db.Messages.AsNoTracking().Where(m => m.RoomId == roomId && !m.IsDeleted), before, after);
        if (legacyBefore.HasValue)
            query = query.Where(m => m.SentAt < legacyBefore.Value);

        var messages = await query
            .Take(count)
            .Select(m => new ChatMessage
            {
                Id = m.Id,
                RoomId = m.RoomId,
                SenderId = m.SenderId,
                SenderName = m.Sender.UserName!,
                Content = m.Content,
                MessageType = m.MessageType,
                SentAt = m.SentAt,
                MentionedUserIds = JsonSerializer.Deserialize(m.MentionedUserIdsJson, ServerJsonContext.Default.GuidArray) ?? Array.Empty<Guid>(),
                Attachments = m.Attachments.Select(a => new AttachmentInfo
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    Url = a.Url
                }).ToArray(),
                IsDeleted = m.IsDeleted,
                EditedAt = m.EditedAt,
                ReplyToMessageId = m.ReplyToMessageId
            })
            .ToArrayAsync();

        // Before the cache write, so the cached tail carries these too.
        await MessageEnricher.AttachReactionsAsync(_db, messages);
        await MessageEnricher.AttachReplyPreviewsAsync(_db, messages);

        if (isCacheable)
        {
            await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(messages, ServerJsonContext.Default.ChatMessageArray),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        }

        return messages;
    }

    public async UnaryResult<SearchResultInfo[]> SearchMessagesAsync(SearchMessagesRequest request)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0) return [];

        var count = Math.Clamp(request.Count, 1, ChatLimits.MaxSearchPageSize);
        var userId = Context.GetUserId();

        // Join through membership rather than filtering after the fact: results must
        // never include rooms the caller cannot otherwise read.
        var memberRoomIds = _db.ChatRoomMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.RoomId);

        var search = _db.Messages
            .AsNoTracking()
            .Where(m => !m.IsDeleted && memberRoomIds.Contains(m.RoomId));

        if (request.RoomId is { } roomId)
        {
            search = search.Where(m => m.RoomId == roomId);
        }

        if (request.Before is { } before)
        {
            search = search.Where(m => m.SentAt < before);
        }

        var likePattern = MessageSearchQuery.LikePattern(query);
        search = MessageSearchQuery.UseFullText(query)
            // Natural-language mode ignores boolean operators, so raw user input is safe
            // to pass through without sanitizing. Match returns a relevance score, so a
            // hit is any score above zero.
            ? search.Where(m => EF.Functions.Match(m.Content, query, MySqlMatchSearchMode.NaturalLanguage) > 0)
            : search.Where(m => EF.Functions.Like(m.Content, likePattern, "\\"));

        var results = await search
            .OrderByDescending(m => m.SentAt)
            .Take(count)
            .Select(m => new SearchResultInfo
            {
                RoomName = m.Room.Name,
                Message = new ChatMessage
                {
                    Id = m.Id,
                    RoomId = m.RoomId,
                    SenderId = m.SenderId,
                    SenderName = m.Sender.UserName!,
                    Content = m.Content,
                    MessageType = m.MessageType,
                    SentAt = m.SentAt,
                    MentionedUserIds = JsonSerializer.Deserialize(m.MentionedUserIdsJson, ServerJsonContext.Default.GuidArray) ?? Array.Empty<Guid>(),
                    Attachments = m.Attachments.Select(a => new AttachmentInfo
                    {
                        Id = a.Id,
                        FileName = a.FileName,
                        ContentType = a.ContentType,
                        SizeBytes = a.SizeBytes,
                        Url = a.Url
                    }).ToArray(),
                    IsDeleted = m.IsDeleted,
                    EditedAt = m.EditedAt,
                    ReplyToMessageId = m.ReplyToMessageId
                }
            })
            .ToArrayAsync();

        return results;
    }

    public async UnaryResult<RoomUnreadState[]> GetRoomUnreadStatesAsync()
    {
        var userId = Context.GetUserId();

        // Quoted guid tokens, because MentionedUserIdsJson is a JSON array of quoted
        // guids - matching the bare guid could hit a substring of another value.
        var mentionToken = $"\"{userId:D}\"";
        // The client uses Guid.Empty as its @all sentinel.
        var mentionAllToken = $"\"{Guid.Empty:D}\"";

        return await RoomUnreadQuery.Build(_db, userId, mentionToken, mentionAllToken).ToArrayAsync();
    }

    public async UnaryResult<ChatMessage[]> GetRoomMessagesAroundAsync(Guid roomId, Guid messageId, int contextCount)
    {
        contextCount = Math.Clamp(contextCount, 1, CacheKeys.MaxMessagePageSize / 2);

        var userId = Context.GetUserId();
        var isMember = await _db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId);
        if (!isMember)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not a member of this room");
        }

        var pivot = await _db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId && m.RoomId == roomId && !m.IsDeleted)
            .Select(m => new { m.Id, m.SentAt })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        var cursor = new MessageCursor { Id = pivot.Id, SentAt = pivot.SentAt };
        var roomMessages = _db.Messages.AsNoTracking().Where(m => m.RoomId == roomId && !m.IsDeleted);
        var beforeIds = await MessageHistoryQuery.RoomPage(roomMessages, cursor, null)
            .Take(contextCount).Select(m => m.Id).ToArrayAsync();
        var afterIds = await MessageHistoryQuery.RoomPage(roomMessages, null, cursor)
            .Take(contextCount).Select(m => m.Id).ToArrayAsync();

        var windowIds = beforeIds.Append(pivot.Id).Concat(afterIds).Distinct().ToArray();

        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => windowIds.Contains(m.Id))
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .OrderBy(m => m.SentAt).ThenBy(m => m.Id)
            .Select(m => new ChatMessage
            {
                Id = m.Id,
                RoomId = m.RoomId,
                SenderId = m.SenderId,
                SenderName = m.Sender.UserName!,
                Content = m.Content,
                MessageType = m.MessageType,
                SentAt = m.SentAt,
                MentionedUserIds = JsonSerializer.Deserialize(m.MentionedUserIdsJson, ServerJsonContext.Default.GuidArray) ?? Array.Empty<Guid>(),
                Attachments = m.Attachments.Select(a => new AttachmentInfo
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    Url = a.Url
                }).ToArray(),
                IsDeleted = m.IsDeleted,
                EditedAt = m.EditedAt,
                ReplyToMessageId = m.ReplyToMessageId
            })
            .ToArrayAsync();

        await MessageEnricher.AttachReactionsAsync(_db, messages);
        await MessageEnricher.AttachReplyPreviewsAsync(_db, messages);

        // Deliberately uncached: an arbitrary window would mint unbounded cache entries.
        return messages;
    }
}
