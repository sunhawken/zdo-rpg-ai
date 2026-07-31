using System.Runtime.InteropServices;
using ZdoRpgAi.Core;

namespace ZdoRpgAi.Client.Hotkey;

public class WindowsHotkeyListener : IHotkeyListener {
    private static readonly ILog Log = Logger.Get<WindowsHotkeyListener>();

    private readonly int _virtualKeyCode;
    private readonly string _keyName;
    private readonly int _pollIntervalMs;
    private volatile bool _wasPressed;
    private volatile bool _suppressed;
    private CancellationTokenSource? _cts;

    public event Action? KeyPressed;
    public event Action? KeyReleased;

    public WindowsHotkeyListener(string keyName, int pollIntervalMs = 30) {
        _keyName = keyName;
        _virtualKeyCode = MapVirtualKeyCode(keyName);
        _pollIntervalMs = pollIntervalMs;
    }

    public async Task RunAsync(CancellationToken ct) {
        Log.Info("Global hotkey: {Key} (Windows vk=0x{Code:X2}, polling every {Ms}ms)",
            _keyName, _virtualKeyCode, _pollIntervalMs);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linked;

        try {
            while (!linked.Token.IsCancellationRequested) {
                // High bit set means the key is currently down. Works globally (any window
                // focused, including a fullscreen/exclusive game), no special permissions needed.
                var isPressed = (GetAsyncKeyState(_virtualKeyCode) & 0x8000) != 0;

                if (isPressed && !_wasPressed) {
                    _wasPressed = true;
                    if (!_suppressed) {
                        Log.Debug("Hotkey pressed: {Key}", _keyName);
                        KeyPressed?.Invoke();
                    }
                }
                else if (!isPressed && _wasPressed) {
                    _wasPressed = false;
                    if (!_suppressed) {
                        Log.Debug("Hotkey released: {Key}", _keyName);
                        KeyReleased?.Invoke();
                    }
                }

                await Task.Delay(_pollIntervalMs, linked.Token);
            }
        }
        catch (OperationCanceledException) {
            // Normal shutdown
        }

        Log.Debug("Hotkey polling stopped");
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Windows virtual-key codes (winuser.h)
    private static int MapVirtualKeyCode(string keyName) {
        var upper = keyName.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) {
            return upper[0];
        }

        // Numpad digits (VK_NUMPAD0..VK_NUMPAD9 = 0x60..0x69) are distinct virtual-key codes from
        // the top-row digits above -- "NUM1"/"NUMPAD1" means this, not "1".
        if (upper.StartsWith("NUM") && !upper.StartsWith("NUMPAD")) {
            upper = "NUMPAD" + upper[3..];
        }
        if (upper.StartsWith("NUMPAD") && upper.Length == 7 && upper[6] is >= '0' and <= '9') {
            return 0x60 + (upper[6] - '0');
        }

        return upper switch {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "CAPSLOCK" => 0x14,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            "LEFTSHIFT" or "LSHIFT" => 0xA0,
            "RIGHTSHIFT" or "RSHIFT" => 0xA1,
            "LEFTCONTROL" or "LCONTROL" or "LEFTCTRL" or "LCTRL" => 0xA2,
            "RIGHTCONTROL" or "RCONTROL" or "RIGHTCTRL" or "RCTRL" => 0xA3,
            "LEFTALT" or "LALT" => 0xA4,
            "RIGHTALT" or "RALT" => 0xA5,
            "MOUSE4" or "XBUTTON1" => 0x05,
            "MOUSE5" or "XBUTTON2" => 0x06,
            _ => throw new ArgumentException(
                $"Unknown key '{keyName}'. Supported: A-Z, 0-9, Num0-Num9, F1-F12, Space, Tab, Escape, modifier keys, Mouse4/Mouse5")
        };
    }

    public void SetSuppressed(bool suppressed) {
        _suppressed = suppressed;
    }

    public void Dispose() {
        _cts?.Cancel();
    }
}
