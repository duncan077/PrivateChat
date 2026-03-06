using System.Collections.Concurrent;
using System.Text;
using Shared.Models;

namespace MusicBot.Services;

public sealed class MusicBotRuntime : IAsyncDisposable
{
    private readonly ApiClientService _api;
    private readonly AuthSession _auth;
    private readonly GrpcChannelProvider _channelProvider;
    private readonly ChatHubClient _chatHub;
    private readonly YoutubeAudioResolver _resolver;
    private readonly WebRtcBotBridge _webRtc;
    private readonly LocalAudioProxyService _localAudioProxy;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RoomPlaybackState> _roomStates = new();
    private readonly ConcurrentDictionary<Guid, string> _roomNames = new();
    private readonly object _activePlaybackSync = new();

    private ActivePlaybackState? _activePlayback;
    private string _commandPrefix = "!";
    private volatile bool _isRunning;

    private static readonly IReadOnlyDictionary<string, string> CommandAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = "play",
            ["p"] = "play",
            ["queue"] = "queue",
            ["q"] = "queue",
            ["list"] = "queue",
            ["skip"] = "skip",
            ["next"] = "skip",
            ["stop"] = "stop",
            ["join"] = "join",
            ["leave"] = "leave",
            ["remove"] = "remove",
            ["rm"] = "remove",
            ["del"] = "remove",
            ["delete"] = "remove",
            ["clear"] = "clear",
            ["nowplaying"] = "nowplaying",
            ["np"] = "nowplaying",
            ["now"] = "nowplaying",
            ["loop"] = "loop",
            ["shuffle"] = "shuffle",
            ["help"] = "help"
        };

    public bool IsRunning => _isRunning;
    public string Status { get; private set; } = "Stopped";

    public event Action<string>? LogLine;
    public event Action? StateChanged;

    public MusicBotRuntime(
        ApiClientService api,
        AuthSession auth,
        GrpcChannelProvider channelProvider,
        ChatHubClient chatHub,
        YoutubeAudioResolver resolver,
        WebRtcBotBridge webRtc,
        LocalAudioProxyService localAudioProxy)
    {
        _api = api;
        _auth = auth;
        _channelProvider = channelProvider;
        _chatHub = chatHub;
        _resolver = resolver;
        _webRtc = webRtc;
        _localAudioProxy = localAudioProxy;
    }

    public void ConfigureAntiBotVerifier(string? verifierUrl, string? apiKey)
        => _resolver.ConfigureExternalVerifier(verifierUrl, apiKey);

    public int ImportYoutubeCookies(string rawText)
        => _resolver.ImportCookies(rawText ?? string.Empty);

    public void ClearYoutubeCookies()
        => _resolver.ClearCookies();

    public async Task StartAsync(string serverUrl, string username, string password, string commandPrefix)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_isRunning)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(serverUrl)) throw new ArgumentException("Server URL is required.", nameof(serverUrl));
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username is required.", nameof(username));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Password is required.", nameof(password));

            Status = "Starting...";
            RaiseStateChanged();

            _commandPrefix = string.IsNullOrWhiteSpace(commandPrefix) ? "!" : commandPrefix.Trim();
            _channelProvider.ServerUrl = serverUrl.Trim();
            _channelProvider.ResetChannel();

            var authResponse = await _api.LoginAsync(username.Trim(), password);
            if (!authResponse.Success || string.IsNullOrWhiteSpace(authResponse.Token) || authResponse.User == null)
            {
                throw new InvalidOperationException(authResponse.Error ?? "Login failed.");
            }

            _auth.SetAuth(authResponse.Token, authResponse.User);

            _chatHub.MessageReceived += HandleChatMessage;
            _chatHub.ConnectionStateChanged += HandleChatHubConnectionStateChanged;
            _webRtc.Error += HandleWebRtcError;
            _webRtc.MusicBotStateChanged += HandleWebRtcMusicStateChanged;
            _webRtc.JanusSessionLost += HandleJanusSessionLost;

            await _chatHub.ConnectAsync();

            var rooms = await _api.GetRoomsAsync();
            foreach (var room in rooms)
            {
                _roomNames[room.Id] = string.IsNullOrWhiteSpace(room.Name)
                    ? room.Id.ToString("D")
                    : room.Name;

                await _chatHub.JoinRoomAsync(room.Id);
                EnsureRoomState(room.Id, room.Name);
            }

            _isRunning = true;
            Status = $"Running (joined {rooms.Length} room(s))";
            Log($"Bot started as {_auth.CurrentUser?.DisplayName ?? _auth.CurrentUser?.UserName ?? "unknown"}.");
            RaiseStateChanged();
        }
        catch
        {
            await SafeShutdownAsync();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_isRunning && _roomStates.IsEmpty)
            {
                Status = "Stopped";
                RaiseStateChanged();
                return;
            }

            Status = "Stopping...";
            RaiseStateChanged();
            await SafeShutdownAsync();
            Status = "Stopped";
            Log("Bot stopped.");
            RaiseStateChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<IReadOnlyList<string>> GetQueueSnapshotAsync()
    {
        var lines = new List<string>();
        foreach (var roomState in _roomStates.Values.OrderBy(s => ResolveRoomName(s.RoomId, s.RoomName)))
        {
            var roomName = ResolveRoomName(roomState.RoomId, roomState.RoomName);
            lock (roomState.Sync)
            {
                var flags = $"loop={(roomState.LoopEnabled ? "on" : "off")}, shuffle={(roomState.ShuffleEnabled ? "on" : "off")}";
                lines.Add($"[{roomName}] {flags}");

                if (roomState.IsPlaying && roomState.CurrentTrack != null)
                {
                    lines.Add($"[{roomName}] now: {FormatTrack(roomState.CurrentTrack)}");
                }

                if (roomState.Queue.Count == 0)
                {
                    lines.Add($"[{roomName}] queue: (empty)");
                }
                else
                {
                    for (var i = 0; i < roomState.Queue.Count; i++)
                    {
                        lines.Add($"[{roomName}] {i + 1}. {FormatTrack(roomState.Queue[i])}");
                    }
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("(no active room workers)");
        }

        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    private async Task SafeShutdownAsync()
    {
        _isRunning = false;

        _chatHub.MessageReceived -= HandleChatMessage;
        _chatHub.ConnectionStateChanged -= HandleChatHubConnectionStateChanged;
        _webRtc.Error -= HandleWebRtcError;
        _webRtc.MusicBotStateChanged -= HandleWebRtcMusicStateChanged;
        _webRtc.JanusSessionLost -= HandleJanusSessionLost;

        var roomStates = _roomStates.Values.ToList();
        foreach (var roomState in roomStates)
        {
            try { roomState.Cts.Cancel(); } catch { }
            try { roomState.Signal.Release(); } catch { }
        }

        foreach (var roomState in roomStates)
        {
            if (roomState.WorkerTask != null)
            {
                try { await roomState.WorkerTask; } catch { }
            }
            roomState.Signal.Dispose();
            roomState.Cts.Dispose();
        }

        _roomStates.Clear();

        CompleteActivePlayback(success: false, error: null);

        try
        {
            await _webRtc.StopMusicPlaybackAsync();
        }
        catch { }

        try
        {
            if (_webRtc.CurrentRoomId.HasValue)
            {
                await _chatHub.LeaveVoiceVideoAsync(_webRtc.CurrentRoomId.Value);
            }
        }
        catch { }

        try
        {
            await _webRtc.StopAsync("musicbot-stop");
        }
        catch { }

        try
        {
            await _chatHub.DisconnectAsync();
        }
        catch { }

        _auth.Clear();
        _channelProvider.ResetChannel();
    }

    private void HandleChatMessage(ChatMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleIncomingMessageAsync(message);
            }
            catch (Exception ex)
            {
                Log($"Unhandled message error: {ex.Message}");
            }
        });
    }

    private void HandleChatHubConnectionStateChanged(bool connected)
    {
        Log(connected ? "Chat hub connected." : "Chat hub disconnected.");

        if (!connected || !_isRunning)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var roomIds = _roomStates.Keys.Concat(_roomNames.Keys).Distinct().ToArray();
                foreach (var roomId in roomIds)
                {
                    await _chatHub.JoinRoomAsync(roomId);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to rejoin rooms after chat reconnect: {ex.Message}");
            }
        });
    }

    private async Task HandleIncomingMessageAsync(ChatMessage message)
    {
        if (!_isRunning)
        {
            return;
        }

        if (message.MessageType != ChatMessageType.Text)
        {
            return;
        }

        if (_auth.CurrentUser != null && message.SenderId == _auth.CurrentUser.Id)
        {
            return;
        }

        var content = message.Content?.Trim() ?? string.Empty;
        if (!TryParseCommand(content, out var command, out var args))
        {
            return;
        }

        var roomState = EnsureRoomState(message.RoomId, null);

        try
        {
            switch (command)
            {
                case "help":
                    await SendHelpAsync(message.RoomId);
                    break;
                case "join":
                    await HandleJoinAsync(roomState);
                    break;
                case "leave":
                    await HandleLeaveAsync(roomState);
                    break;
                case "play":
                    await HandlePlayAsync(roomState, message, args);
                    break;
                case "skip":
                    await HandleSkipAsync(roomState);
                    break;
                case "stop":
                    await HandleStopAsync(roomState);
                    break;
                case "queue":
                    await HandleQueueAsync(roomState);
                    break;
                case "remove":
                    await HandleRemoveAsync(roomState, args);
                    break;
                case "clear":
                    await HandleClearAsync(roomState);
                    break;
                case "nowplaying":
                    await HandleNowPlayingAsync(roomState);
                    break;
                case "loop":
                    await HandleLoopAsync(roomState, args);
                    break;
                case "shuffle":
                    await HandleShuffleAsync(roomState, args);
                    break;
                default:
                    await SendRoomMessageAsync(message.RoomId, $"Unknown command '{command}'. Use {_commandPrefix}help.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Command '{command}' failed in room {message.RoomId:D}: {ex.Message}");
            await SendRoomMessageAsync(message.RoomId, $"Command failed: {ex.Message}");
        }
        finally
        {
            RaiseStateChanged();
        }
    }
    private async Task SendHelpAsync(Guid roomId)
    {
        var msg =
            "MusicBot commands: " +
            "play|/play <query>, skip|/skip, stop, queue, nowplaying, " +
            "remove <index>, clear, loop [on|off|toggle], shuffle [on|off|toggle], join, leave.";
        await SendRoomMessageAsync(roomId, msg);
    }

    private async Task HandleJoinAsync(RoomPlaybackState roomState)
    {
        await EnsureTransportBoundToRoomAsync(roomState.RoomId, CancellationToken.None);
        await SendRoomMessageAsync(roomState.RoomId, $"Joined voice for {ResolveRoomName(roomState.RoomId, roomState.RoomName)}.");
    }

    private async Task HandleLeaveAsync(RoomPlaybackState roomState)
    {
        lock (roomState.Sync)
        {
            roomState.Queue.Clear();
            roomState.SkipRequested = true;
            roomState.StopRequested = true;
        }

        await RequestStopIfActiveRoomAsync(roomState.RoomId);
        await LeaveRoomIfActiveAsync(roomState.RoomId, "leave-command");
        await SendRoomMessageAsync(roomState.RoomId, "Left voice and cleared queue.");
    }

    private async Task HandlePlayAsync(RoomPlaybackState roomState, ChatMessage message, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            await SendRoomMessageAsync(roomState.RoomId, $"Usage: {_commandPrefix}play <query> or /play <query>");
            return;
        }

        var query = string.Join(' ', args).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await SendRoomMessageAsync(roomState.RoomId, "Please provide a query.");
            return;
        }

        Log($"Resolving '{query}' for room {roomState.RoomId:D}...");
        ResolvedTrack? resolved;
        try
        {
            resolved = await _resolver.ResolveAsync(query);
        }
        catch (Exception ex)
        {
            await SendRoomMessageAsync(roomState.RoomId, $"Resolve failed for '{query}': {ex.Message}");
            return;
        }

        if (resolved == null)
        {
            await SendRoomMessageAsync(roomState.RoomId, $"No match found for '{query}'.");
            return;
        }

        var queued = new QueuedTrack(
            Query: query,
            Track: resolved,
            RequestedByUserId: message.SenderId,
            RequestedByName: string.IsNullOrWhiteSpace(message.SenderName) ? "unknown" : message.SenderName,
            QueuedAtUtc: DateTimeOffset.UtcNow);

        int position;
        lock (roomState.Sync)
        {
            roomState.Queue.Add(queued);
            position = roomState.Queue.Count;
        }

        roomState.Signal.Release();
        await SendRoomMessageAsync(
            roomState.RoomId,
            $"Queued #{position}: {resolved.Title} ({FormatDuration(resolved.Duration)})");
    }

    private async Task HandleSkipAsync(RoomPlaybackState roomState)
    {
        var skipped = false;
        lock (roomState.Sync)
        {
            if (roomState.IsPlaying)
            {
                roomState.SkipRequested = true;
                skipped = true;
            }
        }

        if (!skipped)
        {
            await SendRoomMessageAsync(roomState.RoomId, "Nothing is currently playing.");
            return;
        }

        await RequestStopIfActiveRoomAsync(roomState.RoomId);
        await SendRoomMessageAsync(roomState.RoomId, "Skipping current track.");
    }

    private async Task HandleStopAsync(RoomPlaybackState roomState)
    {
        var removed = 0;
        var hadPlayback = false;

        lock (roomState.Sync)
        {
            removed = roomState.Queue.Count;
            roomState.Queue.Clear();
            roomState.SkipRequested = true;
            roomState.StopRequested = true;
            hadPlayback = roomState.IsPlaying;
        }

        if (hadPlayback)
        {
            await RequestStopIfActiveRoomAsync(roomState.RoomId);
        }

        await LeaveRoomIfActiveAsync(roomState.RoomId, "stop-command");

        await SendRoomMessageAsync(roomState.RoomId, $"Stopped playback and cleared {removed} queued track(s).");
    }

    private async Task HandleQueueAsync(RoomPlaybackState roomState)
    {
        var roomName = ResolveRoomName(roomState.RoomId, roomState.RoomName);
        var sb = new StringBuilder();

        lock (roomState.Sync)
        {
            sb.Append($"[{roomName}] loop={(roomState.LoopEnabled ? "on" : "off")}, shuffle={(roomState.ShuffleEnabled ? "on" : "off")}");

            if (roomState.IsPlaying && roomState.CurrentTrack != null)
            {
                sb.Append($" | now: {FormatTrack(roomState.CurrentTrack)}");
            }

            if (roomState.Queue.Count == 0)
            {
                sb.Append(" | queue: empty");
            }
            else
            {
                var limit = Math.Min(8, roomState.Queue.Count);
                for (var i = 0; i < limit; i++)
                {
                    sb.AppendLine();
                    sb.Append($"{i + 1}. {FormatTrack(roomState.Queue[i])}");
                }

                if (roomState.Queue.Count > limit)
                {
                    sb.AppendLine();
                    sb.Append($"... and {roomState.Queue.Count - limit} more");
                }
            }
        }

        await SendRoomMessageAsync(roomState.RoomId, sb.ToString());
    }

    private async Task HandleRemoveAsync(RoomPlaybackState roomState, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out var oneBasedIndex) || oneBasedIndex <= 0)
        {
            await SendRoomMessageAsync(roomState.RoomId, "Usage: remove <queue-index>");
            return;
        }

        QueuedTrack? removed = null;
        lock (roomState.Sync)
        {
            var idx = oneBasedIndex - 1;
            if (idx >= 0 && idx < roomState.Queue.Count)
            {
                removed = roomState.Queue[idx];
                roomState.Queue.RemoveAt(idx);
            }
        }

        if (removed == null)
        {
            await SendRoomMessageAsync(roomState.RoomId, $"Queue index {oneBasedIndex} is out of range.");
            return;
        }

        await SendRoomMessageAsync(roomState.RoomId, $"Removed #{oneBasedIndex}: {FormatTrack(removed)}");
    }

    private async Task HandleClearAsync(RoomPlaybackState roomState)
    {
        int removed;
        lock (roomState.Sync)
        {
            removed = roomState.Queue.Count;
            roomState.Queue.Clear();
        }

        await LeaveRoomIfIdleAsync(roomState, "clear-idle");

        await SendRoomMessageAsync(roomState.RoomId, $"Cleared {removed} queued track(s).");
    }

    private async Task HandleNowPlayingAsync(RoomPlaybackState roomState)
    {
        QueuedTrack? current;
        lock (roomState.Sync)
        {
            current = roomState.CurrentTrack;
        }

        if (current == null)
        {
            await SendRoomMessageAsync(roomState.RoomId, "Nothing is currently playing.");
            return;
        }

        await SendRoomMessageAsync(roomState.RoomId, $"Now playing: {FormatTrack(current)}");
    }

    private async Task HandleLoopAsync(RoomPlaybackState roomState, IReadOnlyList<string> args)
    {
        bool enabled;
        lock (roomState.Sync)
        {
            enabled = ApplyToggle(roomState.LoopEnabled, args.FirstOrDefault(), out var changed);
            roomState.LoopEnabled = enabled;
            if (!changed)
            {
                enabled = roomState.LoopEnabled;
            }
        }

        await SendRoomMessageAsync(roomState.RoomId, $"Loop is now {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task HandleShuffleAsync(RoomPlaybackState roomState, IReadOnlyList<string> args)
    {
        bool enabled;
        lock (roomState.Sync)
        {
            enabled = ApplyToggle(roomState.ShuffleEnabled, args.FirstOrDefault(), out var changed);
            roomState.ShuffleEnabled = enabled;
            if (!changed)
            {
                enabled = roomState.ShuffleEnabled;
            }
        }

        await SendRoomMessageAsync(roomState.RoomId, $"Shuffle is now {(enabled ? "enabled" : "disabled")}.");
    }

    private async Task SendRoomMessageAsync(Guid roomId, string text)
    {
        try
        {
            await _chatHub.SendMessageAsync(roomId, text);
        }
        catch (Exception ex)
        {
            Log($"Failed to send message to room {roomId:D}: {ex.Message}");
        }
    }

    private bool TryParseCommand(string content, out string command, out IReadOnlyList<string> args)
    {
        command = string.Empty;
        args = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        string body;

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            body = trimmed[1..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(_commandPrefix) && trimmed.StartsWith(_commandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            body = trimmed[_commandPrefix.Length..].Trim();
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var candidate = parts[0].Trim();
        if (!CommandAliases.TryGetValue(candidate, out var mapped) || string.IsNullOrWhiteSpace(mapped))
        {
            command = candidate.ToLowerInvariant();
        }
        else
        {
            command = mapped;
        }

        args = parts.Skip(1).ToArray();
        return true;
    }

    private static bool ApplyToggle(bool currentValue, string? arg, out bool changed)
    {
        changed = true;
        if (string.IsNullOrWhiteSpace(arg) || arg.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            return !currentValue;
        }

        if (arg.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        changed = false;
        return currentValue;
    }
    private RoomPlaybackState EnsureRoomState(Guid roomId, string? roomName)
    {
        if (!string.IsNullOrWhiteSpace(roomName))
        {
            _roomNames[roomId] = roomName.Trim();
        }

        var state = _roomStates.GetOrAdd(roomId, id => new RoomPlaybackState(id, ResolveRoomName(id, roomName)));

        if (!string.IsNullOrWhiteSpace(roomName))
        {
            lock (state.Sync)
            {
                state.RoomName = roomName.Trim();
            }
        }

        EnsureWorkerStarted(state);
        return state;
    }

    private void EnsureWorkerStarted(RoomPlaybackState state)
    {
        Task? previousTask;
        lock (state.Sync)
        {
            if (state.WorkerTask != null && !state.WorkerTask.IsCompleted)
            {
                return;
            }

            previousTask = state.WorkerTask;
            state.WorkerTask = Task.Run(() => RunRoomWorkerAsync(state));
        }

        if (previousTask?.IsFaulted == true)
        {
            var reason = previousTask.Exception?.GetBaseException().Message ?? "unknown";
            Log($"Restarted room worker for {state.RoomId:D} after fault: {reason}");
        }
    }

    private async Task RunRoomWorkerAsync(RoomPlaybackState roomState)
    {
        while (!roomState.Cts.Token.IsCancellationRequested)
        {
            try
            {
                await roomState.Signal.WaitAsync(roomState.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!roomState.Cts.Token.IsCancellationRequested)
            {
                QueuedTrack? nextTrack = null;
                var shouldShuffle = false;

                lock (roomState.Sync)
                {
                    if (roomState.IsPlaying)
                    {
                        break;
                    }

                    if (roomState.Queue.Count == 0)
                    {
                        break;
                    }

                    shouldShuffle = roomState.ShuffleEnabled;
                    var index = shouldShuffle
                        ? Random.Shared.Next(0, roomState.Queue.Count)
                        : 0;

                    nextTrack = roomState.Queue[index];
                    roomState.Queue.RemoveAt(index);
                    roomState.CurrentTrack = nextTrack;
                    roomState.IsPlaying = true;
                    roomState.StopRequested = false;
                    roomState.SkipRequested = false;
                }

                if (nextTrack == null)
                {
                    break;
                }

                var playbackSucceeded = false;
                try
                {
                    playbackSucceeded = await PlayTrackAsync(roomState, nextTrack);
                }
                catch (OperationCanceledException) when (roomState.Cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Room worker playback error in {roomState.RoomId:D}: {ex.Message}");
                }

                lock (roomState.Sync)
                {
                    var replay = playbackSucceeded && roomState.LoopEnabled && !roomState.SkipRequested && !roomState.StopRequested;
                    if (replay && roomState.CurrentTrack != null)
                    {
                        roomState.Queue.Add(roomState.CurrentTrack);
                    }

                    roomState.IsPlaying = false;
                    roomState.CurrentTrack = null;
                    roomState.SkipRequested = false;
                    roomState.StopRequested = false;

                    if (roomState.Queue.Count > 0)
                    {
                        roomState.Signal.Release();
                    }
                }

                RaiseStateChanged();
                await LeaveRoomIfIdleAsync(roomState, "idle-no-stream");
            }
        }
    }

    private async Task<bool> PlayTrackAsync(RoomPlaybackState roomState, QueuedTrack queuedTrack)
    {
        await _transportGate.WaitAsync(roomState.Cts.Token);
        try
        {
            if (!_isRunning || roomState.Cts.Token.IsCancellationRequested)
            {
                return false;
            }

            await EnsureTransportBoundToRoomLockedAsync(roomState.RoomId, roomState.Cts.Token);

            var candidateUrls = BuildCandidateUrls(queuedTrack.Track);
            if (candidateUrls.Count == 0)
            {
                await SendRoomMessageAsync(roomState.RoomId, $"Playback failed for '{queuedTrack.Track.Title}': no playable URLs.");
                return false;
            }

            var playback = CreateActivePlayback(roomState.RoomId);

            try
            {
                await _webRtc.StartMusicPlaybackAsync(candidateUrls, queuedTrack.Track.Title, 0.95);
                await SendRoomMessageAsync(roomState.RoomId, $"Now playing: {FormatTrack(queuedTrack)}");
            }
            catch (Exception ex)
            {
                CompleteActivePlayback(success: false, error: ex);
                await SendRoomMessageAsync(roomState.RoomId, $"Playback failed: {ex.Message}");
                return false;
            }

            var timeout = queuedTrack.Track.Duration > TimeSpan.Zero
                ? queuedTrack.Track.Duration + TimeSpan.FromSeconds(30)
                : TimeSpan.FromMinutes(15);

            var timeoutTask = Task.Delay(timeout, roomState.Cts.Token);
            var completed = await Task.WhenAny(playback.Completion.Task, timeoutTask);
            if (completed == timeoutTask)
            {
                Log($"Playback timeout in room {roomState.RoomId:D}: {queuedTrack.Track.Title}");
                try
                {
                    await _webRtc.StopMusicPlaybackAsync();
                }
                catch { }
            }
            else
            {
                try { await playback.Completion.Task; } catch { }
            }

            var success = completed != timeoutTask;
            CompleteActivePlayback(success, error: null);
            return success;
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async Task LeaveRoomIfIdleAsync(RoomPlaybackState roomState, string reason)
    {
        await _transportGate.WaitAsync();
        try
        {
            if (_webRtc.CurrentRoomId != roomState.RoomId)
            {
                return;
            }

            lock (roomState.Sync)
            {
                if (roomState.IsPlaying || roomState.Queue.Count > 0)
                {
                    return;
                }
            }

            lock (_activePlaybackSync)
            {
                if (_activePlayback?.RoomId == roomState.RoomId)
                {
                    return;
                }
            }

            try
            {
                await _webRtc.StopMusicPlaybackAsync();
            }
            catch { }

            try
            {
                await _chatHub.LeaveVoiceVideoAsync(roomState.RoomId);
            }
            catch { }

            try
            {
                await _webRtc.StopAsync(reason);
            }
            catch { }

            Log($"Released voice transport for room {roomState.RoomId:D} ({reason}).");
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async Task EnsureTransportBoundToRoomAsync(Guid roomId, CancellationToken ct)
    {
        await _transportGate.WaitAsync(ct);
        try
        {
            await EnsureTransportBoundToRoomLockedAsync(roomId, ct);
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async Task EnsureTransportBoundToRoomLockedAsync(Guid roomId, CancellationToken ct)
    {
        if (_webRtc.CurrentRoomId == roomId && _webRtc.CurrentJanusSessionId > 0)
        {
            return;
        }

        if (_webRtc.CurrentRoomId.HasValue)
        {
            try
            {
                await _webRtc.StopMusicPlaybackAsync();
            }
            catch { }

            try
            {
                await _chatHub.LeaveVoiceVideoAsync(_webRtc.CurrentRoomId.Value);
            }
            catch { }

            try
            {
                await _webRtc.StopAsync("switch-room");
            }
            catch { }
        }

        var session = await _chatHub.JoinVoiceVideoAsync(roomId);
        var user = _auth.CurrentUser ?? throw new InvalidOperationException("Bot is not authenticated.");
        var token = _auth.Token ?? throw new InvalidOperationException("Bot token is missing.");

        await _webRtc.InitAsync(
            session,
            token,
            user.Id,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName);

        Log($"Bound WebRTC transport to room {roomId:D}.");
    }

    private async Task LeaveRoomIfActiveAsync(Guid roomId, string reason)
    {
        await _transportGate.WaitAsync();
        try
        {
            if (_webRtc.CurrentRoomId != roomId)
            {
                return;
            }

            try
            {
                await _webRtc.StopMusicPlaybackAsync();
            }
            catch { }

            try
            {
                await _chatHub.LeaveVoiceVideoAsync(roomId);
            }
            catch { }

            try
            {
                await _webRtc.StopAsync(reason);
            }
            catch { }
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private async Task RequestStopIfActiveRoomAsync(Guid roomId)
    {
        ActivePlaybackState? playback;
        lock (_activePlaybackSync)
        {
            playback = _activePlayback;
        }

        if (playback == null || playback.RoomId != roomId)
        {
            return;
        }

        try
        {
            await _webRtc.StopMusicPlaybackAsync();
        }
        catch (Exception ex)
        {
            Log($"Stop playback request failed: {ex.Message}");
            CompleteActivePlayback(success: false, error: ex);
        }
    }

    private IReadOnlyList<string> BuildCandidateUrls(ResolvedTrack track)
    {
        var directUrls = (track.StreamUrls ?? Array.Empty<string>())
            .Where(static u => !string.IsNullOrWhiteSpace(u))
            .Select(static u => u.Trim())
            .ToArray();

        if (directUrls.Length == 0 && !string.IsNullOrWhiteSpace(track.StreamUrl))
        {
            directUrls = [track.StreamUrl.Trim()];
        }

        var proxiedUrls = _localAudioProxy.MapToProxyUrls(directUrls);

        return proxiedUrls
            .Concat(directUrls)
            .Where(static u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void HandleWebRtcError(string message)
    {
        var safe = string.IsNullOrWhiteSpace(message) ? "(empty)" : message.Trim();
        Log($"[WebRTC] {safe}");
        if (safe.Contains("musicbot", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("playback", StringComparison.OrdinalIgnoreCase))
        {
            CompleteActivePlayback(success: false, error: new InvalidOperationException(safe));
        }
    }

    private void HandleWebRtcMusicStateChanged(bool isPlaying, string? title)
    {
        if (isPlaying)
        {
            Log($"Playback started{(string.IsNullOrWhiteSpace(title) ? string.Empty : $": {title}")}");
        }
        else
        {
            Log($"Playback stopped{(string.IsNullOrWhiteSpace(title) ? string.Empty : $": {title}")}");
            CompleteActivePlayback(success: true, error: null);
        }

        RaiseStateChanged();
    }

    private void HandleJanusSessionLost()
    {
        Log("WebRTC Janus session lost.");
        CompleteActivePlayback(success: false, error: new InvalidOperationException("Janus session lost."));
        RaiseStateChanged();
    }

    private ActivePlaybackState CreateActivePlayback(Guid roomId)
    {
        var created = new ActivePlaybackState(roomId);
        lock (_activePlaybackSync)
        {
            _activePlayback = created;
        }

        return created;
    }

    private void CompleteActivePlayback(bool success, Exception? error)
    {
        ActivePlaybackState? existing;
        lock (_activePlaybackSync)
        {
            existing = _activePlayback;
            _activePlayback = null;
        }

        if (existing == null)
        {
            return;
        }

        if (error != null)
        {
            existing.Completion.TrySetException(error);
            return;
        }

        existing.Completion.TrySetResult(success);
    }

    private string ResolveRoomName(Guid roomId, string? fallback = null)
    {
        if (_roomNames.TryGetValue(roomId, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return roomId.ToString("D");
    }

    private static string FormatTrack(QueuedTrack queued)
        => $"{queued.Track.Title} ({FormatDuration(queued.Track.Duration)}) - req by {queued.RequestedByName}";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "live";
        }

        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }

    private void Log(string line)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogLine?.Invoke($"[{timestamp}] {line}");
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
        _transportGate.Dispose();
    }

    private sealed class RoomPlaybackState
    {
        public Guid RoomId { get; }
        public string RoomName { get; set; }
        public object Sync { get; } = new();
        public List<QueuedTrack> Queue { get; } = [];
        public SemaphoreSlim Signal { get; } = new(0);
        public CancellationTokenSource Cts { get; } = new();
        public Task? WorkerTask { get; set; }

        public QueuedTrack? CurrentTrack { get; set; }
        public bool IsPlaying { get; set; }
        public bool LoopEnabled { get; set; }
        public bool ShuffleEnabled { get; set; }
        public bool SkipRequested { get; set; }
        public bool StopRequested { get; set; }

        public RoomPlaybackState(Guid roomId, string roomName)
        {
            RoomId = roomId;
            RoomName = roomName;
        }
    }

    private sealed class ActivePlaybackState
    {
        public Guid RoomId { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActivePlaybackState(Guid roomId)
        {
            RoomId = roomId;
        }
    }

    private sealed record QueuedTrack(
        string Query,
        ResolvedTrack Track,
        Guid RequestedByUserId,
        string RequestedByName,
        DateTimeOffset QueuedAtUtc);
}
