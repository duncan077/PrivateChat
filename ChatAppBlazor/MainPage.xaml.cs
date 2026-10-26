#if ANDROID
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
#endif
#if WINDOWS
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Windowing;
using System.Diagnostics;
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
            ConfigureWindowsPerformancePriority();
            ConfigureWindowsWebView2Arguments();
#endif
            InitializeComponent();
        }

#if WINDOWS
        private static void ConfigureWindowsPerformancePriority()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                TryElevateProcessPriorityAndAffinity(process);
            }
            catch
            {
                // Best effort; priority elevation may fail under some policies.
            }
        }

        private static void ApplyWindowsProcessPerformanceTuning(int webViewBrowserProcessId = 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var currentProcess = Process.GetCurrentProcess();
                    TryElevateProcessPriorityAndAffinity(currentProcess);
                }
                catch { }

                if (webViewBrowserProcessId <= 0)
                {
                    return;
                }

                try
                {
                    using var webViewProcess = Process.GetProcessById(webViewBrowserProcessId);
                    if (!webViewProcess.HasExited)
                    {
                        TryElevateProcessPriorityAndAffinity(webViewProcess);
                    }
                }
                catch { }
            });
        }

        private static void TryElevateProcessPriorityAndAffinity(Process process)
        {
            try
            {
                process.PriorityBoostEnabled = true;
            }
            catch { }

            try
            {
                if (process.PriorityClass < ProcessPriorityClass.AboveNormal)
                {
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
            }
            catch { }
        }

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
            var webViewBrowserProcessId = 0;
            try
            {
                webViewBrowserProcessId = checked((int)webView2.CoreWebView2.BrowserProcessId);
            }
            catch { }
            ApplyWindowsProcessPerformanceTuning(webViewBrowserProcessId);

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
        internal sealed class AndroidMediaWebChromeClient : WebChromeClient
        {
            // Chromium only enables the HTML5 Fullscreen API inside a WebView when
            // the chrome client implements the custom-view contract below. Without
            // it, document.fullscreenEnabled is false and every requestFullscreen()
            // call rejects - which is why the fullscreen button did nothing on
            // Android. The "custom view" Chromium hands over IS the rendered
            // fullscreen content; we just have to host it over the activity.
            private static AndroidMediaWebChromeClient? s_fullscreenOwner;

            private Android.Views.View? _customView;
            private ICustomViewCallback? _customViewCallback;

            /// <summary>
            /// Closes the active fullscreen view, if any. Called from the activity's
            /// back handling so back exits fullscreen instead of leaving the app.
            /// </summary>
            internal static bool TryCloseFullscreenView()
            {
                var owner = s_fullscreenOwner;
                if (owner?._customView == null)
                {
                    return false;
                }

                owner.OnHideCustomView();
                return true;
            }

            public override void OnShowCustomView(Android.Views.View? view, ICustomViewCallback? callback)
            {
                if (view == null || callback == null)
                {
                    return;
                }

                // Chromium contract: a second fullscreen request while one is live
                // must be refused by hiding the new view immediately.
                if (_customView != null)
                {
                    try { callback.OnCustomViewHidden(); } catch { }
                    return;
                }

                var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView
                    as Android.Widget.FrameLayout;
                if (decor == null)
                {
                    try { callback.OnCustomViewHidden(); } catch { }
                    return;
                }

                _customView = view;
                _customViewCallback = callback;
                s_fullscreenOwner = this;

                view.SetBackgroundColor(Android.Graphics.Color.Black);
                // Watching a call is exactly when the screen must not sleep.
                view.KeepScreenOn = true;

                decor.AddView(view, new Android.Widget.FrameLayout.LayoutParams(
                    Android.Views.ViewGroup.LayoutParams.MatchParent,
                    Android.Views.ViewGroup.LayoutParams.MatchParent));
            }

            public override void OnHideCustomView()
            {
                var view = _customView;
                if (view == null)
                {
                    return;
                }

                _customView = null;
                var callback = _customViewCallback;
                _customViewCallback = null;
                if (ReferenceEquals(s_fullscreenOwner, this))
                {
                    s_fullscreenOwner = null;
                }

                (view.Parent as Android.Views.ViewGroup)?.RemoveView(view);
                view.KeepScreenOn = false;

                // Tells Chromium the view is gone so the page's fullscreenchange
                // event fires and the DOM state stays in sync.
                try { callback?.OnCustomViewHidden(); } catch { }
            }

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
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return;
                }
                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = TryGetJsonString(typeEl);
                if (string.IsNullOrWhiteSpace(type))
                {
                    return;
                }
                if (string.Equals(type, "ptt-config", StringComparison.OrdinalIgnoreCase))
                {
                    var enabled =
                        root.TryGetProperty("enabled", out var enabledEl) &&
                        enabledEl.ValueKind == System.Text.Json.JsonValueKind.True;
                    var key = root.TryGetProperty("key", out var keyEl) ? TryGetJsonString(keyEl) : null;

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

        private static string? TryGetJsonString(System.Text.Json.JsonElement element)
            => element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : null;

        private void HandleWindowsDesktopFullscreenMessage(System.Text.Json.JsonElement root)
        {
            var action = root.TryGetProperty("action", out var actionEl) ? TryGetJsonString(actionEl) : null;

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
            var action = root.TryGetProperty("action", out var actionEl) ? TryGetJsonString(actionEl) : null;

            if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            {
                var preferProcessIsolation =
                    root.TryGetProperty("preferProcessIsolation", out var preferEl) &&
                    preferEl.ValueKind == System.Text.Json.JsonValueKind.True;
                var targetLabel =
                    root.TryGetProperty("targetLabel", out var labelEl) &&
                    labelEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? TryGetJsonString(labelEl)
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

                    PostWindowsWebMessage(
                        new Serialization.ScreenAudioFallbackStateMessage
                        {
                            State = "started",
                            Mode = startResult?.Mode ?? "system",
                            ProcessId = startResult?.ProcessId,
                            WindowTitle = startResult?.WindowTitle
                        },
                        Serialization.AppJsonContext.Default.ScreenAudioFallbackStateMessage);
                }
                catch (Exception ex)
                {
                    PostWindowsWebMessage(
                        new Serialization.ScreenAudioFallbackStateMessage
                        {
                            State = "error",
                            Message = BuildErrorMessage(ex)
                        },
                        Serialization.AppJsonContext.Default.ScreenAudioFallbackStateMessage);
                }
                return;
            }

            if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                _screenShareAudioFallback?.Stop();
                PostWindowsWebMessage(
                    new Serialization.ScreenAudioFallbackStateMessage { State = "stopped" },
                    Serialization.AppJsonContext.Default.ScreenAudioFallbackStateMessage);
            }
        }

        private void OnWindowsScreenAudioChunkCaptured(
            Platforms.Windows.WindowsScreenShareAudioFallback.AudioChunk chunk)
        {
            PostWindowsWebMessage(
                new Serialization.ScreenAudioFallbackChunkMessage
                {
                    SampleRate = chunk.SampleRate,
                    Channels = chunk.Channels,
                    Data = chunk.Base64Data
                },
                Serialization.AppJsonContext.Default.ScreenAudioFallbackChunkMessage);
        }

        private void OnWindowsScreenAudioCaptureError(string message)
        {
            PostWindowsWebMessage(
                new Serialization.ScreenAudioFallbackStateMessage
                {
                    State = "error",
                    Message = message
                },
                Serialization.AppJsonContext.Default.ScreenAudioFallbackStateMessage);
        }

        private void PostWindowsWebMessage<T>(
            T payload,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        {
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, typeInfo);
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
