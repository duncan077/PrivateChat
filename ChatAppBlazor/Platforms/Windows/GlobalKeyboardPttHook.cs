#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatAppBlazor.Platforms.Windows;

internal sealed class GlobalKeyboardPttHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly object _sync = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly IntPtr _hookHandle;
    private readonly HashSet<int> _pressedKeys = [];

    private bool _enabled;
    private bool _pttActive;
    private PttBinding _binding = PttBinding.Default;
    private bool _disposed;

    public event Action<bool>? PttPressedChanged;

    public GlobalKeyboardPttHook()
    {
        _proc = HookCallback;
        _hookHandle = InstallHook(_proc);
    }

    public void UpdateBinding(bool enabled, string? bindingText)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _binding = PttBinding.Parse(bindingText);
            _pressedKeys.Clear();
            EmitIfChangedUnsafe(false);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = unchecked((int)(long)wParam);
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = unchecked((int)data.vkCode);

            lock (_sync)
            {
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    _pressedKeys.Add(vk);
                }
                else if (message is WM_KEYUP or WM_SYSKEYUP)
                {
                    _pressedKeys.Remove(vk);
                }

                EvaluateAndEmitUnsafe();
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void EvaluateAndEmitUnsafe()
    {
        var isPressed = _enabled && IsBindingPressedUnsafe(_binding);
        EmitIfChangedUnsafe(isPressed);
    }

    private void EmitIfChangedUnsafe(bool isPressed)
    {
        if (_pttActive == isPressed) return;
        _pttActive = isPressed;
        PttPressedChanged?.Invoke(isPressed);
    }

    private bool IsBindingPressedUnsafe(PttBinding binding)
    {
        if (binding.Ctrl && !IsCtrlPressedUnsafe()) return false;
        if (binding.Alt && !IsAltPressedUnsafe()) return false;
        if (binding.Shift && !IsShiftPressedUnsafe()) return false;
        if (binding.Meta && !IsMetaPressedUnsafe()) return false;

        if (binding.KeyVk is int keyVk)
        {
            return _pressedKeys.Contains(keyVk);
        }

        // Modifier-only bindings are allowed.
        return binding.Ctrl || binding.Alt || binding.Shift || binding.Meta;
    }

    private bool IsCtrlPressedUnsafe() =>
        _pressedKeys.Contains(0x11) || _pressedKeys.Contains(0xA2) || _pressedKeys.Contains(0xA3);

    private bool IsAltPressedUnsafe() =>
        _pressedKeys.Contains(0x12) || _pressedKeys.Contains(0xA4) || _pressedKeys.Contains(0xA5);

    private bool IsShiftPressedUnsafe() =>
        _pressedKeys.Contains(0x10) || _pressedKeys.Contains(0xA0) || _pressedKeys.Contains(0xA1);

    private bool IsMetaPressedUnsafe() =>
        _pressedKeys.Contains(0x5B) || _pressedKeys.Contains(0x5C);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnhookWindowsHookEx(_hookHandle);
    }

    private static IntPtr InstallHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleName = currentModule?.ModuleName;
        var moduleHandle = string.IsNullOrWhiteSpace(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private readonly record struct PttBinding(bool Ctrl, bool Alt, bool Shift, bool Meta, int? KeyVk)
    {
        public static readonly PttBinding Default = new(false, false, false, false, 0x20); // Space

        public static PttBinding Parse(string? bindingText)
        {
            if (string.IsNullOrWhiteSpace(bindingText))
            {
                return Default;
            }

            var ctrl = false;
            var alt = false;
            var shift = false;
            var meta = false;
            int? keyVk = null;

            foreach (var raw in bindingText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                    case "controlleft":
                    case "controlright":
                        ctrl = true;
                        continue;
                    case "alt":
                    case "altleft":
                    case "altright":
                        alt = true;
                        continue;
                    case "shift":
                    case "shiftleft":
                    case "shiftright":
                        shift = true;
                        continue;
                    case "meta":
                    case "metaleft":
                    case "metaright":
                    case "cmd":
                    case "win":
                        meta = true;
                        continue;
                }

                keyVk = MapCodeToVirtualKey(raw);
            }

            if (keyVk is null && !ctrl && !alt && !shift && !meta)
            {
                keyVk = 0x20; // Space fallback
            }

            return new PttBinding(ctrl, alt, shift, meta, keyVk);
        }

        private static int? MapCodeToVirtualKey(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
            {
                return char.ToUpperInvariant(code[3]);
            }

            if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6 && char.IsDigit(code[5]))
            {
                return code[5];
            }

            if (code.StartsWith("F", StringComparison.Ordinal) &&
                int.TryParse(code[1..], out var fKey) &&
                fKey is >= 1 and <= 24)
            {
                return 0x6F + fKey;
            }

            if (code.StartsWith("Numpad", StringComparison.Ordinal))
            {
                var token = code["Numpad".Length..];
                if (token.Length == 1 && char.IsDigit(token[0]))
                {
                    return 0x60 + (token[0] - '0');
                }
            }

            return code switch
            {
                "Space" => 0x20,
                "Tab" => 0x09,
                "Enter" => 0x0D,
                "Escape" => 0x1B,
                "Backspace" => 0x08,
                "Backquote" => 0xC0,
                "CapsLock" => 0x14,
                "ArrowUp" => 0x26,
                "ArrowDown" => 0x28,
                "ArrowLeft" => 0x25,
                "ArrowRight" => 0x27,
                "Minus" => 0xBD,
                "Equal" => 0xBB,
                "BracketLeft" => 0xDB,
                "BracketRight" => 0xDD,
                "Backslash" => 0xDC,
                "Semicolon" => 0xBA,
                "Quote" => 0xDE,
                "Comma" => 0xBC,
                "Period" => 0xBE,
                "Slash" => 0xBF,
                "NumpadDecimal" => 0x6E,
                "NumpadAdd" => 0x6B,
                "NumpadSubtract" => 0x6D,
                "NumpadMultiply" => 0x6A,
                "NumpadDivide" => 0x6F,
                _ => null
            };
        }
    }
}
#endif
