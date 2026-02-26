using Avalonia.Controls;
using Avalonia.Interactivity;
using ChatApp.ViewModels;

namespace ChatApp.Views;

public partial class VoiceVideoView : UserControl
{
    public VoiceVideoView()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is VoiceVideoViewModel vm)
        {
            vm.AttachWebView(WebViewHost);
            await vm.LoadRoomsAsync();
        }
    }
}
