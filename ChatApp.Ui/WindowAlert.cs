using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace ChatApp.Ui;

/// <summary>
/// Attention without toasts: flashes the taskbar button until the window regains
/// focus. Plain P/Invoke — no WinAppSDK, no packaging requirements, nothing for
/// NativeAOT to trip over. On non-Windows platforms it is a no-op; the in-app unread
/// badges remain the signal there.
/// </summary>
public static class WindowAlert
{
    /// <summary>
    /// Flashes the main window if it is not currently focused. Safe from any thread.
    /// </summary>
    public static void Flash()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = MainWindow();
            if (window is null || window.IsActive)
                return;

            if (!OperatingSystem.IsWindows())
                return;

            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0)
                return;

            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = handle,
                // Flash caption and taskbar, keep flashing until the window comes to
                // the foreground — the standard "you have something waiting" pattern.
                Flags = FlashAll | FlashTimerNoForeground,
                Count = 0,
                Timeout = 0
            };

            _ = FlashWindowEx(ref info);
        });
    }

    /// <summary>True when the app's main window has focus — used to skip alerts.</summary>
    public static bool IsWindowActive()
        => Dispatcher.UIThread.CheckAccess()
            ? MainWindow()?.IsActive == true
            : Dispatcher.UIThread.Invoke(() => MainWindow()?.IsActive == true);

    /// <summary>Restores and fronts the main window, e.g. from the tray icon.</summary>
    public static void ActivateMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = MainWindow();
            if (window is null)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Show();
            window.Activate();
        });
    }

    private static Window? MainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private const uint FlashAll = 3;
    private const uint FlashTimerNoForeground = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
