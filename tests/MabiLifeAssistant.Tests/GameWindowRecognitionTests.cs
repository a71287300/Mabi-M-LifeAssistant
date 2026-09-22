using System.Windows.Media;

namespace MabiLifeAssistant.Tests;

public sealed class GameWindowRecognitionTests
{
    private static readonly Color Background = Color.FromRgb(18, 24, 30);
    private static readonly Color Green = Color.FromRgb(40, 220, 70);

    [Fact]
    public void Finds_all_eight_green_skill_buttons()
    {
        const int width = 1000;
        const int height = 800;
        var positions = new (int Row, int Column)[]
        {
            (0, 0), (1, 0), (0, 1), (2, 0), (3, 0), (2, 1), (1, 1), (3, 1)
        };
        var source = BitmapTestFactory.Create(width, height, (x, y) =>
        {
            foreach (var (row, column) in positions)
            {
                var centerX = (int)(width * (column == 0 ? 0.497 : 0.798));
                var centerY = (int)(height * (0.335 + row * 0.103));
                if (Math.Abs(x - centerX) <= 20 && Math.Abs(y - centerY) <= 10)
                    return Green;
            }

            return Background;
        });

        var bounds = MabiLifeAssistant.GameWindowService.FindProceedButtonBounds(source);

        Assert.Equal(8, bounds.Count(point => point.HasValue));
    }

    [Fact]
    public void Rejects_a_guide_frame_without_green_buttons()
    {
        var source = BitmapTestFactory.Create(1000, 800, (_, _) => Background);

        var bounds = MabiLifeAssistant.GameWindowService.FindProceedButtonBounds(source);

        Assert.All(bounds, point => Assert.Null(point));
    }

    [Fact]
    public void Finds_the_wide_green_confirmation_button()
    {
        var source = BitmapTestFactory.Create(1000, 800, (x, y) =>
            x >= 560 && x <= 690 && y >= 700 && y <= 742 ? Green : Background);

        var point = MabiLifeAssistant.GameWindowService.FindConfirmationButton(source);

        Assert.NotNull(point);
        Assert.InRange(point!.Value.X, 620, 630);
        Assert.InRange(point.Value.Y, 718, 724);
    }
}
