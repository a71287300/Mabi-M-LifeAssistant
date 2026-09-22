using System.Windows;
using System.Windows.Media.Imaging;
using WpfPoint = System.Windows.Point;

namespace MabiLifeAssistant;

/// <summary>
/// Boundary for desktop window capture and input. The automation workflow uses
/// this interface so its safety decisions can be tested without touching Win32.
/// </summary>
internal interface IGameWindowService
{
    bool IsUsable(IntPtr handle);
    BitmapSource CaptureClient(IntPtr handle);
    bool Focus(IntPtr handle);
    void Click(IntPtr handle, WpfPoint point);
    IReadOnlyList<WpfPoint?> FindProceedButtons(BitmapSource source);
    WpfPoint? FindConfirmationButton(BitmapSource source);
}

internal sealed class NativeGameWindowService : IGameWindowService
{
    public bool IsUsable(IntPtr handle) => GameWindowService.IsUsable(handle);

    public BitmapSource CaptureClient(IntPtr handle) => GameWindowService.CaptureClient(handle);

    public bool Focus(IntPtr handle) => GameWindowService.Focus(handle);

    public void Click(IntPtr handle, WpfPoint point) => GameWindowService.Click(handle, point);

    public IReadOnlyList<WpfPoint?> FindProceedButtons(BitmapSource source) => GameWindowService.FindProceedButtons(source);

    public WpfPoint? FindConfirmationButton(BitmapSource source) => GameWindowService.FindConfirmationButton(source);
}
