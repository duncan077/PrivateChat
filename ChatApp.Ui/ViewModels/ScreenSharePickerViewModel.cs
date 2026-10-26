using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChatApp.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChatApp.Ui.ViewModels;

/// <summary>One row in the share picker.</summary>
public sealed record ShareTargetItem(ScreenShareTarget Target)
{
    public string Title => Target.Title;

    public string Detail => Target is { Width: > 0, Height: > 0 }
        ? $"{Target.Width}×{Target.Height}"
        : Target.Kind.ToString();
}

/// <summary>
/// The share-target picker: the native stand-in for the browser's getDisplayMedia
/// dialog. Lists monitors today; windows and applications join the list when a
/// Windows Graphics Capture backend exists.
/// </summary>
public partial class ScreenSharePickerViewModel : ViewModelBase
{
    private readonly MediaEngineHost _media;

    public ObservableCollection<ShareTargetItem> Targets { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Raised with the chosen target, or null when cancelled.</summary>
    public event Action<ScreenShareTarget?>? Completed;

    public ScreenSharePickerViewModel(MediaEngineHost media)
    {
        _media = media;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var targets = await _media.Engine.GetScreenShareTargetsAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Targets.Clear();
                foreach (var target in targets)
                    Targets.Add(new ShareTargetItem(target));

                if (Targets.Count == 0)
                    Error = "Nothing available to share.";
            });
        }
        catch (Exception ex)
        {
            Error = $"Could not list screens: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Choose(ShareTargetItem item) => Completed?.Invoke(item.Target);

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}
