using System.Collections.Concurrent;
using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Data.Entities;
using ChatServer.Services;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using MagicOnion.Server.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Shared.Hubs;
using Shared.Models;
using Shared.Requests;

namespace ChatServer.Hubs;

public class ChatHubImpl : StreamingHubBase<IChatHub, IChatHubReceiver>, IChatHub
{
    private static readonly ConcurrentDictionary<Guid, HashSet<Guid>> _onlineUsers = new();
    private static readonly ConcurrentDictionary<Guid, HashSet<Guid>> _voiceVideoUsers = new();

    private readonly AppDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly JwtTokenService _jwt;
    private readonly JanusService _janus;
    private readonly IConfiguration _config;

    private Guid _userId;
    private string _userName = string.Empty;
    private readonly HashSet<Guid> _joinedRooms = [];

    public ChatHubImpl(AppDbContext db, IDistributedCache cache, JwtTokenService jwt, JanusService janus, IConfiguration config)
    {
        _db = db;
        _cache = cache;
        _jwt = jwt;
        _janus = janus;
        _config = config;
    }

    protected override async ValueTask OnConnecting()
    {
        var httpContext = Context.CallContext.GetHttpContext();
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.Unauthenticated, "Authentication required");
        }

        _userId = Context.GetUserId();
        _userName = Context.GetUserName();

        _onlineUsers.AddOrUpdate(_userId,
            _ => [ConnectionId],
            (_, set) => { lock (set) { set.Add(ConnectionId); } return set; });
    }

    protected override async ValueTask OnDisconnected()
    {
        foreach (var roomId in _joinedRooms)
        {
            await RemoveFromRoomGroupAsync(roomId);
        }

        if (_onlineUsers.TryGetValue(_userId, out var connections))
        {
            lock (connections)
            {
                connections.Remove(ConnectionId);
                if (connections.Count == 0)
                {
                    _onlineUsers.TryRemove(_userId, out _);
                }
            }
        }

        foreach (var roomId in _joinedRooms)
        {
            if (_voiceVideoUsers.TryGetValue(roomId, out var voiceUsers))
            {
                lock (voiceUsers)
                {
                    voiceUsers.Remove(_userId);
                }
            }
        }
    }

    public async ValueTask JoinRoomAsync(Guid roomId)
    {
        var room = await _db.ChatRooms.FindAsync(roomId);
        if (room == null) throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        var existingMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == _userId);

        if (existingMembership == null)
        {
            var membership = new ChatRoomMember
            {
                Id = Guid.NewGuid(),
                RoomId = roomId,
                UserId = _userId,
                Role = UserRole.Member,
                JoinedAt = DateTime.UtcNow
            };
            _db.ChatRoomMembers.Add(membership);
            await _db.SaveChangesAsync();
            await _cache.RemoveAsync("rooms:all");
        }

        var group = await Group.AddAsync($"room:{roomId}");
        _joinedRooms.Add(roomId);

        var user = await _db.Users.FindAsync(_userId);
        var memberInfo = new ChatMemberInfo
        {
            UserId = _userId,
            UserName = _userName,
            DisplayName = user?.DisplayName ?? _userName,
            AvatarUrl = user?.AvatarUrl,
            Role = existingMembership?.Role ?? UserRole.Member,
            JoinedAt = existingMembership?.JoinedAt ?? DateTime.UtcNow,
            IsOnline = true
        };

        group.All.OnUserJoinedRoom(roomId, memberInfo);
    }

    public async ValueTask LeaveRoomAsync(Guid roomId)
    {
        await RemoveFromRoomGroupAsync(roomId);
        _joinedRooms.Remove(roomId);
    }

    public async ValueTask DeleteRoomAsync(Guid roomId)
    {
        var room = await _db.ChatRooms.FindAsync(roomId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, "Room not found");

        if (room.OwnerId != _userId)
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Only the owner can delete the room");

        _db.ChatRooms.Remove(room);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync("rooms:all");

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnRoomDeleted(roomId);
    }

    public async ValueTask SendMessageAsync(Guid roomId, SendMessageRequest request)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        var messageEntity = new MessageEntity
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = _userId,
            Content = request.Content,
            MessageType = ChatMessageType.Text,
            SentAt = DateTime.UtcNow,
            MentionedUserIdsJson = JsonSerializer.Serialize(request.MentionedUserIds)
        };

        foreach (var att in request.Attachments)
        {
            messageEntity.Attachments.Add(new AttachmentEntity
            {
                Id = att.Id,
                FileName = att.FileName,
                ContentType = att.ContentType,
                SizeBytes = att.SizeBytes,
                Url = att.Url
            });
        }

        _db.Messages.Add(messageEntity);
        await _db.SaveChangesAsync();

        var chatMessage = new ChatMessage
        {
            Id = messageEntity.Id,
            RoomId = roomId,
            SenderId = _userId,
            SenderName = _userName,
            Content = request.Content,
            MessageType = ChatMessageType.Text,
            SentAt = messageEntity.SentAt,
            MentionedUserIds = request.MentionedUserIds,
            Attachments = request.Attachments
        };

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnMessageReceived(chatMessage);

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask DeleteMessageAsync(Guid roomId, Guid messageId)
    {
        var membership = await GetMembershipOrThrowAsync(roomId);
        var message = await _db.Messages.FindAsync(messageId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (message.SenderId != _userId && membership.Role < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        message.IsDeleted = true;
        await _db.SaveChangesAsync();

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnMessageDeleted(roomId, messageId);

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask KickUserAsync(Guid roomId, Guid userId)
    {
        var membership = await GetMembershipOrThrowAsync(roomId);
        if (membership.Role < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in room");

        if (targetMembership.Role >= membership.Role)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot kick user with equal or higher role");
        }

        _db.ChatRoomMembers.Remove(targetMembership);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync("rooms:all");

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserKicked(roomId, userId, "Kicked by moderator");
    }

    public async ValueTask MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        var membership = await GetMembershipOrThrowAsync(sourceRoomId);
        if (membership.Role < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetRoom = await _db.ChatRooms.FindAsync(targetRoomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Target room not found");

        var sourceMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == sourceRoomId && m.UserId == userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in source room");

        _db.ChatRoomMembers.Remove(sourceMembership);

        var existingTarget = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == targetRoomId && m.UserId == userId);

        if (existingTarget == null)
        {
            _db.ChatRoomMembers.Add(new ChatRoomMember
            {
                Id = Guid.NewGuid(),
                RoomId = targetRoomId,
                UserId = userId,
                Role = UserRole.Member,
                JoinedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        await _cache.RemoveAsync("rooms:all");

        var sourceGroup = await Group.AddAsync($"room:{sourceRoomId}");
        sourceGroup.All.OnUserMoved(sourceRoomId, targetRoomId, userId);

        var targetGroup = await Group.AddAsync($"room:{targetRoomId}");
        var user = await _db.Users.FindAsync(userId);
        if (user != null)
        {
            targetGroup.All.OnUserJoinedRoom(targetRoomId, new ChatMemberInfo
            {
                UserId = userId,
                UserName = user.UserName!,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                Role = UserRole.Member,
                JoinedAt = DateTime.UtcNow,
                IsOnline = _onlineUsers.ContainsKey(userId)
            });
        }
    }

    public async ValueTask SetUserRoleAsync(Guid roomId, Guid userId, UserRole role)
    {
        var membership = await GetMembershipOrThrowAsync(roomId);
        if (membership.Role < UserRole.Admin)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only admins and owners can change roles");
        }

        var targetMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in room");

        if (role >= membership.Role)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot assign a role equal to or higher than your own");
        }

        targetMembership.Role = role;
        await _db.SaveChangesAsync();

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserRoleChanged(roomId, userId, role);
    }

    public async ValueTask SendPrivateMessageAsync(Guid recipientId, SendMessageRequest request)
    {
        var recipient = await _db.Users.FindAsync(recipientId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Recipient not found");

        var pmEntity = new PrivateMessageEntity
        {
            Id = Guid.NewGuid(),
            SenderId = _userId,
            RecipientId = recipientId,
            Content = request.Content,
            SentAt = DateTime.UtcNow,
            MentionedUserIdsJson = JsonSerializer.Serialize(request.MentionedUserIds)
        };

        foreach (var att in request.Attachments)
        {
            pmEntity.Attachments.Add(new AttachmentEntity
            {
                Id = att.Id,
                FileName = att.FileName,
                ContentType = att.ContentType,
                SizeBytes = att.SizeBytes,
                Url = att.Url
            });
        }

        _db.PrivateMessages.Add(pmEntity);
        await _db.SaveChangesAsync();

        var privateMessage = new PrivateMessage
        {
            Id = pmEntity.Id,
            SenderId = _userId,
            SenderName = _userName,
            RecipientId = recipientId,
            Content = request.Content,
            SentAt = pmEntity.SentAt,
            Attachments = request.Attachments,
            MentionedUserIds = request.MentionedUserIds
        };

        var dmGroup = await Group.AddAsync($"dm:{GetDmKey(_userId, recipientId)}");
        dmGroup.All.OnPrivateMessageReceived(privateMessage);
    }

    public async ValueTask MarkPrivateMessageReadAsync(Guid senderId)
    {
        var unread = await _db.PrivateMessages
            .Where(m => m.SenderId == senderId && m.RecipientId == _userId && !m.IsRead)
            .ToListAsync();

        foreach (var msg in unread)
        {
            msg.IsRead = true;
        }

        await _db.SaveChangesAsync();

        var dmGroup = await Group.AddAsync($"dm:{GetDmKey(_userId, senderId)}");
        dmGroup.All.OnPrivateMessagesRead(_userId);
    }

    public async ValueTask<JanusSessionInfo> JoinVoiceVideoAsync(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        var room = await _db.ChatRooms.FindAsync(roomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        if (!room.IsVoiceEnabled && !room.IsVideoEnabled)
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Voice/Video not enabled for this room");

        _voiceVideoUsers.AddOrUpdate(roomId,
            _ => [_userId],
            (_, set) => { lock (set) { set.Add(_userId); } return set; });

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserJoinedVoiceVideo(roomId, _userId);

        var sessionId = await _janus.CreateSessionAsync();
        var handleId = await _janus.AttachPluginAsync(sessionId);

        long janusRoomId = Math.Abs(roomId.GetHashCode());
        if (janusRoomId == 0) janusRoomId = 1;

        await _janus.EnsureVideoRoomAsync(sessionId, handleId, janusRoomId);

        var serverBase = _config["Server:BaseUrl"] ?? "http://localhost:5000";
        var iceServersJson = _config["Janus:IceServers"]
            ?? "[{\"urls\":\"stun:stun.l.google.com:19302\"}]";

        return new JanusSessionInfo
        {
            RoomId = roomId,
            JanusSessionId = sessionId,
            JanusHandleId = handleId,
            JanusUrl = _config["Janus:Url"] ?? "http://localhost:8088/janus",
            IceServers = iceServersJson,
            ServerBaseUrl = serverBase,
            JanusRoomId = janusRoomId
        };
    }

    public async ValueTask LeaveVoiceVideoAsync(Guid roomId)
    {
        if (_voiceVideoUsers.TryGetValue(roomId, out var users))
        {
            lock (users)
            {
                users.Remove(_userId);
            }
        }

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserLeftVoiceVideo(roomId, _userId);
    }

    public async ValueTask SendWebRtcSignalAsync(Guid roomId, Guid targetUserId, string signalType, string payload)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        var group = await Group.AddAsync($"room:{roomId}");

        if (_onlineUsers.TryGetValue(targetUserId, out var targetConnections))
        {
            Guid[] targets;
            lock (targetConnections)
            {
                targets = [.. targetConnections];
            }
            foreach (var connId in targets)
            {
                group.Single(connId).OnWebRtcSignalReceived(roomId, _userId, signalType, payload);
            }
        }
    }

    private async Task<ChatRoomMember> GetMembershipOrThrowAsync(Guid roomId)
    {
        return await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == _userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not a member of this room");
    }

    private async Task RemoveFromRoomGroupAsync(Guid roomId)
    {
        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserLeftRoom(roomId, _userId);
        await group.RemoveAsync(Context);
    }

    private async Task InvalidateMessageCache(Guid roomId)
    {
        await _cache.RemoveAsync($"messages:{roomId}:50:"); 
    }

    private static string GetDmKey(Guid a, Guid b) =>
        string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal) < 0
            ? $"{a}_{b}" : $"{b}_{a}";
}
