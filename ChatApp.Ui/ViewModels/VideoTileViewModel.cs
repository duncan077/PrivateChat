using System;
using System.Linq;
using ChatApp.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatApp.Ui.ViewModels;

/// <summary>
/// One tile in the call grid: a video source plus the labelling around it.
/// <para>
/// The tile exists for as long as the source does, whether or not any picture is
/// arriving — a participant with their camera off still occupies a place in the grid,
/// showing their initials.
/// </para>
/// </summary>
public partial class VideoTileViewModel : ViewModelBase
{
    public IVideoSource Source { get; }

    /// <summary>Stable key for reconciling the grid against the engine's sources.</summary>
    public string Id => Source.Id;

    public Guid UserId => Source.UserId;

    public bool IsScreenShare => Source.Kind == RemoteStreamKind.ScreenShare;

    public bool IsLocal { get; }

    /// <summary>
    /// Front cameras are mirrored, because people expect their preview to behave like a
    /// mirror. A screen share never is: text would come out backwards.
    /// </summary>
    public bool IsMirrored => IsLocal && !IsScreenShare;

    public string DisplayName { get; }

    /// <summary>Fallback avatar text, shown whenever no picture is arriving.</summary>
    public string Initials { get; }

    [ObservableProperty]
    public partial bool IsSpeaking { get; set; }

    public VideoTileViewModel(IVideoSource source, bool isLocal)
    {
        Source = source;
        IsLocal = isLocal;

        var name = string.IsNullOrWhiteSpace(source.DisplayName)
            ? isLocal ? "You" : "Participant"
            : source.DisplayName;

        DisplayName = this switch
        {
            { IsScreenShare: true } => $"{name} — screen",
            { IsLocal: true } => $"{name} (you)",
            _ => name
        };

        Initials = BuildInitials(name);
    }

    private static string BuildInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var letters = words
            .Where(word => char.IsLetterOrDigit(word[0]))
            .Take(2)
            .Select(word => char.ToUpperInvariant(word[0]))
            .ToArray();

        return letters.Length > 0 ? new string(letters) : "?";
    }
}
