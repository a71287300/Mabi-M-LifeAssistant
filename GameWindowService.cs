using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace MabiLifeAssistant;

internal static class GameWindowService
{
    private const uint PrintWindowClientOnly = 0x00000001;
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int ShowRestore = 9;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const ushort VirtualKeyC = 0x43;

    public static IReadOnlyList<WindowChoice> FindVisibleWindows(int ownProcessId)
    {
        var windows = new List<WindowChoice>();
        EnumWindows((handle, parameter) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero)
                return true;

            var length = GetWindowTextLength(handle);
            if (length <= 0)
                return true;

            var title = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(handle, title, title.Capacity);
            var windowTitle = title.ToString().Trim();
            if (string.IsNullOrWhiteSpace(windowTitle))
                return true;

            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == ownProcessId)
                return true;

            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                processName = "應用程式";
            }

            windows.Add(new WindowChoice
            {
                Handle = handle,
                Title = windowTitle,
                ProcessName = processName,
                DisplayName = $"{windowTitle}   ·   {processName}"
            });
            return true;
        }, IntPtr.Zero);

        return windows.OrderByDescending(w => w.ProcessName.Contains("mabinogi", StringComparison.OrdinalIgnoreCase)
                                               || w.Title.Contains("瑪奇", StringComparison.OrdinalIgnoreCase)
                                               || w.Title.Contains("Mabinogi", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && IsWindow(handle) && !IsIconic(handle);

    public static System.Windows.Size GetClientSize(IntPtr handle)
    {
        if (!GetClientRect(handle, out var rect))
            throw new InvalidOperationException("無法讀取遊戲視窗尺寸。");
        return new System.Windows.Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static bool Focus(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
            return false;
        if (IsIconic(handle))
            _ = ShowWindow(handle, ShowRestore);
        _ = SetForegroundWindow(handle);
        _ = BringWindowToTop(handle);
        Thread.Sleep(120);
        return GetForegroundWindow() == handle;
    }

    public static BitmapSource CaptureClient(IntPtr handle)
    {
        if (!IsUsable(handle))
            throw new InvalidOperationException("遊戲視窗已關閉或最小化。");
        if (!GetClientRect(handle, out var rect))
            throw new InvalidOperationException("無法讀取遊戲視窗尺寸。");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 16 || height < 16 || width > 8192 || height > 8192)
            throw new InvalidOperationException("遊戲視窗尺寸不正確。");

        var origin = new NativePoint { X = rect.Left, Y = rect.Top };
        _ = ClientToScreen(handle, ref origin);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            bool printed;
            try
            {
                printed = PrintWindow(handle, hdc, PrintWindowClientOnly | PrintWindowRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (!printed || IsAllBlack(bitmap))
                graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DeleteObject(hBitmap);
        }
    }

    public static void PressC(IntPtr handle)
    {
        if (GetForegroundWindow() != handle)
            throw new InvalidOperationException("選取的遊戲視窗不在前景，已取消按鍵以免操作到其他程式。");
        SendKey(VirtualKeyC);
    }

    public static void Click(IntPtr handle, System.Windows.Point point)
    {
        if (GetForegroundWindow() != handle || !GetClientRect(handle, out var rect))
            throw new InvalidOperationException("選取的遊戲視窗不在前景，已取消點擊以免操作到其他程式。");

        var clientWidth = rect.Right - rect.Left;
        var clientHeight = rect.Bottom - rect.Top;
        if (clientWidth <= 0 || clientHeight <= 0)
            throw new InvalidOperationException("遊戲視窗目前沒有可點擊的畫面。");
        var clientPoint = new NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        };
        if (clientPoint.X < 0 || clientPoint.X >= clientWidth || clientPoint.Y < 0 || clientPoint.Y >= clientHeight)
            throw new InvalidOperationException("辨識到的位置超出遊戲視窗，已取消點擊。");
        if (!ClientToScreen(handle, ref clientPoint))
            throw new InvalidOperationException("無法定位遊戲中的選項。");

        UserActivityMonitor.SuppressProgrammaticMouseMove();
        if (!SetCursorPos(clientPoint.X, clientPoint.Y))
            throw new InvalidOperationException("無法將游標移到遊戲選項。");

        SendMouseButton(MouseLeftDown);
        Thread.Sleep(35);
        SendMouseButton(MouseLeftUp);
    }

    public static IReadOnlyList<System.Windows.Point?> FindProceedButtons(BitmapSource source)
    {
        return FindProceedButtonBounds(source)
            .Select(bounds => bounds is { } value
                ? new System.Windows.Point(value.X + value.Width / 2, value.Y + value.Height / 2)
                : (System.Windows.Point?)null)
            .ToArray();
    }

    public static IReadOnlyList<System.Windows.Rect?> FindSkillCardBounds(BitmapSource source)
    {
        var buttonBounds = FindProceedButtonBounds(source);
        var positions = new (int Row, int Column)[]
        {
            (0, 0), (1, 0), (0, 1), (2, 0), (3, 0), (2, 1), (1, 1), (3, 1)
        };
        var results = new System.Windows.Rect?[buttonBounds.Count];
        for (var index = 0; index < buttonBounds.Count; index++)
        {
            if (buttonBounds[index] is null)
                continue;

            var (row, column) = positions[index];
            var cardLeft = source.PixelWidth * (column == 0 ? 0.225 : 0.53);
            var cardRight = source.PixelWidth * (column == 0 ? 0.515 : 0.86);
            var cardCenterY = source.PixelHeight * (0.335 + row * 0.103);
            var cardTop = Math.Max(0, cardCenterY - source.PixelHeight * 0.048);
            var cardBottom = Math.Min(source.PixelHeight, cardCenterY + source.PixelHeight * 0.048);
            results[index] = new System.Windows.Rect(cardLeft, cardTop, cardRight - cardLeft, cardBottom - cardTop);
        }

        return results;
    }

    public static IReadOnlyList<System.Windows.Rect?> FindProceedButtonBounds(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
        var positions = new (int Row, int Column)[]
        {
            (0, 0), (1, 0), (0, 1), (2, 0), (3, 0), (2, 1), (1, 1), (3, 1)
        };
        var results = new System.Windows.Rect?[positions.Length];

        for (var index = 0; index < positions.Length; index++)
        {
            var (row, column) = positions[index];
            var centerX = source.PixelWidth * (column == 0 ? 0.497 : 0.798);
            var centerY = source.PixelHeight * (0.335 + row * 0.103);
            var halfWidth = Math.Max(1, (int)(source.PixelWidth * 0.03));
            var halfHeight = Math.Max(1, (int)(source.PixelHeight * 0.025));
            var left = Math.Clamp((int)centerX - halfWidth, 0, source.PixelWidth - 1);
            var top = Math.Clamp((int)centerY - halfHeight, 0, source.PixelHeight - 1);
            var right = Math.Clamp((int)centerX + halfWidth, left + 1, source.PixelWidth);
            var bottom = Math.Clamp((int)centerY + halfHeight, top + 1, source.PixelHeight);

            var minX = right;
            var maxX = left;
            var minY = bottom;
            var maxY = top;
            var greenPixels = 0;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var pixelIndex = (y * source.PixelWidth + x) * 4;
                var blue = pixels[pixelIndex];
                var green = pixels[pixelIndex + 1];
                var red = pixels[pixelIndex + 2];
                if (green < 100 || green - red < 45 || green - blue < 15)
                    continue;

                greenPixels++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            var buttonWidth = maxX - minX + 1;
            var buttonHeight = maxY - minY + 1;
            var regionArea = (right - left) * (bottom - top);
            if (greenPixels < regionArea * 0.05
                || buttonWidth < source.PixelWidth * 0.02
                || buttonHeight < source.PixelHeight * 0.02
                || buttonWidth > source.PixelWidth * 0.08
                || buttonHeight > source.PixelHeight * 0.07
                || buttonWidth / (double)buttonHeight < 1.2)
                continue;

            results[index] = new System.Windows.Rect(minX, minY, buttonWidth, buttonHeight);
        }

        return results;
    }

    public static System.Windows.Point? FindConfirmationButton(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0);

        // The game confirmation dialog is centered near the bottom of the guide.
        // Restricting the search to this dialog area avoids the green buttons in
        // the cards behind the dimmed modal.
        var left = Math.Clamp((int)(source.PixelWidth * 0.45), 0, source.PixelWidth - 1);
        var top = Math.Clamp((int)(source.PixelHeight * 0.83), 0, source.PixelHeight - 1);
        var right = Math.Clamp((int)(source.PixelWidth * 0.70), left + 1, source.PixelWidth);
        var bottom = Math.Clamp((int)(source.PixelHeight * 0.98), top + 1, source.PixelHeight);
        var minX = right;
        var maxX = left;
        var minY = bottom;
        var maxY = top;
        var greenPixels = 0;

        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var pixelIndex = (y * source.PixelWidth + x) * 4;
            var blue = pixels[pixelIndex];
            var green = pixels[pixelIndex + 1];
            var red = pixels[pixelIndex + 2];
            if (green < 100 || green - red < 45 || green - blue < 15)
                continue;

            greenPixels++;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        var regionArea = (right - left) * (bottom - top);
        if (greenPixels < regionArea * 0.025
            || width < source.PixelWidth * 0.07
            || height < source.PixelHeight * 0.03
            || width > source.PixelWidth * 0.22
            || height > source.PixelHeight * 0.10
            || width / (double)height < 2.0)
            return null;

        return new System.Windows.Point((minX + maxX) / 2d, (minY + maxY) / 2d);
    }

    private static bool IsAllBlack(Bitmap bitmap)
    {
        var xs = new[] { bitmap.Width / 4, bitmap.Width / 2, bitmap.Width * 3 / 4 };
        var ys = new[] { bitmap.Height / 4, bitmap.Height / 2, bitmap.Height * 3 / 4 };
        foreach (var x in xs)
        foreach (var y in ys)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.R > 3 || color.G > 3 || color.B > 3)
                return false;
        }
        return true;
    }

    private static void SendKey(ushort virtualKey)
    {
        var down = new NativeInput { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey } } };
        var up = new NativeInput { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = KeyUp } } };
        if (SendInput(1, new[] { down }, Marshal.SizeOf<NativeInput>()) != 1 || SendInput(1, new[] { up }, Marshal.SizeOf<NativeInput>()) != 1)
            throw new InvalidOperationException("無法向遊戲送出按鍵。");
    }

    private static void SendMouseButton(uint flags)
    {
        var input = new NativeInput { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>()) != 1)
            throw new InvalidOperationException("無法向遊戲送出滑鼠操作。");
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
