namespace ChatAppBlazor
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        private void OnBlazorWebViewInitialized(object? sender,
            Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
#if WINDOWS
            var webView2 = e.WebView;

            // Auto-grant camera and microphone permissions so getUserMedia() works
            // without prompting inside the BlazorWebView.
            webView2.CoreWebView2.PermissionRequested += (s, args) =>
            {
                if (args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera ||
                    args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Microphone)
                {
                    args.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
                }
            };

            // Accept self-signed / dev certificates for localhost so that fetch()
            // calls to https://localhost:5002/api/janus/... succeed inside WebView2.
            // Without this, WebView2 blocks the requests silently and Janus API
            // calls (publish, subscribe, poll) all fail with net::ERR_CERT errors.
            webView2.CoreWebView2.ServerCertificateErrorDetected += (s, args) =>
            {
                if (args.RequestUri.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase) ||
                    args.RequestUri.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    args.Action = Microsoft.Web.WebView2.Core.CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                }
            };
#elif ANDROID
            // On Android, manifest permissions (CAMERA, RECORD_AUDIO) handle the OS-level
            // grant. Ensure the WebView allows inline media playback without a user gesture.
            e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
#endif
        }
    }
}
