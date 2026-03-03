using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
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

    public async UnaryResult<ChatRoomInfo[]> GetRoomsAsync()
    {
        var cacheKey = "rooms:all";
        var cached = await _cache.GetStringAsync(cacheKey);
        if (cached != null)
        {
            return JsonSerializer.Deserialize<ChatRoomInfo[]>(cached)!;
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

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(rooms),
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

        var room = new Data.Entities.ChatRoomEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
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
            JoinedAt = DateTime.UtcNow
        };

        _db.ChatRooms.Add(room);
        _db.ChatRoomMembers.Add(membership);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync("rooms:all");

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
        await _cache.RemoveAsync("rooms:all");
        return true;
    }

    public async UnaryResult<ServerMemberInfo[]> GetServerMembersAsync()
    {
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

        return users
            .Select(u => new ServerMemberInfo
            {
                UserId = u.Id,
                UserName = u.UserName,
                DisplayName = u.DisplayName,
                AvatarUrl = u.AvatarUrl,
                IsOnline = Hubs.ChatHubImpl.IsUserOnline(u.Id),
                RoomIds = roomIdsByUser.TryGetValue(u.Id, out var roomIds) ? roomIds : [],
                Role = u.GlobalRole
            })
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.UserName, StringComparer.OrdinalIgnoreCase)
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
    {
        var cacheKey = $"messages:{roomId}:{count}:{before?.Ticks}";
        var cached = await _cache.GetStringAsync(cacheKey);
        if (cached != null)
        {
            return JsonSerializer.Deserialize<ChatMessage[]>(cached)!;
        }

        var query = _db.Messages
            .Where(m => m.RoomId == roomId && !m.IsDeleted)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .OrderByDescending(m => m.SentAt)
            .AsQueryable();

        if (before.HasValue)
        {
            query = query.Where(m => m.SentAt < before.Value);
        }

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
                MentionedUserIds = JsonSerializer.Deserialize<Guid[]>(m.MentionedUserIdsJson) ?? Array.Empty<Guid>(),
                Attachments = m.Attachments.Select(a => new AttachmentInfo
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    Url = a.Url
                }).ToArray(),
                IsDeleted = m.IsDeleted
            })
            .ToArrayAsync();

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(messages),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });

        return messages;
    }
}
