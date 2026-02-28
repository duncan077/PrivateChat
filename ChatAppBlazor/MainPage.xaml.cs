namespace ChatAppBlazor
{
    public partial class MainPage : ContentPage
    {
#if WINDOWS
        private Microsoft.UI.Xaml.Controls.WebView2? _windowsWebView;
        private Platforms.Windows.GlobalKeyboardPttHook? _pttHook;
        private bool _windowsInteropReady;
#endif

        public MainPage()
        {
            InitializeComponent();
        }

        private void OnBlazorWebViewInitialized(object? sender,
            Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
        {
#if WINDOWS
            var webView2 = e.WebView;
            _windowsWebView = webView2;

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

            if (!_windowsInteropReady)
            {
                _windowsInteropReady = true;
                _pttHook = new Platforms.Windows.GlobalKeyboardPttHook();
                _pttHook.PttPressedChanged += OnWindowsPttPressedChanged;
                webView2.CoreWebView2.WebMessageReceived += OnWindowsWebMessageReceived;
            }
#elif ANDROID
            // On Android, manifest permissions (CAMERA, RECORD_AUDIO) handle the OS-level
            // grant. Ensure the WebView allows inline media playback without a user gesture.
            e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
#endif
        }

#if WINDOWS
        private void OnWindowsPttPressedChanged(bool isPressed)
        {
            var view = _windowsWebView;
            if (view?.CoreWebView2 == null) return;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var pressedValue = isPressed ? "true" : "false";
                    await view.ExecuteScriptAsync($"window.webrtcManager?.nativeSetPttPressed({pressedValue});");
                }
                catch { }
            });
        }

        private void OnWindowsWebMessageReceived(
            object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var payload = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
                var root = payload.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                if (!string.Equals(typeEl.GetString(), "ptt-config", StringComparison.OrdinalIgnoreCase)) return;

                var enabled =
                    root.TryGetProperty("enabled", out var enabledEl) &&
                    enabledEl.ValueKind == System.Text.Json.JsonValueKind.True;
                var key = root.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;

                _pttHook?.UpdateBinding(enabled, key);
            }
            catch { }
        }

        protected override void OnHandlerChanging(HandlerChangingEventArgs args)
        {
            if (args.NewHandler == null)
            {
                if (_windowsWebView?.CoreWebView2 != null)
                {
                    _windowsWebView.CoreWebView2.WebMessageReceived -= OnWindowsWebMessageReceived;
                }

                if (_pttHook != null)
                {
                    _pttHook.PttPressedChanged -= OnWindowsPttPressedChanged;
                    _pttHook.Dispose();
                    _pttHook = null;
                }
            }

            base.OnHandlerChanging(args);
        }
#endif
    }
}
