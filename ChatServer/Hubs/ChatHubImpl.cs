using System.Collections.Concurrent;
using System.Text.Json;
using ChatServer.Auth;
using ChatServer.Data;
using ChatServer.Data.Entities;
using ChatServer.Serialization;
using ChatServer.Services;
using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;
using MagicOnion.Server.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Shared;
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

    /// <summary>
    /// Active screen-annotation sessions per call context (room id, or private call
    /// context id for DM calls), keyed within a context by the sharer's user id.
    ///
    /// Keyed by sharer rather than one session per call because several people can
    /// share at once, and each of them gates annotations on their own screen. An
    /// entry exists only while that sharer has annotations on.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, Dictionary<Guid, ScreenAnnotationState>> _annotationSessions = new();

    /// <summary>
    /// Guards adding and removing whole context entries in <see cref="_annotationSessions"/>.
    /// Per-sharer reads and writes take the inner dictionary's own lock; this one
    /// exists so "the last sharer left, drop the context" cannot race a sharer
    /// arriving and leave them writing into an orphaned dictionary. Lock order is
    /// always this gate first, then the inner dictionary.
    /// </summary>
    private static readonly object _annotationSessionsGate = new();

    /// <summary>Upper bound on floats in one annotation payload (~2k points).</summary>
    private const int MaxAnnotationPointValues = 4096;

    /// <summary>
    /// A StreamingHub is one DI scope for the life of the connection, so a scoped
    /// AppDbContext here would live for hours and never release anything it tracked -
    /// every message body a user sent stayed reachable for the whole session, and
    /// DetectChanges walked the accumulated graph on every save. Each method takes a
    /// short-lived context from the factory instead.
    /// </summary>
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDistributedCache _cache;
    private readonly JwtTokenService _jwt;
    private readonly JanusService _janus;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatHubImpl> _logger;

    private Guid _userId;
    private string _userName = string.Empty;
    private readonly HashSet<Guid> _joinedRooms = [];
    private readonly HashSet<Guid> _joinedPrivateCallContexts = [];
    private bool _becameOnlineOnConnect;
    private DateTime _lastTypingSignalAt = DateTime.MinValue;

    /// <summary>
    /// Group handles, acquired once at the points where this connection genuinely joins
    /// something. Group.AddAsync is a join, not a getter: it allocates the prefixed name,
    /// takes the per-hub-type semaphore, bumps a refcount, and appends to a per-connection
    /// bag that is never deduplicated or trimmed - so calling it on every hub invocation
    /// grew that bag for the life of the connection and made teardown O(total calls).
    ///
    /// Hub methods for one connection are processed one at a time, so plain
    /// non-concurrent collections are safe here.
    /// </summary>
    private IGroup<IChatHubReceiver>? _globalGroup;
    private readonly Dictionary<Guid, IGroup<IChatHubReceiver>> _roomGroups = [];
    private readonly Dictionary<Guid, IGroup<IChatHubReceiver>> _privateCallGroups = [];

    /// <summary>
    /// This connection's own global role, so moderation calls stop re-reading it on every
    /// invocation.
    ///
    /// A cached role must never outlive a demotion, so <see cref="_roleGeneration"/> is
    /// bumped whenever any user's role changes and the cached value records the generation
    /// it was read at. Without that, a demoted user would keep acting with their old
    /// privileges until they happened to reconnect.
    /// </summary>
    private UserRole? _actorRole;
    private int _actorRoleGeneration = -1;

    private static readonly ConcurrentDictionary<Guid, int> _roleGeneration = new();

    /// <summary>
    /// Janus session id per (user, call context).
    ///
    /// Nothing used to release these: a session was created on every join and left for
    /// Janus to reap once keepalives stopped, while the server-side event queue keyed by
    /// that id - an unbounded channel - was never removed.
    ///
    /// Ownership is process-wide rather than per connection because a client keeps its
    /// media session across a hub reconnect: the reconnected connection re-registers
    /// presence and carries on with the same Janus session, so it must be able to find -
    /// and eventually release - the session its predecessor created.
    /// </summary>
    private static readonly ConcurrentDictionary<(Guid UserId, Guid ContextId), long> _janusSessions = new();

    public ChatHubImpl(
        IDbContextFactory<AppDbContext> dbFactory,
        IDistributedCache cache,
        JwtTokenService jwt,
        JanusService janus,
        IConfiguration config,
        ILogger<ChatHubImpl> logger)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _jwt = jwt;
        _janus = janus;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Every live connection belonging to any of <paramref name="userIds"/>, gathered into
    /// one list so callers can hand it to <c>Only()</c> and have the payload encoded once
    /// rather than once per recipient.
    /// </summary>
    private static List<Guid> CollectConnections(ReadOnlySpan<Guid> userIds)
    {
        var targets = new List<Guid>();

        foreach (var userId in userIds)
        {
            if (!_onlineUsers.TryGetValue(userId, out var connections))
            {
                continue;
            }

            lock (connections)
            {
                targets.AddRange(connections);
            }
        }

        return targets;
    }

    /// <summary>
    /// Users currently in a room's call. Used by the room service and by the
    /// annotation broadcast, which must reach call participants only.
    /// </summary>
    public static Guid[] GetVoiceParticipantsSnapshot(Guid roomId)
    {
        if (roomId == Guid.Empty)
        {
            return [];
        }

        if (_voiceVideoUsers.TryGetValue(roomId, out var users))
        {
            lock (users)
            {
                return users
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToArray();
            }
        }

        return [];
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
        _globalGroup = await Group.AddAsync("global");
    }

    protected override ValueTask OnConnected()
    {
        // Do not broadcast from OnConnecting; broadcasting there can start the
        // duplex stream body before MagicOnion writes required response headers.
        if (_becameOnlineOnConnect && _globalGroup != null)
        {
            _globalGroup.All.OnUserOnlineStatusChanged(_userId, true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The global group handle, joined once in <see cref="OnConnecting"/>.</summary>
    private async ValueTask<IGroup<IChatHubReceiver>> GlobalGroupAsync()
        => _globalGroup ??= await Group.AddAsync("global");

    /// <summary>The handle for a room this connection has joined, cached per room.</summary>
    private async ValueTask<IGroup<IChatHubReceiver>> RoomGroupAsync(Guid roomId)
    {
        if (!_roomGroups.TryGetValue(roomId, out var group))
        {
            group = await Group.AddAsync($"room:{roomId}");
            _roomGroups[roomId] = group;
        }

        return group;
    }

    /// <summary>The handle for a private call context, cached per context.</summary>
    private async ValueTask<IGroup<IChatHubReceiver>> PrivateCallGroupAsync(Guid callContextId)
    {
        if (!_privateCallGroups.TryGetValue(callContextId, out var group))
        {
            group = await Group.AddAsync($"pcall:{callContextId}");
            _privateCallGroups[callContextId] = group;
        }

        return group;
    }

    /// <summary>
    /// Broadcasts to a room this connection may not belong to.
    ///
    /// MagicOnion exposes no way to obtain a group handle without joining it, so a
    /// moderation action aimed at a room the actor never joined has to join, send, and
    /// leave again. Skipping the leave is what would otherwise subscribe moderators to the
    /// traffic of every room they had ever moderated, for the rest of the connection.
    /// Rooms this connection really is in take the cached handle and are left alone.
    /// </summary>
    private async Task SendToRoomAsync(Guid roomId, Action<IGroup<IChatHubReceiver>> send)
    {
        if (_roomGroups.TryGetValue(roomId, out var joined))
        {
            send(joined);
            return;
        }

        var transient = await Group.AddAsync($"room:{roomId}");
        try
        {
            send(transient);
        }
        finally
        {
            await transient.RemoveAsync(Context);
        }
    }

    protected override async ValueTask OnDisconnected()
    {
        // Sampled before anything below removes it.
        //
        // ReleaseOrphanedJanusSessionsAsync decides whether a media session is still
        // wanted by asking whether the user is present in the call - but this method is
        // what removes that presence, moments earlier. So by the time it ran, every
        // session looked orphaned and got destroyed, including the one belonging to a
        // client that was merely reconnecting. That is the loop behind calls dying
        // mid-session: hub blip -> presence cleared here -> live Janus session destroyed
        // -> client's next poll 404s -> full rejoin -> repeat.
        var hadCallPresence = new HashSet<Guid>();
        foreach (var contextId in _joinedRooms.Concat(_joinedPrivateCallContexts))
        {
            if (HasCallPresence(contextId, _userId))
            {
                hadCallPresence.Add(contextId);
            }
        }

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
            // Both the annotation session and voice presence are per-user, not
            // per-connection, so neither may be torn down while the user still has
            // another connection. During a reconnect the replacement connection has
            // usually already re-registered via EnsureVoiceVideoPresenceAsync before
            // this runs: releasing unconditionally would wipe the new connection's
            // presence, and would broadcast an annotation-disabled state that is
            // indistinguishable from the sharer deliberately turning annotations off -
            // so a brief network blip would silently kill annotations for everyone.
            //
            // becameOffline is precisely "no connections left", which is the crash
            // case the release exists for, so gating loses nothing.
            if (becameOffline)
            {
                await ReleaseRoomAnnotationSessionAsync(roomId, _userId);

                if (RemoveVoiceParticipant(roomId, _userId))
                {
                    var roomGroup = await RoomGroupAsync(roomId);
                    roomGroup.All.OnUserLeftVoiceVideo(roomId, _userId);
                }
            }
        }

        foreach (var callContextId in _joinedPrivateCallContexts)
        {
            // Same per-user vs per-connection rule as the room loop above. This path
            // is if anything more exposed: the DM reconnect branch of
            // CallStore.HandleConnectionStateChanged only calls
            // EnsurePrivateVoiceVideoPresenceAsync, so if this teardown ran
            // unconditionally it would delete the presence and annotation session the
            // replacement connection had just re-registered, leaving the reconnected
            // client in the call but silently detached from it.
            if (becameOffline)
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

                await ReleasePrivateAnnotationSessionAsync(callContextId, _userId);
            }

            var privateCallGroup = await PrivateCallGroupAsync(callContextId);

            if (becameOffline)
            {
                privateCallGroup.All.OnUserLeftVoiceVideo(callContextId, _userId);
            }

            // Group membership IS per-connection: this connection is going away
            // regardless, and the replacement re-joins through
            // EnsurePrivateVoiceVideoPresenceAsync, so this stays outside the gate.
            await privateCallGroup.RemoveAsync(Context);
            _privateCallGroups.Remove(callContextId);
        }

        // Only for contexts this user has genuinely left - a hub reconnect must not take
        // the client's media session with it.
        await ReleaseOrphanedJanusSessionsAsync(hadCallPresence);

        if (becameOffline)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.All.OnUserOnlineStatusChanged(_userId, false);
        }
    }

    public async ValueTask JoinRoomAsync(Guid roomId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Room existence and this user's membership timestamp in one statement, rather
        // than a FindAsync for the room followed by a lookup for the membership. A null
        // result means no such room; a null JoinedAt means the room exists but this user
        // is not a member yet.
        var membership = await db.ChatRooms
            .AsNoTracking()
            .Where(r => r.Id == roomId)
            .Select(r => new
            {
                JoinedAt = r.Members
                    .Where(m => m.UserId == _userId)
                    .Select(m => (DateTime?)m.JoinedAt)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Room not found");

        var joinedAt = membership.JoinedAt;
        if (joinedAt == null)
        {
            joinedAt = DateTime.UtcNow;
            db.ChatRoomMembers.Add(new ChatRoomMember
            {
                Id = Guid.NewGuid(),
                RoomId = roomId,
                UserId = _userId,
                Role = UserRole.Member,
                JoinedAt = joinedAt.Value,
                // Joining does not make the room's back catalogue unread.
                LastReadAt = joinedAt.Value
            });
            await db.SaveChangesAsync();
            await CacheKeys.InvalidateDirectoryAsync(_cache);
        }

        var group = await RoomGroupAsync(roomId);
        _joinedRooms.Add(roomId);
        AddRoomConnection(roomId, ConnectionId);

        // Sampled before the read, so a role change racing this query invalidates it.
        var roleGeneration = _roleGeneration.GetValueOrDefault(_userId);

        var profile = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == _userId)
            .Select(u => new { u.DisplayName, u.AvatarUrl, u.GlobalRole })
            .FirstOrDefaultAsync();

        // Free side effect of already having read it.
        CacheActorRole(profile?.GlobalRole ?? UserRole.Member, roleGeneration);

        group.All.OnUserJoinedRoom(roomId, new ChatMemberInfo
        {
            UserId = _userId,
            UserName = _userName,
            DisplayName = profile?.DisplayName ?? _userName,
            AvatarUrl = profile?.AvatarUrl,
            Role = profile?.GlobalRole ?? UserRole.Member,
            JoinedAt = joinedAt.Value,
            IsOnline = true
        });
    }

    public async ValueTask LeaveRoomAsync(Guid roomId)
    {
        // Leaving the room ends any call in it. OnDisconnected only walks _joinedRooms,
        // so anything left behind here could never be reclaimed: the user would sit in
        // the call as a ghost participant, holding an annotation session and a Janus
        // session, until the process restarted.
        //
        // Runs before the group is left so the departure still reaches the room, and
        // takes the cached handle rather than a transient join.
        await LeaveRoomCallAsync(roomId);

        await RemoveFromRoomGroupAsync(roomId);
        _joinedRooms.Remove(roomId);
    }

    public async ValueTask DeleteRoomAsync(Guid roomId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var room = await db.ChatRooms.FindAsync(roomId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, "Room not found");

        var actorRole = await GetActorRoleAsync(db);
        if (room.OwnerId != _userId && actorRole < UserRole.Admin)
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Only the owner or an admin can delete the room");

        db.ChatRooms.Remove(room);
        await db.SaveChangesAsync();
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        // Before the broadcast, so the call is already gone by the time clients react
        // to the deletion and start tearing their own side down.
        await DiscardRoomCallStateAsync(roomId);

        await SendToRoomAsync(roomId, g => g.All.OnRoomDeleted(roomId));

        // The room is gone. Leave the group properly rather than just forgetting the
        // handle, so group membership and _roomConnections do not outlive the room.
        if (_joinedRooms.Remove(roomId))
        {
            await RemoveFromRoomGroupAsync(roomId);
        }
    }

    public async ValueTask RenameRoomAsync(Guid roomId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Room name cannot be empty");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var room = await db.ChatRooms.FindAsync(roomId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, "Room not found");

        var actorRole = await GetActorRoleAsync(db);
        if (room.OwnerId != _userId && actorRole < UserRole.Admin)
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Only the owner or an admin can rename the room");

        room.Name = name.Trim();
        await db.SaveChangesAsync();
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        // Broadcast to all connected clients so every sidebar updates immediately
        var globalGroup = await GlobalGroupAsync();
        globalGroup.All.OnRoomRenamed(roomId, room.Name);
    }

    public async ValueTask SendMessageAsync(Guid roomId, SendMessageRequest request)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        ValidateMessageContent(request.Content, allowEmpty: request.Attachments is { Length: > 0 });

        var messageEntity = new MessageEntity
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = _userId,
            Content = request.Content,
            MessageType = ChatMessageType.Text,
            SentAt = DateTime.UtcNow,
            MentionedUserIdsJson = JsonSerializer.Serialize(request.MentionedUserIds, ServerJsonContext.Default.GuidArray),
            ReplyToMessageId = request.ReplyToMessageId
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

        // The member list does not depend on the insert, and now that contexts come from
        // a factory the two no longer share one connection - so this overlaps the write
        // instead of queueing behind it. MagicOnion processes a connection's hub calls one
        // at a time, so every round trip left on this path delays that client's next call.
        var memberIdsTask = LoadOtherRoomMemberIdsAsync(roomId);
        // Keep the overlapped query observed even if the insert below throws.
        _ = memberIdsTask.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        MessageReplyPreview? replyPreview = null;

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            if (request.ReplyToMessageId is { } parentId)
            {
                var parent = await db.Messages
                    .AsNoTracking()
                    .Where(m => m.Id == parentId)
                    .Select(m => new { m.RoomId, m.IsDeleted, SenderName = m.Sender.UserName!, m.Content })
                    .FirstOrDefaultAsync();

                // A reply may only point at a live message in the same room.
                if (parent == null || parent.RoomId != roomId || parent.IsDeleted)
                {
                    throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Replied-to message not found");
                }

                replyPreview = new MessageReplyPreview
                {
                    SenderName = parent.SenderName,
                    Snippet = MessageEnricher.Snippet(parent.Content)
                };
            }

            db.Messages.Add(messageEntity);
            await db.SaveChangesAsync();
        }

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
            Attachments = request.Attachments,
            ReplyToMessageId = request.ReplyToMessageId,
            ReplyPreview = replyPreview
        };

        var group = await RoomGroupAsync(roomId);
        group.All.OnMessageReceived(chatMessage);

        // Server-side push: notify room members who are online but not connected to this
        // room. One Only() call rather than a Single() per connection - Single builds a
        // fresh proxy chain and runs a full MessagePack pass for each target, so the same
        // message body was re-encoded once per recipient.
        var memberIds = await memberIdsTask;
        if (memberIds.Length > 0)
        {
            var roomConnectionSnapshot = GetRoomConnectionSnapshot(roomId);
            var targets = new List<Guid>();

            foreach (var memberId in memberIds)
            {
                if (!_onlineUsers.TryGetValue(memberId, out var memberConnections))
                {
                    continue;
                }

                lock (memberConnections)
                {
                    foreach (var connectionId in memberConnections)
                    {
                        if (roomConnectionSnapshot == null || !roomConnectionSnapshot.Contains(connectionId))
                        {
                            targets.Add(connectionId);
                        }
                    }
                }
            }

            if (targets.Count > 0)
            {
                var globalGroup = await GlobalGroupAsync();
                globalGroup.Only(targets).OnMessageReceived(chatMessage);
            }
        }

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask DeleteMessageAsync(Guid roomId, Guid messageId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        await EnsureMembershipAsync(db, roomId, _userId);
        var actorRole = await GetActorRoleAsync(db);

        // Only the columns needed to authorize this, projected instead of loading the
        // whole row - FindAsync pulled the longtext body across just to flip a flag.
        // RoomId comes along so a caller can't delete via a room they moderate while
        // the message lives in another.
        var target = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SenderId, m.RoomId })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (target.RoomId != roomId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");
        }

        if (target.SenderId != _userId && actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        await db.Messages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true));

        var group = await RoomGroupAsync(roomId);
        group.All.OnMessageDeleted(roomId, messageId);

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask EditMessageAsync(Guid roomId, Guid messageId, EditMessageRequest request)
    {
        var content = ValidateMessageContent(request.Content, allowEmpty: false);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureMembershipAsync(db, roomId, _userId);

        var target = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SenderId, m.RoomId, m.IsDeleted })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (target.RoomId != roomId || target.IsDeleted)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");
        }

        // Sender-only: moderators can remove content but never rewrite someone's words.
        if (target.SenderId != _userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only the sender can edit a message");
        }

        var editedAt = DateTime.UtcNow;
        var mentionedUserIds = request.MentionedUserIds ?? [];
        var mentionedJson = JsonSerializer.Serialize(mentionedUserIds, ServerJsonContext.Default.GuidArray);

        await db.Messages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Content, content)
                .SetProperty(m => m.MentionedUserIdsJson, mentionedJson)
                .SetProperty(m => m.EditedAt, editedAt));

        var group = await RoomGroupAsync(roomId);
        group.All.OnMessageEdited(roomId, messageId, content, mentionedUserIds, editedAt);

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask ToggleReactionAsync(Guid roomId, Guid messageId, string emoji)
    {
        var normalized = ValidateEmoji(emoji);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureMembershipAsync(db, roomId, _userId);

        var target = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.RoomId, m.IsDeleted })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (target.RoomId != roomId || target.IsDeleted)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");
        }

        var added = await ToggleReactionRowAsync(db, messageId, privateMessageId: null, normalized);

        var group = await RoomGroupAsync(roomId);
        group.All.OnMessageReactionChanged(roomId, messageId, _userId, normalized, added);

        await InvalidateMessageCache(roomId);
    }

    public async ValueTask TogglePrivateReactionAsync(Guid messageId, string emoji)
    {
        var normalized = ValidateEmoji(emoji);

        await using var db = await _dbFactory.CreateDbContextAsync();

        var target = await db.PrivateMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SenderId, m.RecipientId, m.IsDeleted })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        // Only the two participants can react to a DM.
        if (target.SenderId != _userId && target.RecipientId != _userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not part of this conversation");
        }

        if (target.IsDeleted)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");
        }

        var added = await ToggleReactionRowAsync(db, messageId: null, privateMessageId: messageId, normalized);

        var targets = CollectConnections([target.SenderId, target.RecipientId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.Only(targets)
                .OnPrivateMessageReactionChanged(target.SenderId, target.RecipientId, messageId, _userId, normalized, added);
        }
    }

    public async ValueTask SetTypingAsync(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        if (!AllowTypingSignal()) return;

        var group = await RoomGroupAsync(roomId);
        group.All.OnTyping(roomId, _userId, _userName);
    }

    public async ValueTask MarkRoomReadAsync(Guid roomId, DateTime lastReadAt)
    {
        // A client clock ahead of ours would otherwise mark future messages read.
        var timestamp = lastReadAt > DateTime.UtcNow ? DateTime.UtcNow : lastReadAt;

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Monotonic in the WHERE clause: a late-arriving older cursor from another
        // device updates nothing rather than reopening the room as unread.
        var updated = await db.ChatRoomMembers
            .Where(m => m.RoomId == roomId && m.UserId == _userId && m.LastReadAt < timestamp)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, timestamp));

        if (updated == 0) return;

        // Only this user's devices care.
        var targets = CollectConnections([_userId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.Only(targets).OnRoomRead(roomId, timestamp);
        }
    }

    public async ValueTask SetPrivateTypingAsync(Guid peerUserId)
    {
        if (!AllowTypingSignal()) return;

        // Only the peer needs this; the sender's own devices have nothing to show.
        var targets = CollectConnections([peerUserId]);
        if (targets.Count == 0) return;

        var globalGroup = await GlobalGroupAsync();
        globalGroup.Only(targets).OnPrivateTyping(_userId, _userName);
    }

    /// <summary>
    /// Floor on how often one connection can emit a typing signal. Clients already
    /// throttle, so this only bounds what a misbehaving or modified client can push.
    /// </summary>
    private bool AllowTypingSignal()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTypingSignalAt < TimeSpan.FromSeconds(1)) return false;
        _lastTypingSignalAt = now;
        return true;
    }

    /// <summary>
    /// Flips one (message, user, emoji) row and reports whether it ended up present.
    /// Delete-first means a double-click converges instead of stacking duplicates; a
    /// racing insert that loses to the unique index is treated as "already added"
    /// rather than an error, since both callers wanted the same end state.
    /// </summary>
    private async Task<bool> ToggleReactionRowAsync(AppDbContext db, Guid? messageId, Guid? privateMessageId, string emoji)
    {
        var removed = await db.Reactions
            .Where(r => r.MessageId == messageId
                     && r.PrivateMessageId == privateMessageId
                     && r.UserId == _userId
                     && r.Emoji == emoji)
            .ExecuteDeleteAsync();

        if (removed > 0)
        {
            return false;
        }

        try
        {
            db.Reactions.Add(new ReactionEntity
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                PrivateMessageId = privateMessageId,
                UserId = _userId,
                Emoji = emoji,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            db.ChangeTracker.Clear();
        }

        return true;
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
        => ex.InnerException is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry };

    /// <summary>
    /// Accepts either a unicode emoji sequence or a custom-emoji token (<c>:name:</c>).
    /// Custom tokens are allowed before custom emoji exist as a feature: clients render
    /// an unknown token as literal text, so nothing breaks, and adding the feature later
    /// needs no contract change.
    /// </summary>
    private static string ValidateEmoji(string? emoji)
    {
        var trimmed = emoji?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || trimmed.Length > ChatLimits.MaxEmojiLength)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Invalid reaction");
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Invalid reaction");
            }
        }

        if (trimmed.StartsWith(':') && !CustomEmojiTokenRegex.IsMatch(trimmed))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Invalid custom emoji token");
        }

        return trimmed;
    }

    private static readonly System.Text.RegularExpressions.Regex CustomEmojiTokenRegex =
        new("^:[a-z0-9_]{2,24}:$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Shared content gate for send and edit. Returns the trimmed content. Empty is
    /// allowed only when the message carries attachments (sticker/file-only sends).
    /// </summary>
    private static string ValidateMessageContent(string? content, bool allowEmpty)
    {
        var trimmed = content?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 && !allowEmpty)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument, "Message is empty");
        }

        if (trimmed.Length > ChatLimits.MaxMessageLength)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.InvalidArgument,
                $"Message exceeds {ChatLimits.MaxMessageLength} characters");
        }

        return trimmed;
    }

    public async ValueTask KickUserAsync(Guid roomId, Guid userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        await EnsureMembershipAsync(db, roomId, _userId);
        var actorRole = await GetActorRoleAsync(db);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetRole = await GetUserGlobalRoleAsync(db, userId);
        if (targetRole >= actorRole)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot kick user with equal or higher role");
        }

        // One DELETE, and its row count doubles as the "user not in room" check, so the
        // membership row never has to be fetched and tracked first.
        var removed = await db.ChatRoomMembers
            .Where(m => m.RoomId == roomId && m.UserId == userId)
            .ExecuteDeleteAsync();

        if (removed == 0)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in room");
        }

        RemoveVoiceParticipant(roomId, userId);
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        await SendToRoomAsync(roomId, g =>
        {
            g.All.OnUserKicked(roomId, userId, "Kicked by moderator");
            g.All.OnUserLeftVoiceVideo(roomId, userId);
            g.All.OnUserKickedFromVoiceVideo(roomId, userId, "Kicked from call by moderator");
        });
    }

    public async ValueTask KickUserFromVoiceVideoAsync(Guid roomId, Guid userId, string reason)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        await EnsureMembershipAsync(db, roomId, _userId);
        var actorRole = await GetActorRoleAsync(db);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Insufficient permissions");
        }

        var targetIsMember = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId);
        if (!targetIsMember)
        {
            throw new ReturnStatusException(StatusCode.NotFound, "User not in room");
        }

        var targetRole = await GetUserGlobalRoleAsync(db, userId);
        if (targetRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot moderate user with equal or higher role");
        }

        if (!RemoveVoiceParticipant(roomId, userId))
        {
            throw new ReturnStatusException(StatusCode.NotFound, "User is not in the current call");
        }

        await ReleaseRoomAnnotationSessionAsync(roomId, userId);

        var kickReason = string.IsNullOrWhiteSpace(reason) ? "Kicked from call by moderator" : reason.Trim();
        await SendToRoomAsync(roomId, g =>
        {
            g.All.OnUserLeftVoiceVideo(roomId, userId);
            g.All.OnUserKickedFromVoiceVideo(roomId, userId, kickReason);
        });
    }

    public async ValueTask MoveUserAsync(Guid sourceRoomId, Guid targetRoomId, Guid userId)
    {
        if (sourceRoomId == targetRoomId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Source and target room cannot be the same");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        await EnsureMembershipAsync(db, sourceRoomId, _userId);
        var actorRole = await GetActorRoleAsync(db);
        if (actorRole < UserRole.Moderator)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Insufficient permissions");
        }

        // The target user's role and the profile needed for the join broadcast come from
        // one read, and the target room's existence and whether the user is already in it
        // come from another. That replaces four separate lookups.
        var targetUser = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.UserName, u.DisplayName, u.AvatarUrl, u.GlobalRole })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not found");

        var targetRoom = await db.ChatRooms
            .AsNoTracking()
            .Where(r => r.Id == targetRoomId)
            .Select(r => new { AlreadyMember = r.Members.Any(m => m.UserId == userId) })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Target room not found");

        // Moving yourself is always allowed: the role comparison can never pass for
        // your own account (your role is never below itself), so without this carve-out
        // nobody - Owner included - could move themselves, which the client's
        // drag-a-participant-to-a-room flow expects to work. Everyone else still faces
        // the strict equal-or-higher guard; there is deliberately no elevated-actor
        // bypass, so an Admin cannot move an Owner.
        if (userId != _userId && targetUser.GlobalRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot move user with equal or higher role");
        }

        var sourceRoomConnectionSnapshot = GetRoomConnectionSnapshot(sourceRoomId);

        // Leaving the source room and joining the target one is a single move, so both
        // statements share a transaction. ExecuteDeleteAsync issues its DELETE immediately
        // rather than queueing onto SaveChanges, so without this an insert that failed
        // would leave the user in no room at all. The execution-strategy wrapper is
        // required because EnableRetryOnFailure is configured.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // The delete's row count is also the "was in the source room" check.
            var removed = await db.ChatRoomMembers
                .Where(m => m.RoomId == sourceRoomId && m.UserId == userId)
                .ExecuteDeleteAsync();

            if (removed == 0)
            {
                throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not in source room");
            }

            if (!targetRoom.AlreadyMember)
            {
                db.ChatRoomMembers.Add(new ChatRoomMember
                {
                    Id = Guid.NewGuid(),
                    RoomId = targetRoomId,
                    UserId = userId,
                    Role = UserRole.Member,
                    JoinedAt = DateTime.UtcNow,
                    LastReadAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            await tx.CommitAsync();
        });

        // Only once the move is durable.
        RemoveVoiceParticipant(sourceRoomId, userId);
        await CacheKeys.InvalidateDirectoryAsync(_cache);

        await SendToRoomAsync(sourceRoomId, g =>
        {
            g.All.OnUserMoved(sourceRoomId, targetRoomId, userId);
            g.All.OnUserLeftVoiceVideo(sourceRoomId, userId);
            g.All.OnUserKickedFromVoiceVideo(sourceRoomId, userId, "Moved to another room");
        });

        // Ensure the moved user receives the move event on all active connections, even
        // ones that were not subscribed to the source room group.
        if (_onlineUsers.TryGetValue(userId, out var movedUserConnections))
        {
            List<Guid> targetConnections;
            lock (movedUserConnections)
            {
                targetConnections = [.. movedUserConnections.Where(connId =>
                    sourceRoomConnectionSnapshot == null || !sourceRoomConnectionSnapshot.Contains(connId))];
            }

            if (targetConnections.Count > 0)
            {
                var globalGroup = await GlobalGroupAsync();
                var only = globalGroup.Only(targetConnections);
                only.OnUserMoved(sourceRoomId, targetRoomId, userId);
                only.OnUserLeftVoiceVideo(sourceRoomId, userId);
                only.OnUserKickedFromVoiceVideo(sourceRoomId, userId, "Moved to another room");
            }
        }

        await SendToRoomAsync(targetRoomId, g => g.All.OnUserJoinedRoom(targetRoomId, new ChatMemberInfo
        {
            UserId = userId,
            UserName = targetUser.UserName!,
            DisplayName = targetUser.DisplayName,
            AvatarUrl = targetUser.AvatarUrl,
            Role = targetUser.GlobalRole,
            JoinedAt = DateTime.UtcNow,
            IsOnline = _onlineUsers.ContainsKey(userId)
        }));
    }

    public async ValueTask SetUserRoleAsync(Guid roomId, Guid userId, UserRole role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var actorRole = await GetActorRoleAsync(db);
        if (actorRole < UserRole.Admin)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only admins and owners can change roles");
        }

        var currentRole = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.GlobalRole)
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "User not found");

        if (currentRole >= actorRole)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Cannot change role for user with equal or higher role");
        }

        if (role >= actorRole || role == UserRole.Owner)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Cannot assign a role equal to or higher than your own");
        }

        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GlobalRole, role));

        // Retire any role this user's other connections have cached.
        _roleGeneration.AddOrUpdate(userId, 1, static (_, generation) => generation + 1);

        await CacheKeys.InvalidateServerMembersAsync(_cache);

        var group = await GlobalGroupAsync();
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

        await using var db = await _dbFactory.CreateDbContextAsync();

        var updated = await db.Users
            .Where(u => u.Id == _userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DisplayName, displayName)
                .SetProperty(u => u.AvatarUrl, avatarUrl));

        if (updated == 0)
        {
            throw new ReturnStatusException(StatusCode.NotFound, "User not found");
        }

        await CacheKeys.InvalidateServerMembersAsync(_cache);

        var globalGroup = await GlobalGroupAsync();
        globalGroup.All.OnUserProfileUpdated(new UserProfileUpdateInfo
        {
            UserId = _userId,
            DisplayName = displayName,
            AvatarUrl = avatarUrl
        });

        return new UserInfo
        {
            Id = _userId,
            UserName = _userName,
            DisplayName = displayName,
            AvatarUrl = avatarUrl,
            IsOnline = IsUserOnline(_userId)
        };
    }

    public async ValueTask SendPrivateMessageAsync(Guid recipientId, SendMessageRequest request)
    {
        ValidateMessageContent(request.Content, allowEmpty: request.Attachments is { Length: > 0 });

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Existence probe against the primary key - the recipient row itself was never used.
        if (!await db.Users.AnyAsync(u => u.Id == recipientId))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Recipient not found");
        }

        MessageReplyPreview? replyPreview = null;
        if (request.ReplyToMessageId is { } parentId)
        {
            var parent = await db.PrivateMessages
                .AsNoTracking()
                .Where(m => m.Id == parentId)
                .Select(m => new { m.SenderId, m.RecipientId, m.IsDeleted, SenderName = m.Sender.UserName!, m.Content })
                .FirstOrDefaultAsync();

            // The parent must be a live message from this same two-person conversation.
            var sameConversation = parent != null &&
                ((parent.SenderId == _userId && parent.RecipientId == recipientId) ||
                 (parent.SenderId == recipientId && parent.RecipientId == _userId));

            if (parent == null || !sameConversation || parent.IsDeleted)
            {
                throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Replied-to message not found");
            }

            replyPreview = new MessageReplyPreview
            {
                SenderName = parent.SenderName,
                Snippet = MessageEnricher.Snippet(parent.Content)
            };
        }

        var pmEntity = new PrivateMessageEntity
        {
            Id = Guid.NewGuid(),
            SenderId = _userId,
            RecipientId = recipientId,
            Content = request.Content,
            SentAt = DateTime.UtcNow,
            MentionedUserIdsJson = JsonSerializer.Serialize(request.MentionedUserIds, ServerJsonContext.Default.GuidArray),
            ReplyToMessageId = request.ReplyToMessageId
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

        db.PrivateMessages.Add(pmEntity);
        await db.SaveChangesAsync();

        var privateMessage = new PrivateMessage
        {
            Id = pmEntity.Id,
            SenderId = _userId,
            SenderName = _userName,
            RecipientId = recipientId,
            Content = request.Content,
            SentAt = pmEntity.SentAt,
            Attachments = request.Attachments,
            MentionedUserIds = request.MentionedUserIds,
            ReplyToMessageId = request.ReplyToMessageId,
            ReplyPreview = replyPreview
        };

        // Push directly to sender/recipient online connections so DM delivery
        // does not depend on either side having joined a transient dm:* group.
        var targets = CollectConnections([_userId, recipientId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.Only(targets).OnPrivateMessageReceived(privateMessage);
        }
    }

    public async ValueTask MarkPrivateMessageReadAsync(Guid senderId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // One UPDATE rather than SELECT-every-row-then-SaveChanges. The old form pulled
        // whole rows - including the unbounded longtext Content - across just to flip a bool.
        await db.PrivateMessages
            .Where(m => m.SenderId == senderId && m.RecipientId == _userId && !m.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true));

        var targets = CollectConnections([_userId, senderId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            var only = globalGroup.Only(targets);
            only.OnPrivateMessagesRead(_userId);
            // Names both participants, so the reader's other devices know which
            // conversation badge to clear.
            only.OnPrivateConversationRead(_userId, senderId);
        }
    }

    public async ValueTask DeletePrivateMessageAsync(Guid messageId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var target = await db.PrivateMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SenderId, m.RecipientId, m.IsDeleted })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        // Sender-only: there is no moderation hierarchy inside a DM.
        if (target.SenderId != _userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only the sender can delete a message");
        }

        if (target.IsDeleted)
        {
            return;
        }

        await db.PrivateMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true));

        var targets = CollectConnections([target.SenderId, target.RecipientId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.Only(targets).OnPrivateMessageDeleted(target.SenderId, target.RecipientId, messageId);
        }
    }

    public async ValueTask EditPrivateMessageAsync(Guid messageId, EditMessageRequest request)
    {
        var content = ValidateMessageContent(request.Content, allowEmpty: false);

        await using var db = await _dbFactory.CreateDbContextAsync();

        var target = await db.PrivateMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SenderId, m.RecipientId, m.IsDeleted })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");

        if (target.IsDeleted)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.NotFound, "Message not found");
        }

        if (target.SenderId != _userId)
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Only the sender can edit a message");
        }

        var editedAt = DateTime.UtcNow;
        var mentionedJson = JsonSerializer.Serialize(request.MentionedUserIds ?? [], ServerJsonContext.Default.GuidArray);

        await db.PrivateMessages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Content, content)
                .SetProperty(m => m.MentionedUserIdsJson, mentionedJson)
                .SetProperty(m => m.EditedAt, editedAt));

        var targets = CollectConnections([target.SenderId, target.RecipientId]);
        if (targets.Count > 0)
        {
            var globalGroup = await GlobalGroupAsync();
            globalGroup.Only(targets).OnPrivateMessageEdited(target.SenderId, target.RecipientId, messageId, content, editedAt);
        }
    }

    public async ValueTask<JanusSessionInfo> JoinVoiceVideoAsync(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(Grpc.Core.StatusCode.FailedPrecondition, "Not in this room");

        await EnsureCallEnabledAsync(roomId);

        _voiceVideoUsers.AddOrUpdate(roomId,
            _ => [_userId],
            (_, set) => { lock (set) { set.Add(_userId); } return set; });

        var group = await RoomGroupAsync(roomId);
        group.All.OnUserJoinedVoiceVideo(roomId, _userId);

        return await CreateJanusSessionInfoAsync(roomId);
    }

    public async ValueTask LeaveVoiceVideoAsync(Guid roomId)
    {
        // Deliberately no _joinedRooms guard: a moderator move has the client leaving
        // the source room and migrating the call independently, so this can legitimately
        // arrive after the room is gone - and it is what tears the old call down.
        await LeaveRoomCallAsync(roomId);
    }

    /// <summary>
    /// Drops this user's call state for a room - voice presence, annotation session and
    /// Janus session - and announces the departure only if they were actually listed as
    /// present, so a repeated or already-superseded leave is silent.
    /// </summary>
    private async Task LeaveRoomCallAsync(Guid roomId)
    {
        // Also drops the room's entry once the last participant goes, which the old
        // inline removal here did not: every room that ever hosted a call kept an
        // empty set alive for the life of the process.
        var wasPresent = RemoveVoiceParticipant(roomId, _userId);

        await ReleaseRoomAnnotationSessionAsync(roomId, _userId);
        await ReleaseJanusSessionForAsync(roomId, "left room call");

        if (!wasPresent)
        {
            return;
        }

        // SendToRoomAsync rather than RoomGroupAsync: the latter is a join that gets
        // cached, so leaving a call in a room this connection is not in would have
        // silently subscribed it to that room's traffic for the rest of the connection.
        await SendToRoomAsync(roomId, g => g.All.OnUserLeftVoiceVideo(roomId, _userId));
    }

    /// <summary>
    /// Wipes a room's call state for every participant, not just the caller. Used when
    /// the room itself is deleted: a call cannot outlive the room it belongs to, and no
    /// client will ever send a leave for a room that is gone, so this is the only thing
    /// that reclaims the participant set, the annotation sessions and - most expensively,
    /// because they live on the Janus server rather than in this process - every
    /// participant's media session.
    /// </summary>
    private async Task DiscardRoomCallStateAsync(Guid roomId)
    {
        _voiceVideoUsers.TryRemove(roomId, out _);

        // Harmless ahead of RemoveFromRoomGroupAsync: RemoveRoomConnection no-ops on a
        // room with no entry.
        _roomConnections.TryRemove(roomId, out _);

        // Same gate as every other structural change, so this cannot race a sharer
        // enabling annotations into a dictionary that is about to be dropped.
        lock (_annotationSessionsGate)
        {
            _annotationSessions.TryRemove(roomId, out _);
        }

        await ReleaseAllJanusSessionsForContextAsync(roomId);
    }

    /// <summary>
    /// Verifies the room exists and has a call enabled, reading only the two flags rather
    /// than materializing (and tracking) the whole room row.
    /// </summary>
    private async Task EnsureCallEnabledAsync(Guid roomId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var flags = await db.ChatRooms
            .AsNoTracking()
            .Where(r => r.Id == roomId)
            .Select(r => new { r.IsVoiceEnabled, r.IsVideoEnabled })
            .FirstOrDefaultAsync()
            ?? throw new ReturnStatusException(StatusCode.NotFound, "Room not found");

        if (!flags.IsVoiceEnabled && !flags.IsVideoEnabled)
        {
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Voice/Video not enabled for this room");
        }
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

        var targets = CollectConnections([targetUserId]);
        if (targets.Count > 0)
        {
            var group = await RoomGroupAsync(roomId);
            group.Only(targets).OnWebRtcSignalReceived(roomId, _userId, signalType, payload);
        }
    }

    public async ValueTask<JanusSessionInfo> JoinPrivateVoiceVideoAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        await EnsurePeerExistsAsync(peerUserId);

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

        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
        _joinedPrivateCallContexts.Add(callContextId);
        privateCallGroup.All.OnUserJoinedVoiceVideo(callContextId, _userId);

        if (addedSelf)
        {
            var peerConnections = CollectConnections([peerUserId]);
            if (peerConnections.Count > 0)
            {
                var globalGroup = await GlobalGroupAsync();
                globalGroup.Only(peerConnections).OnPrivateCallIncoming(_userId);
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
        await ReleasePrivateAnnotationSessionAsync(callContextId, _userId);
        await ReleaseJanusSessionForAsync(callContextId, "left private call");

        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
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

        var targets = CollectConnections([targetUserId]);
        if (targets.Count > 0)
        {
            var privateCallGroup = await PrivateCallGroupAsync(callContextId);
            privateCallGroup.Only(targets).OnWebRtcSignalReceived(callContextId, _userId, signalType, payload);
        }
    }

    // ─── Screen-share annotations ──────────────────────────────────────────────

    public async ValueTask SetScreenAnnotationStateAsync(Guid roomId, ScreenAnnotationState state)
    {
        EnsureInRoomCall(roomId);
        var resolved = ApplyAnnotationState(roomId, state);

        await BroadcastToRoomCallAsync(roomId, receiver => receiver.OnScreenAnnotationStateChanged(roomId, resolved));
    }

    public async ValueTask SendScreenAnnotationAsync(Guid roomId, ScreenAnnotationEvent annotationEvent)
    {
        EnsureInRoomCall(roomId);
        var sanitized = SanitizeAnnotationEventOrThrow(roomId, annotationEvent);

        await BroadcastToRoomCallAsync(roomId, receiver => receiver.OnScreenAnnotationReceived(roomId, _userId, sanitized));
    }

    public ValueTask<ScreenAnnotationState[]> GetScreenAnnotationSessionsAsync(Guid roomId)
    {
        EnsureInRoomCall(roomId);
        return ValueTask.FromResult(GetAnnotationSessionsSnapshot(roomId));
    }

    public async ValueTask SetPrivateScreenAnnotationStateAsync(Guid peerUserId, ScreenAnnotationState state)
    {
        var callContextId = EnsureInPrivateCall(peerUserId);
        var resolved = ApplyAnnotationState(callContextId, state);

        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
        privateCallGroup.All.OnScreenAnnotationStateChanged(callContextId, resolved);
    }

    public async ValueTask SendPrivateScreenAnnotationAsync(Guid peerUserId, ScreenAnnotationEvent annotationEvent)
    {
        var callContextId = EnsureInPrivateCall(peerUserId);
        var sanitized = SanitizeAnnotationEventOrThrow(callContextId, annotationEvent);

        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
        privateCallGroup.All.OnScreenAnnotationReceived(callContextId, _userId, sanitized);
    }

    public ValueTask<ScreenAnnotationState[]> GetPrivateScreenAnnotationSessionsAsync(Guid peerUserId)
    {
        var callContextId = EnsureInPrivateCall(peerUserId);
        return ValueTask.FromResult(GetAnnotationSessionsSnapshot(callContextId));
    }

    /// <summary>
    /// Sends to the room's call participants only. Ink arrives many times a second,
    /// so it must not fan out to everyone who merely has the room open.
    /// </summary>
    private async Task BroadcastToRoomCallAsync(Guid roomId, Action<IChatHubReceiver> send)
    {
        var targets = CollectConnections(GetVoiceParticipantsSnapshot(roomId));
        if (targets.Count == 0)
        {
            return;
        }

        // One Only() rather than a Single() per connection. This runs many times a second
        // while someone is drawing, and Single() re-ran a full MessagePack encode of the
        // same stroke for every participant. .All is not a substitute: the room group holds
        // everyone with the room open, not just the people in the call.
        //
        // Routed through SendToRoomAsync because a moderator kicking someone from a call
        // reaches here via ReleaseRoomAnnotationSessionAsync for a room they need not have
        // joined - the caching path would subscribe them to it permanently.
        await SendToRoomAsync(roomId, group => send(group.Only(targets)));
    }

    private void EnsureInRoomCall(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
        {
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not in this room");
        }

        if (!_voiceVideoUsers.TryGetValue(roomId, out var users))
        {
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not in the call");
        }

        lock (users)
        {
            if (!users.Contains(_userId))
            {
                throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not in the call");
            }
        }
    }

    private Guid EnsureInPrivateCall(Guid peerUserId)
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

        return callContextId;
    }

    /// <summary>
    /// Stores the caller's own annotation session for a call. Each sharer has their
    /// own entry, so enabling annotations never takes anything away from someone
    /// else who is also sharing. Disabling drops the caller's entry only.
    /// </summary>
    private ScreenAnnotationState ApplyAnnotationState(Guid contextId, ScreenAnnotationState? state)
    {
        var resolved = BuildAnnotationState(state ?? new ScreenAnnotationState());

        lock (_annotationSessionsGate)
        {
            if (!_annotationSessions.TryGetValue(contextId, out var sessions))
            {
                if (!resolved.Enabled)
                {
                    // Nothing stored and nothing to store - already off.
                    return resolved;
                }

                sessions = [];
                _annotationSessions[contextId] = sessions;
            }

            lock (sessions)
            {
                if (resolved.Enabled)
                {
                    sessions[_userId] = resolved;
                }
                else if (sessions.Remove(_userId) && sessions.Count == 0)
                {
                    _annotationSessions.TryRemove(contextId, out _);
                }
            }
        }

        return resolved;
    }

    private ScreenAnnotationState BuildAnnotationState(ScreenAnnotationState requested) => new()
    {
        Enabled = requested.Enabled,
        SharerUserId = _userId,
        CaptureKind = requested.CaptureKind,
        SurfaceWidth = Math.Clamp(requested.SurfaceWidth, 0, 32768),
        SurfaceHeight = Math.Clamp(requested.SurfaceHeight, 0, 32768),
        OverlayActive = requested.OverlayActive,
        // 0 means "keep until cleared"; anything else is bounded so a bad client
        // cannot pin ink for hours by sending a huge value.
        AutoFadeSeconds = Math.Clamp(requested.AutoFadeSeconds, 0, 300)
    };

    /// <summary>Every share in the call that currently allows annotations.</summary>
    private static ScreenAnnotationState[] GetAnnotationSessionsSnapshot(Guid contextId)
    {
        if (!_annotationSessions.TryGetValue(contextId, out var sessions))
        {
            return [];
        }

        lock (sessions)
        {
            var snapshot = new ScreenAnnotationState[sessions.Count];
            var index = 0;
            foreach (var session in sessions.Values)
            {
                snapshot[index++] = session.Clone();
            }

            return snapshot;
        }
    }

    private static ScreenAnnotationState? GetAnnotationSession(Guid contextId, Guid sharerUserId)
    {
        if (!_annotationSessions.TryGetValue(contextId, out var sessions))
        {
            return null;
        }

        lock (sessions)
        {
            return sessions.GetValueOrDefault(sharerUserId);
        }
    }

    private ScreenAnnotationEvent SanitizeAnnotationEventOrThrow(Guid contextId, ScreenAnnotationEvent? annotationEvent)
    {
        if (annotationEvent == null)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Missing annotation payload");
        }

        // Which share was this drawn on? Coordinates are normalized to one captured
        // surface, so an event without a target cannot be placed on anything.
        var targetSharerUserId = annotationEvent.TargetSharerUserId;
        if (targetSharerUserId == Guid.Empty)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Annotation is missing its target share");
        }

        var session = GetAnnotationSession(contextId, targetSharerUserId);
        var isSharer = targetSharerUserId == _userId;

        // Permission is per share and granted by whoever owns it. A sharer with no
        // session has annotations off on their own screen, so nobody may draw there.
        if (session?.Enabled != true)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Annotations are not enabled for this share");
        }

        if (annotationEvent.Kind == ScreenAnnotationEventKind.ClearAll && !isSharer)
        {
            throw new ReturnStatusException(StatusCode.PermissionDenied, "Only the presenter can clear all annotations");
        }

        var points = annotationEvent.Points ?? [];
        if (points.Length % 2 != 0)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Annotation points must be x,y pairs");
        }

        if (points.Length > MaxAnnotationPointValues)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Annotation stroke is too large");
        }

        var strokeId = (annotationEvent.StrokeId ?? string.Empty).Trim();
        if (strokeId.Length > 64)
        {
            strokeId = strokeId[..64];
        }

        return new ScreenAnnotationEvent
        {
            Kind = annotationEvent.Kind,
            StrokeId = strokeId,
            Tool = annotationEvent.Tool,
            Color = annotationEvent.Color,
            Width = Math.Clamp(annotationEvent.Width, 0.0005f, 0.25f),
            Points = points,
            TargetSharerUserId = targetSharerUserId
        };
    }

    /// <summary>
    /// Drops the annotation session a departing participant owned and tells the
    /// rest of the call that share's annotations are off. Sessions belonging to
    /// anyone else still sharing are left alone.
    /// </summary>
    private static bool TryReleaseAnnotationSession(Guid contextId, Guid userId, out ScreenAnnotationState releasedState)
    {
        releasedState = new ScreenAnnotationState();

        lock (_annotationSessionsGate)
        {
            if (!_annotationSessions.TryGetValue(contextId, out var sessions))
            {
                return false;
            }

            lock (sessions)
            {
                if (!sessions.Remove(userId))
                {
                    return false;
                }

                if (sessions.Count == 0)
                {
                    _annotationSessions.TryRemove(contextId, out _);
                }
            }
        }

        releasedState = new ScreenAnnotationState { SharerUserId = userId };
        return true;
    }

    private async Task ReleaseRoomAnnotationSessionAsync(Guid roomId, Guid userId)
    {
        if (!TryReleaseAnnotationSession(roomId, userId, out var releasedState))
        {
            return;
        }

        await BroadcastToRoomCallAsync(roomId, receiver => receiver.OnScreenAnnotationStateChanged(roomId, releasedState));
    }

    private async Task ReleasePrivateAnnotationSessionAsync(Guid callContextId, Guid userId)
    {
        if (!TryReleaseAnnotationSession(callContextId, userId, out var releasedState))
        {
            return;
        }

        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
        privateCallGroup.All.OnScreenAnnotationStateChanged(callContextId, releasedState);
    }

    // ─── Presence re-registration ──────────────────────────────────────────────
    //
    // Called by a client that believes it is already in a call (for example after a
    // hub reconnect) to make sure the server still lists it. Only broadcasts when
    // this actually re-adds the user, so a redundant call is silent.

    public async ValueTask EnsureVoiceVideoPresenceAsync(Guid roomId)
    {
        if (!_joinedRooms.Contains(roomId))
            throw new ReturnStatusException(StatusCode.FailedPrecondition, "Not in this room");

        await EnsureCallEnabledAsync(roomId);

        var addedSelf = false;
        _voiceVideoUsers.AddOrUpdate(roomId,
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

        if (addedSelf)
        {
            var group = await RoomGroupAsync(roomId);
            group.All.OnUserJoinedVoiceVideo(roomId, _userId);
        }
    }

    public async ValueTask EnsurePrivateVoiceVideoPresenceAsync(Guid peerUserId)
    {
        if (peerUserId == Guid.Empty || peerUserId == _userId)
        {
            throw new ReturnStatusException(StatusCode.InvalidArgument, "Invalid private call target");
        }

        await EnsurePeerExistsAsync(peerUserId);

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

        // The group join is deliberately unconditional, unlike the broadcast below.
        // This is the reconnect path for DM calls and it is the ONLY thing that puts the
        // connection into the pcall group - the room path gets that from JoinRoomAsync,
        // but CallStore's private branch calls just this method. On a reconnect the user
        // is usually still present from the old connection, so addedSelf is false; if the
        // join were gated on it the reconnected client would rejoin nothing and go deaf
        // to every call event (signalling, annotations, participant leave) for the rest
        // of the call.
        var privateCallGroup = await PrivateCallGroupAsync(callContextId);
        _joinedPrivateCallContexts.Add(callContextId);

        if (addedSelf)
        {
            privateCallGroup.All.OnUserJoinedVoiceVideo(callContextId, _userId);
        }
    }

    private async Task<JanusSessionInfo> CreateJanusSessionInfoAsync(Guid contextId)
    {
        // Replace rather than accumulate: a rejoin of the same context supersedes the
        // previous session, and the old one must not be orphaned.
        await ReleaseJanusSessionForAsync(contextId, "superseded by a new session");

        var sessionId = await _janus.CreateSessionAsync();
        _janusSessions[(_userId, contextId)] = sessionId;
        var handleId = await _janus.AttachPluginAsync(sessionId);

        var guidBytes = contextId.ToByteArray();
        long janusRoomId = Math.Abs((long)(BitConverter.ToInt32(guidBytes, 0) ^ BitConverter.ToInt32(guidBytes, 4)));
        if (janusRoomId == 0) janusRoomId = 1;

        // JanusService caches room confirmation per socket connection, so it no
        // longer needs the session id.
        await _janus.EnsureVideoRoomAsync(janusRoomId);

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

    /// <summary>
    /// Membership guard. Every caller used this purely as a check and discarded the row,
    /// so it is an index-only probe against the unique (RoomId, UserId) index rather than
    /// a materialized - and tracked - ChatRoomMember.
    /// </summary>
    private static async Task EnsureMembershipAsync(AppDbContext db, Guid roomId, Guid userId)
    {
        if (!await db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId))
        {
            throw new ReturnStatusException(Grpc.Core.StatusCode.PermissionDenied, "Not a member of this room");
        }
    }

    /// <summary>Cached role for this connection, re-read whenever its generation moves.</summary>
    private async Task<UserRole> GetActorRoleAsync(AppDbContext db)
    {
        var generation = _roleGeneration.GetValueOrDefault(_userId);
        if (_actorRole == null || _actorRoleGeneration != generation)
        {
            _actorRole = await GetUserGlobalRoleAsync(db, _userId);
            _actorRoleGeneration = generation;
        }

        return _actorRole.Value;
    }

    /// <summary>
    /// Records a role read as of <paramref name="generation"/>. The generation must be
    /// sampled BEFORE the read, or a demotion landing in between would be stamped as
    /// already-seen and swallowed for the connection's lifetime.
    /// </summary>
    private void CacheActorRole(UserRole role, int generation)
    {
        _actorRole = role;
        _actorRoleGeneration = generation;
    }

    private static async Task<UserRole> GetUserGlobalRoleAsync(AppDbContext db, Guid userId)
    {
        var role = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.GlobalRole)
            .FirstOrDefaultAsync();

        return role ?? UserRole.Member;
    }

    /// <summary>Member ids for a room, excluding the caller. Own context so it can overlap other work.</summary>
    private async Task<Guid[]> LoadOtherRoomMemberIdsAsync(Guid roomId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ChatRoomMembers
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId != _userId)
            .Select(m => m.UserId)
            .Distinct()
            .ToArrayAsync();
    }

    /// <summary>Existence probe for a call peer; the user row itself is never used.</summary>
    private async Task EnsurePeerExistsAsync(Guid peerUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == peerUserId))
        {
            throw new ReturnStatusException(StatusCode.NotFound, "Target user not found");
        }
    }

    /// <summary>Destroys this user's Janus session for one call context, if any.</summary>
    private async Task ReleaseJanusSessionForAsync(Guid contextId, string reason)
    {
        if (!_janusSessions.TryRemove((_userId, contextId), out var sessionId))
        {
            return;
        }

        // Every destroy is recorded with who asked and why. A client whose media session
        // disappears sees only "session not found" on its next poll, with nothing on the
        // server tying that back to the decision that destroyed it - which makes a session
        // reaped by Janus indistinguishable from one this server tore down itself.
        _logger.LogInformation(
            "Destroying Janus session {Session} for user {User} in context {Context} ({Reason})",
            sessionId, _userId, contextId, reason);

        try
        {
            await _janus.DestroySessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            // Best effort: Janus reaps an idle session on its own, so a failure here costs
            // a delayed cleanup, not a broken call.
            _logger.LogWarning(ex, "Failed to destroy Janus session {Session}", sessionId);
        }
    }

    /// <summary>
    /// Destroys every Janus session held for one call context, whoever owns it.
    ///
    /// The per-user release above is driven by that user's own leave or disconnect,
    /// which never arrives when the context itself is destroyed underneath everyone.
    /// </summary>
    private async Task ReleaseAllJanusSessionsForContextAsync(Guid contextId)
    {
        foreach (var entry in _janusSessions)
        {
            if (entry.Key.ContextId != contextId || !_janusSessions.TryRemove(entry))
            {
                continue;
            }

            try
            {
                await _janus.DestroySessionAsync(entry.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to destroy Janus session {Session} for removed context {Context}",
                    entry.Value, contextId);
            }
        }
    }

    /// <summary>
    /// Releases Janus sessions for call contexts this user has actually left.
    ///
    /// Presence is the signal, not the disconnect itself. A client keeps its media session
    /// across a hub reconnect and the replacement connection re-registers presence before
    /// this runs, so tearing sessions down unconditionally would drop a live call on every
    /// transient disconnect.
    /// </summary>
    private async Task ReleaseOrphanedJanusSessionsAsync(IReadOnlySet<Guid> hadCallPresenceAtDisconnect)
    {
        foreach (var contextId in _joinedRooms.Concat(_joinedPrivateCallContexts).Distinct().ToArray())
        {
            // Still in the call, or was in it when this disconnect began. The second half
            // is what makes a reconnect survivable: the client keeps its media session
            // across a hub reconnect, and this teardown had already cleared the presence
            // the first half reads. If the client never does come back, Janus reaps the
            // session on its own idle timeout - which is exactly what that timeout is for,
            // and a far better outcome than killing a call that was coming back.
            if (HasCallPresence(contextId, _userId) || hadCallPresenceAtDisconnect.Contains(contextId))
            {
                continue;
            }

            await ReleaseJanusSessionForAsync(contextId, "no call presence on disconnect");
        }
    }

    private static bool HasCallPresence(Guid contextId, Guid userId)
    {
        if (_voiceVideoUsers.TryGetValue(contextId, out var roomUsers))
        {
            lock (roomUsers)
            {
                if (roomUsers.Contains(userId)) return true;
            }
        }

        if (_privateVoiceVideoUsers.TryGetValue(contextId, out var privateUsers))
        {
            lock (privateUsers)
            {
                if (privateUsers.Contains(userId)) return true;
            }
        }

        return false;
    }

    private async Task RemoveFromRoomGroupAsync(Guid roomId)
    {
        RemoveRoomConnection(roomId, ConnectionId);
        var group = await RoomGroupAsync(roomId);
        group.All.OnUserLeftRoom(roomId, _userId);
        await group.RemoveAsync(Context);
        _roomGroups.Remove(roomId);
    }

    private async Task InvalidateMessageCache(Guid roomId)
    {
        await _cache.RemoveAsync(CacheKeys.RoomMessages(roomId));
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
