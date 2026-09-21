using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;

namespace MabiLifeAssistant;

/// <summary>Detects activity from the compass found in the game frame.</summary>
internal sealed class GameMotionDetector
{
    private const int GridWidth = 48;
    private const int GridHeight = 40;
    private const int SignificantGrayDifference = 34;
    private const double SignificantChangedFraction = 0.16;
    private const double MotionInset = 0.14;
    private const double MotionDiskRadius = 0.46;
    private byte[]? _previous;
    private Rect? _trackedBounds;
    private int _motionVotes;

    /// <summary>
    /// Detects the in-game working indicator: a green circular button with a
    /// white square in the center. While it is visible, the game is already
    /// performing the selected life-skill action and the assistant must not
    /// open the guide or start another round.
    /// </summary>
    public bool IsWorkIndicatorActive(BitmapSource source, Rect? candidateBounds)
    {
        if (candidateBounds is not { } bounds)
            return false;

        var region = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var centerX = Math.Clamp((int)Math.Round(bounds.X + bounds.Width / 2d), 0, region.PixelWidth - 1);
        var centerY = Math.Clamp((int)Math.Round(bounds.Y + bounds.Height / 2d), 0, region.PixelHeight - 1);
        var radius = Math.Max(12, (int)Math.Round(Math.Min(bounds.Width, bounds.Height) * 0.44));
        var left = Math.Max(0, centerX - radius);
        var top = Math.Max(0, centerY - radius);
        var right = Math.Min(region.PixelWidth, centerX + radius + 1);
        var bottom = Math.Min(region.PixelHeight, centerY + radius + 1);
        var rowBytes = (right - left) * 4;
        var row = new byte[rowBytes];
        var diskPixels = 0;
        var greenPixels = 0;
        var whitePixels = 0;
        var whiteHalfSize = Math.Max(6, (int)(radius * 0.38));

        for (var y = top; y < bottom; y++)
        {
            region.CopyPixels(new System.Windows.Int32Rect(left, y, right - left, 1), row, rowBytes, 0);
            for (var x = left; x < right; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy > radius * radius)
                    continue;

                diskPixels++;
                var pixelIndex = (x - left) * 4;
                var blue = row[pixelIndex];
                var green = row[pixelIndex + 1];
                var red = row[pixelIndex + 2];
                if (green >= 145 && green - red >= 35 && green - blue >= 8)
                    greenPixels++;

                if (Math.Abs(dx) <= whiteHalfSize && Math.Abs(dy) <= whiteHalfSize
                    && red >= 205 && green >= 205 && blue >= 205)
                    whitePixels++;
            }
        }

        if (diskPixels == 0)
            return false;

        var greenFraction = greenPixels / (double)diskPixels;
        var whiteRegionSize = whiteHalfSize * 2 + 1;
        var whiteFraction = whitePixels / (double)(whiteRegionSize * whiteRegionSize);
        return greenFraction >= 0.48 && whiteFraction >= 0.24;
    }

    public bool HasSignificantMotion(BitmapSource source, Rect? candidateBounds)
    {
        if (candidateBounds is not { } bounds)
        {
            _previous = null;
            _trackedBounds = null;
            _motionVotes = 0;
            return false;
        }

        bounds = TrackStableBounds(bounds);

        var region = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        // The outer green ring has a gentle animated glow even when the player
        // is standing still. Compare the stable inner face instead, while
        // keeping the needle and center visible for real camera movement.
        var fullLeft = Math.Clamp((int)Math.Round(bounds.X), 0, region.PixelWidth - 1);
        var fullTop = Math.Clamp((int)Math.Round(bounds.Y), 0, region.PixelHeight - 1);
        var fullWidth = Math.Clamp((int)Math.Round(bounds.Width), 1, region.PixelWidth - fullLeft);
        var fullHeight = Math.Clamp((int)Math.Round(bounds.Height), 1, region.PixelHeight - fullTop);
        var insetX = Math.Max(1, (int)Math.Round(fullWidth * MotionInset));
        var insetY = Math.Max(1, (int)Math.Round(fullHeight * MotionInset));
        var left = fullLeft + insetX;
        var top = fullTop + insetY;
        var width = Math.Max(1, fullWidth - insetX * 2);
        var height = Math.Max(1, fullHeight - insetY * 2);
        var rowBytes = width * 4;
        var sampleRow = new byte[rowBytes];
        var current = new byte[GridWidth * GridHeight];

        var comparable = 0;
        var changed = 0;
        for (var y = 0; y < GridHeight; y++)
        {
            var sourceY = Math.Min(region.PixelHeight - 1, top + (int)((y + 0.5) * height / GridHeight));
            region.CopyPixels(new System.Windows.Int32Rect(left, sourceY, width, 1), sampleRow, rowBytes, 0);
            for (var x = 0; x < GridWidth; x++)
            {
                var normalizedX = (x + 0.5) / GridWidth - 0.5;
                var normalizedY = (y + 0.5) / GridHeight - 0.5;
                if (normalizedX * normalizedX + normalizedY * normalizedY > MotionDiskRadius * MotionDiskRadius)
                    continue;

                var sourceX = Math.Min(width - 1, (int)((x + 0.5) * width / GridWidth));
                var index = sourceX * 4;
                var currentIndex = y * GridWidth + x;
                current[currentIndex] = (byte)((sampleRow[index + 2] * 30 + sampleRow[index + 1] * 59 + sampleRow[index] * 11) / 100);
                comparable++;
                if (_previous is not null && Math.Abs(current[currentIndex] - _previous[currentIndex]) >= SignificantGrayDifference)
                    changed++;
            }
        }

        if (_previous is null)
        {
            _previous = current;
            _motionVotes = 0;
            return false;
        }

        _previous = current;
        // Ignore tiny HUD glow changes; require a broad change in the stable
        // inner compass face so the idle pulse around the button does not
        // reset the timer. A single noisy frame is not enough to reset it.
        var significantChange = comparable > 0 && changed >= comparable * SignificantChangedFraction;
        _motionVotes = significantChange
            ? Math.Min(2, _motionVotes + 1)
            : Math.Max(0, _motionVotes - 1);
        return _motionVotes >= 2;
    }

    public void Reset()
    {
        _previous = null;
        _trackedBounds = null;
        _motionVotes = 0;
    }

    private Rect TrackStableBounds(Rect candidate)
    {
        if (_trackedBounds is not { } tracked)
        {
            _trackedBounds = candidate;
            return candidate;
        }

        var trackedCenter = new System.Windows.Point(tracked.X + tracked.Width / 2d, tracked.Y + tracked.Height / 2d);
        var candidateCenter = new System.Windows.Point(candidate.X + candidate.Width / 2d, candidate.Y + candidate.Height / 2d);
        var centerShift = Math.Sqrt(
            Math.Pow(candidateCenter.X - trackedCenter.X, 2) +
            Math.Pow(candidateCenter.Y - trackedCenter.Y, 2));
        var sizeChange = Math.Max(
            Math.Abs(candidate.Width - tracked.Width) / Math.Max(1d, tracked.Width),
            Math.Abs(candidate.Height - tracked.Height) / Math.Max(1d, tracked.Height));
        var allowedShift = Math.Max(8d, Math.Min(tracked.Width, tracked.Height) * 0.12);

        if (centerShift > allowedShift || sizeChange > 0.20)
        {
            _trackedBounds = candidate;
            _previous = null;
            _motionVotes = 0;
            return candidate;
        }

        return tracked;
    }
}
