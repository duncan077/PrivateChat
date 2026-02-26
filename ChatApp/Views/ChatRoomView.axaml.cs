using Avalonia.Controls;
using Avalonia.Interactivity;
using ChatApp.ViewModels;

namespace ChatApp.Views;

public partial class ChatRoomView : UserControl
{
    public ChatRoomView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Attach the embedded WebView to the VoiceVideoViewModel so it can
        // render the WebRTC session directly inside the chat room window.
        if (DataContext is ChatRoomViewModel vm)
        {
            vm.VoiceVideo.AttachWebView(CallWebViewHost);
        }
    }
}
