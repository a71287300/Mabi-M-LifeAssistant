using System.Windows;
using System.Windows.Media;

namespace MabiLifeAssistant.Tests;

public sealed class GameMotionDetectorTests
{
    private static readonly Rect IndicatorBounds = new(20, 20, 60, 60);

    [Fact]
    public void Recognizes_the_green_work_indicator_with_a_white_pause_square()
    {
        var source = CreateIndicator(withWhiteSquare: true);
        var detector = new MabiLifeAssistant.GameMotionDetector();

        Assert.True(detector.IsWorkIndicatorActive(source, IndicatorBounds));
    }

    [Fact]
    public void Does_not_treat_a_green_circle_without_the_pause_square_as_working()
    {
        var source = CreateIndicator(withWhiteSquare: false);
        var detector = new MabiLifeAssistant.GameMotionDetector();

        Assert.False(detector.IsWorkIndicatorActive(source, IndicatorBounds));
    }

    [Fact]
    public void Requires_two_significant_frame_changes_before_reporting_motion()
    {
        var detector = new MabiLifeAssistant.GameMotionDetector();
        var first = CreateMotionFrame(Color.FromRgb(50, 60, 80));
        var second = CreateMotionFrame(Color.FromRgb(220, 210, 170));

        Assert.False(detector.HasSignificantMotion(first, IndicatorBounds));
        Assert.False(detector.HasSignificantMotion(second, IndicatorBounds));
        Assert.True(detector.HasSignificantMotion(first, IndicatorBounds));
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateIndicator(bool withWhiteSquare)
    {
        return BitmapTestFactory.Create(100, 100, (x, y) =>
        {
            var distance = Math.Sqrt(Math.Pow(x - 50, 2) + Math.Pow(y - 50, 2));
            if (distance <= 26)
            {
                if (withWhiteSquare && Math.Abs(x - 50) <= 9 && Math.Abs(y - 50) <= 9)
                    return Colors.White;
                return Color.FromRgb(60, 210, 80);
            }

            return Color.FromRgb(20, 25, 30);
        });
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateMotionFrame(Color innerColor)
    {
        return BitmapTestFactory.Create(100, 100, (x, y) =>
        {
            var distance = Math.Sqrt(Math.Pow(x - 50, 2) + Math.Pow(y - 50, 2));
            return distance <= 28 ? innerColor : Color.FromRgb(20, 25, 30);
        });
    }
}
