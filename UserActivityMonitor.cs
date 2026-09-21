using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MabiLifeAssistant;

internal sealed class UserActivityMonitor : IDisposable
{
    private const int HookKeyboardLowLevel = 13;
    private const int HookMouseLowLevel = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const uint InjectedKeyboard = 0x10;
    private const uint InjectedMouse = 0x01;
    private const int VirtualKeyEscape = 0x1B;
    private const int DefaultStartHotkey = 0x21; // PageUp
    private const int DefaultStopHotkey = 0x22; // PageDown

    private readonly Action _emergencyStop;
    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private readonly IntPtr _keyboardHook;
    private readonly IntPtr _mouseHook;
    private long _lastInputMilliseconds;
    private int _startHotkey = DefaultStartHotkey;
    private int _stopHotkey = DefaultStopHotkey;
    private bool _disposed;

    public event Action<uint>? HotkeyPressed;

    public UserActivityMonitor(Action emergencyStop)
    {
        _emergencyStop = emergencyStop;
        _lastInputMilliseconds = Environment.TickCount64;
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _keyboardHook = SetWindowsHookEx(HookKeyboardLowLevel, _keyboardCallback, IntPtr.Zero, 0);
        _mouseHook = SetWindowsHookEx(HookMouseLowLevel, _mouseCallback, IntPtr.Zero, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("無法啟用鍵盤／滑鼠閒置偵測。請重新啟動程式後再試。");
        }
    }

    public long LastUserInputMilliseconds => Interlocked.Read(ref _lastInputMilliseconds);

    public void ConfigureHotkeys(int startHotkey, int stopHotkey)
    {
        Volatile.Write(ref _startHotkey, IsUsableHotkey(startHotkey) ? startHotkey : DefaultStartHotkey);
        Volatile.Write(ref _stopHotkey, IsUsableHotkey(stopHotkey) ? stopHotkey : DefaultStopHotkey);
    }

    public static void SuppressProgrammaticMouseMove() =>
        ProgrammaticMoveSuppression.SetUntil(Environment.TickCount64 + 300);

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardHookData>(data);
            if ((info.Flags & InjectedKeyboard) == 0)
            {
                MarkUserInput();
                var keyMessage = message.ToInt32();
                if (info.VirtualKey == VirtualKeyEscape && keyMessage == WmKeyDown)
                    _emergencyStop();
                if (keyMessage is WmKeyDown or WmSysKeyDown)
                {
                    var virtualKey = (int)info.VirtualKey;
                    if (virtualKey == Volatile.Read(ref _startHotkey)
                        || virtualKey == Volatile.Read(ref _stopHotkey))
                    {
                        HotkeyPressed?.Invoke(info.VirtualKey);
                    }
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var messageId = message.ToInt32();
            var info = Marshal.PtrToStructure<MouseHookData>(data);
            if ((info.Flags & InjectedMouse) == 0
                && (messageId != WmMouseMove || Environment.TickCount64 > ProgrammaticMoveSuppression.Until))
            {
                if (messageId is WmMouseMove or WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmMouseWheel)
                    MarkUserInput();
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void MarkUserInput() => Interlocked.Exchange(ref _lastInputMilliseconds, Environment.TickCount64);

    private static bool IsUsableHotkey(int virtualKey) => virtualKey is > 0 and <= 0xFE
        && virtualKey != VirtualKeyEscape;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_keyboardHook != IntPtr.Zero)
            _ = UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero)
            _ = UnhookWindowsHookEx(_mouseHook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData { public NativePoint Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    private static class ProgrammaticMoveSuppression
    {
        private static long _until;
        public static long Until => Interlocked.Read(ref _until);
        public static void SetUntil(long value) => Interlocked.Exchange(ref _until, value);
    }
}
