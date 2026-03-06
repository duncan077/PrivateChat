#if ANDROID
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
#endif
#if WINDOWS
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Windowing;
using WinRT.Interop;
#endif
namespace ChatAppBlazor
{
    public partial class MainPage : ContentPage
    {
        private void BlazorWebView_BlazorWebViewInitializing(object sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
        {
#if WINDOWS
            var arguments = State.CallStore.BuildWindowsWebView2Arguments();
            var existingArguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            e.EnvironmentOptions = new CoreWebView2EnvironmentOptions()
            {
                AdditionalBrowserArguments = MergeBrowserArguments(existingArguments, arguments)
            };
#endif
        }

#if WINDOWS
        private const string WindowsHostMessagePrefix = "__webrtcHost:";
        private Microsoft.UI.Xaml.Controls.WebView2? _windowsWebView;
        private Platforms.Windows.GlobalKeyboardPttHook? _pttHook;
        private Platforms.Windows.WindowsScreenShareAudioFallback? _screenShareAudioFallback;
        private bool _windowsInteropReady;
        private AppWindowPresenterKind _desktopFullscreenRestoreKind = AppWindowPresenterKind.Overlapped;
        private bool _desktopFullscreenRestoreMaximized;
        private bool _desktopFullscreenStateCaptured;
#endif

        public MainPage()
        {
#if WINDOWS
            ConfigureWindowsWebView2Arguments();
#endif
            InitializeComponent();
        }

#if WINDOWS
        private static void ConfigureWindowsWebView2Arguments()
        {
            var arguments = State.CallStore.BuildWindowsWebView2Arguments();
            var existingArguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            Environment.SetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                MergeBrowserArguments(existingArguments, arguments));
        }

        private static string? MergeBrowserArguments(string? existing, string? additional)
        {
            var existingTrimmed = string.IsNullOrWhiteSpace(existing) ? null : existing.Trim();
            var additionalTrimmed = string.IsNullOrWhiteSpace(additional) ? null : additional.Trim();

            if (existingTrimmed == null) return additionalTrimmed;
            if (additionalTrimmed == null) return existingTrimmed;

            return $"{existingTrimmed} {additionalTrimmed}";
        }
#endif

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
                _screenShareAudioFallback = new Platforms.Windows.WindowsScreenShareAudioFallback();
                _screenShareAudioFallback.ChunkCaptured += OnWindowsScreenAudioChunkCaptured;
                _screenShareAudioFallback.CaptureError += OnWindowsScreenAudioCaptureError;
                webView2.CoreWebView2.WebMessageReceived += OnWindowsWebMessageReceived;
            }
#elif ANDROID
            // On Android, manifest permissions (CAMERA, RECORD_AUDIO) handle the OS-level
            // grant. Ensure the WebView allows inline media playback without a user gesture.
            e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
            e.WebView.Settings.JavaScriptCanOpenWindowsAutomatically = true;
            e.WebView.Settings.TextZoom = 100;
            e.WebView.Settings.UseWideViewPort = true;
            e.WebView.Settings.LoadWithOverviewMode = false;
            e.WebView.Settings.BuiltInZoomControls = false;
            e.WebView.Settings.DisplayZoomControls = false;
            e.WebView.Settings.SetSupportZoom(false);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                e.WebView.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
            }
            e.WebView.SetWebChromeClient(new AndroidMediaWebChromeClient());
#endif
        }

#if ANDROID
        private sealed class AndroidMediaWebChromeClient : WebChromeClient
        {
            public override void OnPermissionRequest(PermissionRequest? request)
            {
                if (request == null)
                {
                    return;
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var requestedResources = request.GetResources() ?? Array.Empty<string>();
                    if (requestedResources.Length == 0)
                    {
                        request.Deny();
                        return;
                    }

                    // Let WebRTC access requested resources from WebView once Android runtime
                    // permissions are granted by MainActivity startup flow.
                    // Denying here can race with permission dialogs and leaves getUserMedia
                    // in a non-publishing state.
                    try
                    {
                        request.Grant(requestedResources);
                    }
                    catch
                    {
                        request.Deny();
                    }
                });
            }
        }
#endif

