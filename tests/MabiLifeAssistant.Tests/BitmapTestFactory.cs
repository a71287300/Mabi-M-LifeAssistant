using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiLifeAssistant.Tests;

internal static class BitmapTestFactory
{
    public static BitmapSource Create(int width, int height, Func<int, int, Color> colorAt)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = colorAt(x, y);
            var index = (y * width + x) * 4;
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        source.Freeze();
        return source;
    }
}
