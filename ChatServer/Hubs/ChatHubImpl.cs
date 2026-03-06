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
    private static readonly ConcurrentDictionary<Guid, HashSet<Guid>> _privateVoiceVideoUsers = new();
    private static readonly ConcurrentDictionary<Guid, HashSet<Guid>> _roomConnections = new();

    private readonly AppDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly JwtTokenService _jwt;
    private readonly JanusService _janus;
    private readonly IConfiguration _config;

    private Guid _userId;
    private string _userName = string.Empty;
    private readonly HashSet<Guid> _joinedRooms = [];
    private readonly HashSet<Guid> _joinedPrivateCallContexts = [];
    private bool _becameOnlineOnConnect;

    public ChatHubImpl(AppDbContext db, IDistributedCache cache, JwtTokenService jwt, JanusService janus, IConfiguration config)
    {
        _db = db;
        _cache = cache;
        _jwt = jwt;
        _janus = janus;
        _config = config;
    }

    public static bool IsUserOnline(Guid userId)
    {
        if (!_onlineUsers.TryGetValue(userId, out var connections))
        {
            return false;
        }

        lock (connections)
        {
            return connections.Count > 0;
        }
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

        _becameOnlineOnConnect = false;
        _onlineUsers.AddOrUpdate(_userId,
            _ =>
            {
                _becameOnlineOnConnect = true;
                return [ConnectionId];
            },
            (_, set) =>
            {
                lock (set)
                {
                    if (set.Count == 0)
                    {
                        _becameOnlineOnConnect = true;
                    }
                    set.Add(ConnectionId);
                }
                return set;
            });

        // Join a global group used for cross-room events like global role updates.
        await Group.AddAsync("global");

    }

    protected override async ValueTask OnConnected()
    {
        // Do not broadcast from OnConnecting; broadcasting there can start the
        // duplex stream body before MagicOnion writes required response headers.
        if (_becameOnlineOnConnect)
        {
            var globalGroup = await Group.AddAsync("global");
            globalGroup.All.OnUserOnlineStatusChanged(_userId, true);
        }
    }

    protected override async ValueTask OnDisconnected()
    {
        foreach (var roomId in _joinedRooms)
        {
            await RemoveFromRoomGroupAsync(roomId);
        }

        var becameOffline = false;
        if (_onlineUsers.TryGetValue(_userId, out var connections))
        {
            lock (connections)
            {
                connections.Remove(ConnectionId);
                if (connections.Count == 0)
                {
                    _onlineUsers.TryRemove(_userId, out _);
                    becameOffline = true;
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

        foreach (var callContextId in _joinedPrivateCallContexts)
        {
            if (_privateVoiceVideoUsers.TryGetValue(callContextId, out var privateVoiceUsers))
            {
                lock (privateVoiceUsers)
                {
                    privateVoiceUsers.Remove(_userId);
                    if (privateVoiceUsers.Count == 0)
                    {
                        _privateVoiceVideoUsers.TryRemove(callContextId, out _);
                    }
                }
            }

            var privateCallGroup = await Group.AddAsync($"pcall:{callContextId}");
            privateCallGroup.All.OnUserLeftVoiceVideo(callContextId, _userId);
            await privateCallGroup.RemoveAsync(Context);
        }

        if (becameOffline)
        {
            var globalGroup = await Group.AddAsync("global");
            globalGroup.All.OnUserOnlineStatusChanged(_userId, false);
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
        AddRoomConnection(roomId, ConnectionId);

        var user = await _db.Users.FindAsync(_userId);
        var memberInfo = new ChatMemberInfo
        {
            UserId = _userId,
            UserName = _userName,
            DisplayName = user?.DisplayName ?? _userName,
            AvatarUrl = user?.AvatarUrl,
            Role = user?.GlobalRole ?? UserRole.Member,
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

        // Server-side push: notify room members who are online but not connected to this room.
        var memberIds = await _db.ChatRoomMembers
            .Where(m => m.RoomId == roomId && m.UserId != _userId)
            .Select(m => m.UserId)
            .Distinct()
            .ToArrayAsync();

        if (memberIds.Length > 0)
        {
            var globalGroup = await Group.AddAsync("global");
            var roomConnectionSnapshot = GetRoomConnectionSnapshot(roomId);

            foreach (var memberId in memberIds)
            {
                if (!_onlineUsers.TryGetValue(memberId, out var memberConnections))
                {
                    continue;
                }

                Guid[] targetConnections;
                lock (memberConnections)
                {
                    targetConnections = [.. memberConnections.Where(connId =>
                        roomConnectionSnapshot == null || !roomConnectionSnapshot.Contains(connId))];
                }

                foreach (var connectionId in targetConnections)
                {
                    globalGroup.Single(connectionId).OnMessageReceived(chatMessage);
                }
            }
        }

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask DeleteMessageAsync(Guid roomId, Guid messageId)
    {
        await GetMembershipOrThrowAsync(roomId);
        var actorRole = await GetUserGlobalRoleAsync(_userId);
        var message = await _db.Messages.FindAsync(messageId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (message.SenderId != _userId && actorRole < UserRole.Moderator)
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
        await GetMembershipOrThrowAsync(roomId);
        var actorRole = await GetUserGlobalRoleAsync(_userId);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in room");

        var targetRole = await GetUserGlobalRoleAsync(userId);
        if (targetRole >= actorRole)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot kick user with equal or higher role");
        }

        RemoveVoiceParticipant(roomId, userId);

        _db.ChatRoomMembers.Remove(targetMembership);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync("rooms:all");

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserKicked(roomId, userId, "Kicked by moderator");
        group.All.OnUserLeftVoiceVideo(roomId, userId);
        group.All.OnUserKickedFromVoiceVideo(roomId, userId, "Kicked from call by moderator");
    }

    public async ValueTask KickUserFromVoiceVideoAsync(Guid roomId, Guid userId, string reason)
    {
        await GetMembershipOrThrowAsync(roomId);
        var actorRole = await GetUserGlobalRoleAsync(_userId);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, "User not in room");

        var targetRole = await GetUserGlobalRoleAsync(userId);
        if (targetRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot moderate user with equal or higher role");
        }

        if (!RemoveVoiceParticipant(roomId, userId))
        {
            throw new ReturnStatusException(StatusCode.NotFound, "User is not in the current call");
        }

        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserLeftVoiceVideo(roomId, userId);
        group.All.OnUserKickedFromVoiceVideo(roomId, userId,
            string.IsNullOrWhiteSpace(reason) ? "Kicked from call by moderator" : reason.Trim());
    }

    public async ValueTask MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        if (sourceRoomId == targetRoomId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Source and target room cannot be the same");
        }

        await GetMembershipOrThrowAsync(sourceRoomId);
        var actorRole = await GetUserGlobalRoleAsync(_userId);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetRoom = await _db.ChatRooms.FindAsync(targetRoomId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Target room not found");

        var sourceMembership = await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == sourceRoomId && m.UserId == userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in source room");

        var targetRole = await GetUserGlobalRoleAsync(userId);
        if (targetRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot move user with equal or higher role");
        }

        _db.ChatRoomMembers.Remove(sourceMembership);
        RemoveVoiceParticipant(sourceRoomId, userId);

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
        sourceGroup.All.OnUserLeftVoiceVideo(sourceRoomId, userId);
        sourceGroup.All.OnUserKickedFromVoiceVideo(sourceRoomId, userId, "Moved to another room");

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
                Role = user.GlobalRole,
                JoinedAt = DateTime.UtcNow,
                IsOnline = _onlineUsers.ContainsKey(userId)
            });
        }
    }

    public async ValueTask SetUserRoleAsync(Guid roomId, Guid userId, UserRole role)
    {
        var actorRole = await GetUserGlobalRoleAsync(_userId);
        if (actorRole < UserRole.Admin)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only admins and owners can change roles");
        }

        var targetUser = await _db.Users.FindAsync(userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not found");

        if (targetUser.GlobalRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot change role for user with equal or higher role");
        }

        if (role >= actorRole || role == UserRole.Owner)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot assign a role equal to or higher than your own");
        }

        targetUser.GlobalRole = role;
        await _db.SaveChangesAsync();

        var group = await Group.AddAsync("global");
        group.All.OnUserRoleChanged(roomId, userId, role);
    }

    public async ValueTask<UserInfo> UpdateProfileAsync(UpdateProfileRequest request)
    {
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Display name is required");
        }

        if (displayName.Length > 64)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Display name must be 64 characters or fewer");
        }

        var avatarUrl = NormalizeAvatarUrl(request.AvatarUrl);
        if (avatarUrl != null && avatarUrl.Length > 2048)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Avatar URL is too long");
        }

        var user = await _db.Users.FindAsync(_userId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, "User not found");

        user.DisplayName = displayName;
        user.AvatarUrl = avatarUrl;
        await _db.SaveChangesAsync();

        var globalGroup = await Group.AddAsync("global");
        globalGroup.All.OnUserProfileUpdated(new UserProfileUpdateInfo
        {
            UserId = _userId,
            DisplayName = user.DisplayName,
            AvatarUrl = user.AvatarUrl
        });

        return new UserInfo
        {
            Id = user.Id,
            UserName = user.UserName ?? _userName,
            DisplayName = user.DisplayName,
            AvatarUrl = user.AvatarUrl,
            IsOnline = IsUserOnline(user.Id)
        };
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

        // Push directly to sender/recipient online connections so DM delivery
        // does not depend on either side having joined a transient dm:* group.
        var globalGroup = await Group.AddAsync("global");
        var targetUserIds = new HashSet<Guid> { _userId, recipientId };

        foreach (var targetUserId in targetUserIds)
        {
            if (!_onlineUsers.TryGetValue(targetUserId, out var targetConnections))
            {
                continue;
            }

            Guid[] connectionSnapshot;
            lock (targetConnections)
            {
                connectionSnapshot = [.. targetConnections];
            }

            foreach (var connectionId in connectionSnapshot)
            {
                globalGroup.Single(connectionId).OnPrivateMessageReceived(privateMessage);
            }
        }
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

        var globalGroup = await Group.AddAsync("global");
        var targetUserIds = new HashSet<Guid> { _userId, senderId };

        foreach (var targetUserId in targetUserIds)
        {
            if (!_onlineUsers.TryGetValue(targetUserId, out var targetConnections))
            {
                continue;
            }

            Guid[] connectionSnapshot;
            lock (targetConnections)
            {
                connectionSnapshot = [.. targetConnections];
            }

            foreach (var connectionId in connectionSnapshot)
            {
                globalGroup.Single(connectionId).OnPrivateMessagesRead(_userId);
            }
        }
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

        return await CreateJanusSessionInfoAsync(roomId);
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

    public ValueTask<Guid[]> GetVoiceVideoParticipantsAsync(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not in this room");

        if (_voiceVideoUsers.TryGetValue(roomId, out var users))
        {
            lock (users)
            {
                return ValueTask.FromResult(users.ToArray());
            }
        }

        return ValueTask.FromResult(Array.Empty<Guid>());
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

    public async ValueTask<JanusSessionInfo> JoinPrivateVoiceVideoAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        var peerExists = await _db.Users.AnyAsync(u => u.Id == peerUserId);
        if (!peerExists)
        {
            throw new ReturnStatusException(StatusCode.NotFound, "Target user not found");
        }

        var callContextId = BuildPrivateCallContextId(_userId, peerUserId);
        var addedSelf = false;

        _privateVoiceVideoUsers.AddOrUpdate(callContextId,
            _ =>
            {
                addedSelf = true;
                return [_userId];
            },
            (_, set) =>
            {
                lock (set)
                {
                    addedSelf = set.Add(_userId);
                }

                return set;
            });

        var privateCallGroup = await Group.AddAsync($"pcall:{callContextId}");
        _joinedPrivateCallContexts.Add(callContextId);
        privateCallGroup.All.OnUserJoinedVoiceVideo(callContextId, _userId);

        if (addedSelf && _onlineUsers.TryGetValue(peerUserId, out var peerConnections))
        {
            Guid[] peerConnectionIds;
            lock (peerConnections)
            {
                peerConnectionIds = [.. peerConnections];
            }

            if (peerConnectionIds.Length > 0)
            {
                var globalGroup = await Group.AddAsync("global");
                foreach (var peerConnectionId in peerConnectionIds)
                {
                    globalGroup.Single(peerConnectionId).OnPrivateCallIncoming(_userId);
                }
            }
        }

        return await CreateJanusSessionInfoAsync(callContextId);
    }

    public async ValueTask LeavePrivateVoiceVideoAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        var callContextId = BuildPrivateCallContextId(_userId, peerUserId);

        if (_privateVoiceVideoUsers.TryGetValue(callContextId, out var users))
        {
            lock (users)
            {
                users.Remove(_userId);
                if (users.Count == 0)
                {
                    _privateVoiceVideoUsers.TryRemove(callContextId, out _);
                }
            }
        }

        _joinedPrivateCallContexts.Remove(callContextId);
        var privateCallGroup = await Group.AddAsync($"pcall:{callContextId}");
        privateCallGroup.All.OnUserLeftVoiceVideo(callContextId, _userId);
        await privateCallGroup.RemoveAsync(Context);
    }

    public ValueTask<Guid[]> GetPrivateVoiceVideoParticipantsAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        var callContextId = BuildPrivateCallContextId(_userId, peerUserId);
        if (_privateVoiceVideoUsers.TryGetValue(callContextId, out var users))
        {
            lock (users)
            {
                return ValueTask.FromResult(users.ToArray());
            }
        }

        return ValueTask.FromResult(Array.Empty<Guid>());
    }

    public async ValueTask SendPrivateWebRtcSignalAsync(Guid peerUserId, Guid targetUserId, string signalType, string payload)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        var callContextId = BuildPrivateCallContextId(_userId, peerUserId);
        if (!_privateVoiceVideoUsers.TryGetValue(callContextId, out var users))
        {
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Private call is not active");
        }

        lock (users)
        {
            if (!users.Contains(_userId))
            {
                throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not joined in private call");
            }
        }

        var privateCallGroup = await Group.AddAsync($"pcall:{callContextId}");
        if (_onlineUsers.TryGetValue(targetUserId, out var targetConnections))
        {
            Guid[] targets;
            lock (targetConnections)
            {
                targets = [.. targetConnections];
            }

            foreach (var connId in targets)
            {
                privateCallGroup.Single(connId).OnWebRtcSignalReceived(callContextId, _userId, signalType, payload);
            }
        }
    }

    private async Task<JanusSessionInfo> CreateJanusSessionInfoAsync(Guid contextId)
    {
        var sessionId = await _janus.CreateSessionAsync();
        var handleId = await _janus.AttachPluginAsync(sessionId);

        var guidBytes = contextId.ToByteArray();
        long janusRoomId = Math.Abs((long)(BitConverter.ToInt32(guidBytes, 0) ^ BitConverter.ToInt32(guidBytes, 4)));
        if (janusRoomId == 0) janusRoomId = 1;

        await _janus.EnsureVideoRoomAsync(sessionId, janusRoomId);

        var serverBase = _config["Server:BaseUrl"] ?? "http://localhost:5000";
        var iceServersJson = _config["Janus:IceServers"]
            ?? "[{\"urls\":\"stun:stun.l.google.com:19302\"}]";

        return new JanusSessionInfo
        {
            RoomId = contextId,
            JanusSessionId = sessionId,
            JanusHandleId = handleId,
            JanusUrl = _config["Janus:Url"] ?? "ws://localhost:8188",
            IceServers = iceServersJson,
            ServerBaseUrl = serverBase,
            JanusRoomId = janusRoomId
        };
    }

    private static Guid BuildPrivateCallContextId(Guid a, Guid b)
    {
        var first = a.ToByteArray();
        var second = b.ToByteArray();
        var bytes = new byte[16];

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(first[i] ^ second[i] ^ 0x5A);
        }

        if (bytes.All(static b => b == 0))
        {
            bytes[0] = 1;
        }

        // Force valid UUID version/variant bits.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }

    private async Task<ChatRoomMember> GetMembershipOrThrowAsync(Guid roomId)
    {
        return await _db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == _userId)
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not a member of this room");
    }

    private async Task<UserRole> GetUserGlobalRoleAsync(Guid userId)
    {
        var role = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.GlobalRole)
            .FirstOrDefaultAsync();

        return role ?? UserRole.Member;
    }

    private async Task RemoveFromRoomGroupAsync(Guid roomId)
    {
        RemoveRoomConnection(roomId, ConnectionId);
        var group = await Group.AddAsync($"room:{roomId}");
        group.All.OnUserLeftRoom(roomId, _userId);
        await group.RemoveAsync(Context);
    }

    private async Task InvalidateMessageCache(Guid roomId)
    {
        await _cache.RemoveAsync($"messages:{roomId}:50:"); 
    }

    private static bool RemoveVoiceParticipant(Guid roomId, Guid userId)
    {
        if (!_voiceVideoUsers.TryGetValue(roomId, out var users))
        {
            return false;
        }

        lock (users)
        {
            var removed = users.Remove(userId);
            if (users.Count == 0)
            {
                _voiceVideoUsers.TryRemove(roomId, out _);
            }

            return removed;
        }
    }

    private static void AddRoomConnection(Guid roomId, Guid connectionId)
    {
        _roomConnections.AddOrUpdate(roomId,
            _ => new HashSet<Guid> { connectionId },
            (_, set) =>
            {
                lock (set)
                {
                    set.Add(connectionId);
                }
                return set;
            });
    }

    private static void RemoveRoomConnection(Guid roomId, Guid connectionId)
    {
        if (!_roomConnections.TryGetValue(roomId, out var connections))
        {
            return;
        }

        lock (connections)
        {
            connections.Remove(connectionId);
            if (connections.Count == 0)
            {
                _roomConnections.TryRemove(roomId, out _);
            }
        }
    }

    private static HashSet<Guid>? GetRoomConnectionSnapshot(Guid roomId)
    {
        if (!_roomConnections.TryGetValue(roomId, out var connections))
        {
            return null;
        }

        lock (connections)
        {
            if (connections.Count == 0)
            {
                return null;
            }

            return new HashSet<Guid>(connections);
        }
    }

    private static string? NormalizeAvatarUrl(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return null;
        }

        var normalized = avatarUrl.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string GetDmKey(Guid a, Guid b) =>
        string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal) < 0
            ? $"{a}_{b}" : $"{b}_{a}";
}
