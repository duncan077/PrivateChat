#if ANDROID
using Android.OS;
using Android.Webkit;
#endif
#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace MusicBot;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
#if WINDOWS
        ConfigureWindowsWebView2Arguments();
#endif
        InitializeComponent();
    }

    private void BlazorWebView_BlazorWebViewInitializing(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
    {
#if WINDOWS
        var existing = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
        e.EnvironmentOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = MergeBrowserArguments(existing, BuildWindowsWebView2Arguments())
        };
#endif
    }

    private void OnBlazorWebViewInitialized(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        e.WebView.CoreWebView2.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.Camera ||
                args.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                args.State = CoreWebView2PermissionState.Allow;
            }
        };
#elif ANDROID
        e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
        e.WebView.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            e.WebView.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        }

        e.WebView.SetWebChromeClient(new AndroidMediaWebChromeClient());
#endif
    }

#if WINDOWS
    private static void ConfigureWindowsWebView2Arguments()
    {
        var existing = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            MergeBrowserArguments(existing, BuildWindowsWebView2Arguments()));
    }

    private static string? MergeBrowserArguments(string? existing, string? additional)
    {
        var existingTrimmed = string.IsNullOrWhiteSpace(existing) ? null : existing.Trim();
        var additionalTrimmed = string.IsNullOrWhiteSpace(additional) ? null : additional.Trim();

        if (existingTrimmed == null) return additionalTrimmed;
        if (additionalTrimmed == null) return existingTrimmed;
        return $"{existingTrimmed} {additionalTrimmed}";
    }

    private static string BuildWindowsWebView2Arguments()
    {
        var args = new[]
        {
            "--disable-features=WebRtcHideLocalIpsWithMdns",
            "--force-webrtc-ip-handling-policy=default_public_interface_only",
            // Required for loopback audio proxy URLs (http://127.0.0.1:port/audio-proxy?...).
            "--allow-running-insecure-content",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-features=CalculateNativeWinOcclusion",
            "--autoplay-policy=no-user-gesture-required",
            "--enable-accelerated-video-decode",
            "--enable-accelerated-video-encode",
            "--enable-gpu-rasterization",
            "--enable-zero-copy"
        };

        return string.Join(" ", args);
    }
#endif

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
}
