using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace ChatApp.Controls;

public class WebView2Host : NativeControlHost
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private nint _hwnd;
    private nint _parentHwnd;
    private string? _pendingUrl;
    private readonly List<string> _pendingMessages = [];
    private bool _initialized;
    private bool _isAttached;

    public event Action<string>? WebMessageReceived;
    public event Action? WebViewReady;

    private bool _nativeVisible = true;

    public CoreWebView2? CoreWebView2 => _webView;
    public bool IsReady => _webView != null;

    public void SetNativeVisible(bool visible)
    {
        _nativeVisible = visible;
        if (_controller != null)
            _controller.IsVisible = visible && _isAttached;
        if (_hwnd != nint.Zero)
            ShowWindow(_hwnd, visible && _isAttached ? SW_SHOW : SW_HIDE);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _parentHwnd = parent.Handle;

        if (_hwnd == nint.Zero)
        {
            var style = WS_CHILD | WS_CLIPCHILDREN;
            if (_nativeVisible) style |= WS_VISIBLE;

            _hwnd = CreateWindowEx(
                0,
                "static",
                "",
                style,
                0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height),
                parent.Handle,
                nint.Zero,
                nint.Zero,
                nint.Zero);
        }
        else
        {
            SetParent(_hwnd, parent.Handle);
            if (_nativeVisible) ShowWindow(_hwnd, SW_SHOW);
        }

        if (!_initialized)
        {
            _initialized = true;
            _ = InitializeWebViewAsync();
        }
        else if (_controller != null)
        {
            _controller.IsVisible = _nativeVisible;
            _controller.ParentWindow = _hwnd;
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        }

        _isAttached = true;

        if (!_nativeVisible)
            ShowWindow(_hwnd, SW_HIDE);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _isAttached = false;
        if (_controller != null)
        {
            _controller.IsVisible = false;
        }
        if (_hwnd != nint.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
            SetParent(_hwnd, nint.Zero);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrivateChat", "WebView2");

            _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_hwnd);
            _webView = _controller.CoreWebView2;

            _controller.IsVisible = _nativeVisible;
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));

            _webView.Settings.AreDefaultContextMenusEnabled = false;
            _webView.Settings.AreDevToolsEnabled = true;
            _webView.Settings.IsStatusBarEnabled = false;
            _webView.Settings.IsZoomControlEnabled = false;

            await _webView.CallDevToolsProtocolMethodAsync(
                "Browser.grantPermissions",
                "{\"permissions\":[\"audioCapture\",\"videoCapture\"]}");

            _webView.WebMessageReceived += (_, args) =>
            {
                var message = args.WebMessageAsJson;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => WebMessageReceived?.Invoke(message));
            };

            if (_pendingUrl != null)
            {
                _webView.Navigate(_pendingUrl);
                _pendingUrl = null;
            }

            foreach (var msg in _pendingMessages)
            {
                _webView.PostWebMessageAsJson(msg);
            }
            _pendingMessages.Clear();

            Avalonia.Threading.Dispatcher.UIThread.Post(() => WebViewReady?.Invoke());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    public void NavigateToFile(string filePath)
    {
        var url = new Uri(filePath).AbsoluteUri;
        if (_webView != null)
        {
            _webView.Navigate(url);
        }
        else
        {
            _pendingUrl = url;
        }
    }

    public void PostWebMessage(string json)
    {
        if (_webView != null)
        {
            _webView.PostWebMessageAsJson(json);
        }
        else
        {
            _pendingMessages.Add(json);
        }
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (_webView != null)
        {
            return await _webView.ExecuteScriptAsync(script);
        }
        return string.Empty;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (_controller != null && _isAttached)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, (int)finalSize.Width), Math.Max(1, (int)finalSize.Height));
        }
        return result;
    }

    public void Cleanup()
    {
        _controller?.Close();
        _controller = null;
        _webView = null;
        _environment = null;
        _initialized = false;

        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