#if WINDOWS
        private void OnWindowsPttPressedChanged(bool isPressed)
        {
            var pressedValue = isPressed ? "true" : "false";
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var view = _windowsWebView;
                    if (view?.CoreWebView2 == null) return;
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
                var type = typeEl.GetString();
                if (string.Equals(type, "ptt-config", StringComparison.OrdinalIgnoreCase))
                {
                    var enabled =
                        root.TryGetProperty("enabled", out var enabledEl) &&
                        enabledEl.ValueKind == System.Text.Json.JsonValueKind.True;
                    var key = root.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;

                    _pttHook?.UpdateBinding(enabled, key);
                    return;
                }

                if (string.Equals(type, "screen-audio-fallback", StringComparison.OrdinalIgnoreCase))
                {
                    HandleWindowsScreenAudioFallbackMessage(root);
                    return;
                }

                if (string.Equals(type, "desktop-fullscreen", StringComparison.OrdinalIgnoreCase))
                {
                    HandleWindowsDesktopFullscreenMessage(root);
                }
            }
            catch { }
        }

        private void HandleWindowsDesktopFullscreenMessage(System.Text.Json.JsonElement root)
        {
            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var appWindow = TryGetWindowsAppWindow();
                    if (appWindow == null)
                    {
                        return;
                    }

                    var isFullscreen = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
                    var wantEnter = string.Equals(action, "enter", StringComparison.OrdinalIgnoreCase)
                        || (!string.Equals(action, "exit", StringComparison.OrdinalIgnoreCase) && !isFullscreen);
                    var wantExit = string.Equals(action, "exit", StringComparison.OrdinalIgnoreCase)
                        || (!string.Equals(action, "enter", StringComparison.OrdinalIgnoreCase) && isFullscreen);

                    if (wantEnter && !isFullscreen)
                    {
                        CaptureDesktopFullscreenRestoreState(appWindow);
                        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                        return;
                    }

                    if (wantExit && isFullscreen)
                    {
                        RestoreDesktopFullscreenState(appWindow);
                    }
                }
                catch
                {
                    // Keep host interop resilient; JS has browser fullscreen fallback.
                }
            });
        }

        private void CaptureDesktopFullscreenRestoreState(AppWindow appWindow)
        {
            _desktopFullscreenRestoreKind = appWindow.Presenter.Kind;
            _desktopFullscreenRestoreMaximized =
                appWindow.Presenter is OverlappedPresenter overlappedPresenter &&
                overlappedPresenter.State == OverlappedPresenterState.Maximized;
            _desktopFullscreenStateCaptured = true;
        }

        private void RestoreDesktopFullscreenState(AppWindow appWindow)
        {
            if (!_desktopFullscreenStateCaptured)
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                return;
            }

            if (_desktopFullscreenRestoreKind == AppWindowPresenterKind.Overlapped)
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_desktopFullscreenRestoreMaximized &&
                    appWindow.Presenter is OverlappedPresenter overlappedPresenter)
                {
                    overlappedPresenter.Maximize();
                }
                return;
            }

            appWindow.SetPresenter(_desktopFullscreenRestoreKind);
        }

        private AppWindow? TryGetWindowsAppWindow()
        {
            var mauiWindow = Window ?? Application.Current?.Windows.FirstOrDefault();
            var platformWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (platformWindow == null)
            {
                return null;
            }

            var hwnd = WindowNative.GetWindowHandle(platformWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        private void HandleWindowsScreenAudioFallbackMessage(System.Text.Json.JsonElement root)
        {
            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;

            if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            {
                var preferProcessIsolation =
                    root.TryGetProperty("preferProcessIsolation", out var preferEl) &&
                    preferEl.ValueKind == System.Text.Json.JsonValueKind.True;
                var targetLabel =
                    root.TryGetProperty("targetLabel", out var labelEl) &&
                    labelEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? labelEl.GetString()
                        : null;
                var allowSystemFallback =
                    !root.TryGetProperty("allowSystemFallback", out var allowFallbackEl) ||
                    allowFallbackEl.ValueKind == System.Text.Json.JsonValueKind.True;

                try
                {
                    var startResult = _screenShareAudioFallback?.Start(
                        new Platforms.Windows.WindowsScreenShareAudioFallback.StartRequest(
                            preferProcessIsolation,
                            targetLabel,
                            allowSystemFallback));

                    PostWindowsWebMessage(new
                    {
                        type = "screen-audio-fallback-state",
                        state = "started",
                        mode = startResult?.Mode ?? "system",
                        processId = startResult?.ProcessId,
                        windowTitle = startResult?.WindowTitle
                    });
                }
                catch (Exception ex)
                {
                    PostWindowsWebMessage(new
                    {
                        type = "screen-audio-fallback-state",
                        state = "error",
                        message = BuildErrorMessage(ex)
                    });
                }
                return;
            }

            if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                _screenShareAudioFallback?.Stop();
                PostWindowsWebMessage(new
                {
                    type = "screen-audio-fallback-state",
                    state = "stopped"
                });
            }
        }

        private void OnWindowsScreenAudioChunkCaptured(
            Platforms.Windows.WindowsScreenShareAudioFallback.AudioChunk chunk)
        {
            PostWindowsWebMessage(new
            {
                type = "screen-audio-fallback-chunk",
                format = "f32le",
                sampleRate = chunk.SampleRate,
                channels = chunk.Channels,
                data = chunk.Base64Data
            });
        }

        private void OnWindowsScreenAudioCaptureError(string message)
        {
            PostWindowsWebMessage(new
            {
                type = "screen-audio-fallback-state",
                state = "error",
                message
            });
        }

        private void PostWindowsWebMessage(object payload)
        {
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var view = _windowsWebView;
                    if (view?.CoreWebView2 == null) return;
                    view.CoreWebView2.PostWebMessageAsString($"{WindowsHostMessagePrefix}{payloadJson}");
                }
                catch
                {
                    // WebView may be shutting down.
                }
            });
        }

        private static string BuildErrorMessage(Exception ex)
        {
            var message = ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                if (!string.IsNullOrWhiteSpace(inner.Message) &&
                    !string.Equals(inner.Message, message, StringComparison.Ordinal))
                {
                    message += $" | {inner.Message}";
                }
                inner = inner.InnerException;
            }

            return message;
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

                if (_screenShareAudioFallback != null)
                {
                    _screenShareAudioFallback.ChunkCaptured -= OnWindowsScreenAudioChunkCaptured;
                    _screenShareAudioFallback.CaptureError -= OnWindowsScreenAudioCaptureError;
                    _screenShareAudioFallback.Dispose();
                    _screenShareAudioFallback = null;
                }
            }

            base.OnHandlerChanging(args);
        }
#endif
    }
}
