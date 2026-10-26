using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using ChatApp.Core.Media;
using ChatApp.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// The video surface of a call: one tile per stream, reconciled against the engine's
/// sources.
/// <para>
/// Replaces the DOM bookkeeping in <c>webrtc-manager.js</c>, which created and destroyed
/// <c>&lt;video&gt;</c> elements as publishers came and went.
/// </para>
/// </summary>
public partial class CallViewModel : ViewModelBase, IDisposable
{
    private readonly MediaEngineHost _media;
    private readonly AuthStateProvider _auth;
    private readonly IVideoSourceProvider? _sources;
    private bool _disposed;

    public ObservableCollection<VideoTileViewModel> Tiles { get; } = [];

    [ObservableProperty]
    public partial bool HasTiles { get; set; }

    public CallViewModel(MediaEngineHost media, AuthStateProvider auth)
    {
        _media = media;
        _auth = auth;
        _sources = media.Engine as IVideoSourceProvider;

        if (_sources is null)
            return;

        _sources.SourceAdded += HandleSourceAdded;
        _sources.SourceRemoved += HandleSourceRemoved;
        _media.SpeakingChanged += HandleSpeakingChanged;

        // Whatever is already live: this view model can be created after the call has
        // started, and the events for those sources have long since fired.
        foreach (var source in _sources.RemoteSources)
            HandleSourceAdded(source);

        if (_sources.LocalScreenShare is { } screen)
            HandleSourceAdded(screen);

        if (_sources.LocalPreview is { } preview)
            HandleSourceAdded(preview);
    }

    private void HandleSourceAdded(IVideoSource source) => OnUiThread(() =>
    {
        if (_disposed || Tiles.Any(tile => tile.Id == source.Id))
            return;

        var tile = new VideoTileViewModel(source, isLocal: IsLocal(source));

        Tiles.Insert(FindInsertIndex(tile), tile);
        HasTiles = Tiles.Count > 0;
    });

    private void HandleSourceRemoved(IVideoSource source) => OnUiThread(() =>
    {
        if (_disposed)
            return;

        var existing = Tiles.FirstOrDefault(tile => tile.Id == source.Id);
        if (existing is null)
            return;

        Tiles.Remove(existing);
        HasTiles = Tiles.Count > 0;
    });

    /// <summary>
    /// Keeps shares at the front and our own preview at the back, so the grid does not
    /// reshuffle every time somebody joins.
    /// </summary>
    private int FindInsertIndex(VideoTileViewModel tile)
    {
        var rank = Rank(tile);

        for (var index = 0; index < Tiles.Count; index++)
        {
            if (Rank(Tiles[index]) > rank)
                return index;
        }

        return Tiles.Count;
    }

    private static int Rank(VideoTileViewModel tile) => tile switch
    {
        { IsScreenShare: true } => 0,
        { IsLocal: false } => 1,
        _ => 2
    };

    private bool IsLocal(IVideoSource source)
    {
        var self = _auth.CurrentUser?.Id;

        // The id check stands alone because a call can start before the profile is
        // loaded, and because Janus feeds never use the local ids.
        return source.Id.StartsWith("local", StringComparison.Ordinal)
            || (self is { } id && id != Guid.Empty && source.UserId == id);
    }

    private void HandleSpeakingChanged(Guid userId, bool speaking) => OnUiThread(() =>
    {
        foreach (var tile in Tiles)
        {
            if (tile.UserId == userId && !tile.IsScreenShare)
                tile.IsSpeaking = speaking;
        }
    });

    /// <summary>
    /// Engine events arrive on capture, decode and poll-loop threads; every collection
    /// mutation has to reach the UI thread.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_sources is not null)
        {
            _sources.SourceAdded -= HandleSourceAdded;
            _sources.SourceRemoved -= HandleSourceRemoved;
            _media.SpeakingChanged -= HandleSpeakingChanged;
        }

        Tiles.Clear();
        GC.SuppressFinalize(this);
    }
}
